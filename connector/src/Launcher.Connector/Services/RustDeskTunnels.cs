using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Launcher.Connector.Core;
using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>Tunnel attivo: la porta locale resta aperta finché non viene chiuso.</summary>
public sealed class Tunnel(RustDeskTunnels owner, string peerId, TunnelRule rule) : IAsyncDisposable
{
    private int _closed;

    public string PeerId { get; } = peerId;

    public int LocalPort => Rule.LocalPort;

    internal TunnelRule Rule { get; } = rule;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            await owner.CloseAsync(this);
        }
    }
}

public sealed class RustDeskException(string message) : Exception(message);

/// <summary>
/// Apre i tunnel TCP con il client RustDesk già installato. Il client attuale non accetta da riga di
/// comando porta locale e destinazione, ma avvia da solo le regole salvate nel file del dispositivo
/// quando si apre <c>--port-forward &lt;ID&gt;</c>. Se una finestra di tunnel verso lo stesso dispositivo
/// è già aperta, RustDesk non rilegge il file: in quel caso la finestra si chiude e si riapre con
/// tutte le regole attive (le sessioni RDP si riconnettono da sole).
/// </summary>
public sealed class RustDeskTunnels
{
    private const string PortForwardTitle = " - Port Forward - ";
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrepareWait = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<TunnelRule>> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _passwords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _touchedPeers = new(StringComparer.Ordinal);

    /// <summary>Scatta quando i tunnel vengono riaperti: le sessioni aperte possono riconnettersi.</summary>
    public event Action? Restarted;

    /// <param name="peerName">Nome del jump host nella rubrica RustDesk, per i messaggi al tecnico.</param>
    public async Task<Tunnel> OpenAsync(string peerId, string? password, string? peerName, string remoteHost, int remotePort,
        IProgress<string>? progress, string? portalClientPath, CancellationToken ct)
    {
        UsePortalClientPath(portalClientPath);
        var exe = LocateExecutable();
        var rule = new TunnelRule(FreePort(), remoteHost, remotePort);
        PeerConfigFile.Validate(rule);

        await _gate.WaitAsync(ct);
        try
        {
            var alreadyOpen = _rules.TryGetValue(peerId, out var list) && list.Count > 0;
            if (list is null)
            {
                list = [];
                _rules[peerId] = list;
            }

            // RustDesk legge le regole dal file del dispositivo nella cartella del client avviato; se il
            // file non c'è ancora (dispositivo mai usato da questo PC) lo crea il Launcher lì.
            var opening = Stopwatch.StartNew();
            var peerFile = PeerFilePath(exe, peerId);
            var knownPeer = File.Exists(peerFile);
            LogClientVersion(exe);
            list.Add(rule);
            _passwords[peerId] = password;
            WritePeerFile(exe, peerId, list);
            Log.Info($"Tunnel richiesto: 127.0.0.1:{rule.LocalPort} → {remoteHost}:{remotePort} via {peerId} " +
                     $"(file del dispositivo {(knownPeer ? "esistente" : "NON presente, creato ora")}: {peerFile})");

            if (alreadyOpen)
            {
                progress?.Report("Riapertura del tunnel RustDesk con la nuova regola…");
                await RestartAllAsync(exe, ct);
            }
            else
            {
                Launch(exe, peerId, password);
            }

            if (!await WaitListeningAsync(rule.LocalPort, FirstWait, ct))
            {
                // RustDesk non ha letto le regole: di solito non ha ancora un file valido per questo
                // dispositivo su questo PC (lo crea alla prima connessione), oppure era già aperta una
                // finestra di tunnel. Si apre una connessione classica, che fa creare il file a RustDesk
                // nella sua cartella, la si chiude e si riaprono i tunnel con le regole in quel file.
                Log.Warn($"Porta {rule.LocalPort} non aperta in {FirstWait.TotalSeconds}s: connessione RustDesk per preparare {peerId}, poi riapro i tunnel");
                LogDiagnostics(exe, peerId, rule);
                progress?.Report("RustDesk non ha aperto il tunnel: preparo il jump host con una connessione RustDesk…");
                ClosePortForwardWindows(exe);
                var prepared = await PreparePeerAsync(exe, peerId, password, peerName, knownPeer ? null : peerFile, ct);
                Log.Info(prepared is null
                    ? $"Nessun file del dispositivo creato da RustDesk per {peerId} entro {PrepareWait.TotalSeconds}s"
                    : $"File del dispositivo scritto da RustDesk: {prepared}");
                WritePeerFile(exe, peerId, list);
                progress?.Report("Riapertura del tunnel RustDesk…");
                await RestartAllAsync(exe, ct);
                if (!await WaitListeningAsync(rule.LocalPort, RetryWait, ct))
                {
                    LogDiagnostics(exe, peerId, rule);
                    list.Remove(rule);
                    WritePeerFile(exe, peerId, list);
                    ClosePortForwardWindows(exe);
                    var label = string.IsNullOrWhiteSpace(peerName) ? peerId : $"{peerName} ({peerId})";
                    Log.Warn($"Tunnel non aperto: regole scritte in {PeerFilePath(exe, peerId)} per {Path.GetFileName(exe)}");
                    throw new RustDeskException(
                        $"Il tunnel verso il jump host {label} non si apre. Apri RustDesk, cerca \"{peerName ?? peerId}\" nella rubrica, " +
                        "collegati una volta (controllo remoto), chiudi la sessione e rilancia la connessione dal portale. " +
                        "Se ancora non va, aggiorna RustDesk sul jump host.");
                }
            }

            Log.Info($"Tunnel aperto: 127.0.0.1:{rule.LocalPort} via {peerId} in {opening.Elapsed.TotalSeconds:0.0} s");
            MinimizePortForwardWindows(exe);
            return new Tunnel(this, peerId, rule);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CloseAsync(Tunnel tunnel)
    {
        await _gate.WaitAsync();
        try
        {
            if (_rules.TryGetValue(tunnel.PeerId, out var list))
            {
                list.Remove(tunnel.Rule);
                var exe = TryLocateExecutable();
                if (exe is not null)
                {
                    WritePeerFile(exe, tunnel.PeerId, list);
                    if (_rules.Values.All(r => r.Count == 0))
                    {
                        // Nessun tunnel attivo: si chiude la finestra di RustDesk.
                        ClosePortForwardWindows(exe);
                    }
                }
            }

            Log.Info($"Tunnel chiuso: porta {tunnel.LocalPort} via {tunnel.PeerId}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>All'uscita: si tolgono le regole dai file, così non ripartono aperture manuali successive.</summary>
    public void CleanupOnExit()
    {
        var exe = TryLocateExecutable();
        if (exe is null)
        {
            return;
        }

        foreach (var peer in _touchedPeers)
        {
            WritePeerFile(exe, peer, []);
        }

        if (_rules.Values.Any(r => r.Count > 0))
        {
            ClosePortForwardWindows(exe);
        }
    }

    private async Task RestartAllAsync(string exe, CancellationToken ct)
    {
        ClosePortForwardWindows(exe);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (FindPortForwardWindows(exe).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
        }

        await Task.Delay(500, ct);
        foreach (var (peer, rules) in _rules.Where(r => r.Value.Count > 0))
        {
            Launch(exe, peer, _passwords.GetValueOrDefault(peer));
            await Task.Delay(800, ct);
        }

        Restarted?.Invoke();
    }

    private static string? _loggedClientVersion;

    /// <summary>Versione del client RustDesk usato (una volta per avvio del Launcher), per il registro.</summary>
    private static void LogClientVersion(string exe)
    {
        if (_loggedClientVersion == exe)
        {
            return;
        }

        _loggedClientVersion = exe;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            Log.Info($"Client RustDesk: {Path.GetFileName(exe)} versione {info.ProductVersion ?? info.FileVersion ?? "sconosciuta"}");
        }
        catch (FileNotFoundException)
        {
        }
    }

    /// <summary>
    /// Stato di RustDesk quando il tunnel non si apre, per capire il perché dal registro: processi e
    /// finestre aperte, e se la regola del tunnel è ancora nel file del dispositivo (RustDesk potrebbe
    /// averlo riscritto con la sua copia in memoria). Nessun segreto: solo titoli e la riga port_forwards.
    /// </summary>
    private static void LogDiagnostics(string exe, string peerId, TunnelRule rule)
    {
        try
        {
            var processes = Native.ProcessIds(Path.GetFileNameWithoutExtension(exe));
            var titles = Native.WindowTitles(processes);
            Log.Info($"Diagnostica RustDesk: {processes.Count} processi, finestre: {(titles.Count == 0 ? "nessuna" : string.Join(" | ", titles))}");
            var path = PeerFilePath(exe, peerId);
            if (!File.Exists(path))
            {
                Log.Info($"Diagnostica RustDesk: file del dispositivo assente ({path})");
                return;
            }

            var toml = File.ReadAllText(path);
            Log.Info($"Diagnostica RustDesk: regola {(PeerConfigFile.Contains(toml, rule) ? "presente" : "NON presente")} nel file " +
                     $"(modificato {File.GetLastWriteTime(path):HH:mm:ss}): {PeerConfigFile.ReadPortForwards(toml) ?? "nessuna riga port_forwards"}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Diagnostica RustDesk non riuscita: " + ex.Message);
        }
    }

    /// <summary>
    /// Connessione classica al jump host (<c>--connect</c>), che fa scrivere a RustDesk il file del
    /// dispositivo nella cartella che legge davvero. Appena il file compare (o cambia) la sessione si
    /// chiude. Restituisce il file scritto, o null se non è comparso entro <see cref="PrepareWait"/>.
    /// </summary>
    private static async Task<string?> PreparePeerAsync(string exe, string peerId, string? password, string? peerName, string? launcherFile,
        CancellationToken ct)
    {
        // Il file creato poco fa dal Launcher si toglie: così si vede quando lo scrive RustDesk.
        if (launcherFile is not null && File.Exists(launcherFile))
        {
            File.Delete(launcherFile);
        }

        var candidates = PeerFileCandidates(exe, peerId);
        var before = candidates.ToDictionary(path => path, path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null);
        Launch(exe, peerId, password, "--connect");

        string? written = null;
        var deadline = DateTime.UtcNow + PrepareWait;
        while (written is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            written = candidates.FirstOrDefault(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) != before[path]);
        }

        // Si lascia a RustDesk il tempo di completare la scrittura, poi si chiude la sessione.
        await Task.Delay(1500, ct);
        var processes = Native.ProcessIds(Path.GetFileNameWithoutExtension(exe));
        var tunnels = FindPortForwardWindows(exe);
        var sessions = new[] { peerId, peerName }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => Native.FindWindows(processes, text!))
            .Distinct()
            .Where(hwnd => !tunnels.Contains(hwnd))
            .ToList();
        foreach (var hwnd in sessions)
        {
            Native.Close(hwnd);
        }

        Log.Info($"Connessione RustDesk di preparazione verso {peerId}: {sessions.Count} finestre chiuse");
        await Task.Delay(1000, ct);
        return written;
    }

    private static void Launch(string exe, string peerId, string? password, string mode = "--port-forward")
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(peerId);
        if (!string.IsNullOrEmpty(password))
        {
            start.ArgumentList.Add("--password");
            start.ArgumentList.Add(password);
        }

        using var process = Process.Start(start);
        Log.Info($"RustDesk avviato: {Path.GetFileName(exe)} {mode} {peerId} ({exe})");
    }

    private void WritePeerFile(string exe, string peerId, IReadOnlyCollection<TunnelRule> rules)
    {
        var path = PeerFilePath(exe, peerId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = PeerConfigFile.SetPortForwards(current, rules);
        var temp = path + ".launcher.tmp";
        File.WriteAllText(temp, updated, PeerConfigFile.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
        _touchedPeers.Add(peerId);
    }

    /// <summary>
    /// File del dispositivo: <c>%APPDATA%\&lt;nome client&gt;\config\peers\&lt;ID&gt;.toml</c>. Conta la cartella del
    /// client che il Launcher avvia: i client personalizzati (es. "Azienda-RustDesk-Client") usano il nome
    /// della loro cartella di installazione, che può essere diverso dal nome dell'eseguibile. Un vecchio
    /// RustDesk standard sullo stesso PC ha una sua cartella che il client personalizzato non legge.
    /// </summary>
    internal static string PeerFilePath(string exe, string peerId)
    {
        var clientDirs = ClientConfigDirs(exe);
        var fileName = peerId + ".toml";

        // 1. Il file del dispositivo nella cartella del client avviato.
        foreach (var config in clientDirs)
        {
            var path = Path.Combine(config, "peers", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 2. Il client è già stato usato su questo PC (ha la sua configurazione): il file va lì,
        //    anche se il dispositivo è nuovo.
        if (clientDirs.FirstOrDefault(Directory.Exists) is { } clientConfig)
        {
            return Path.Combine(clientConfig, "peers", fileName);
        }

        // 3. Ripiego: la cartella di RustDesk standard, se conosce già il dispositivo.
        var standard = Path.Combine(StandardConfigDir(), "peers", fileName);
        return File.Exists(standard) ? standard : Path.Combine(clientDirs[0], "peers", fileName);
    }

    /// <summary>Dove RustDesk può scrivere il file del dispositivo: cartelle del client avviato e di RustDesk standard.</summary>
    private static List<string> PeerFileCandidates(string exe, string peerId) =>
        ClientConfigDirs(exe).Append(StandardConfigDir())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(config => Path.Combine(config, "peers", peerId + ".toml"))
            .ToList();

    /// <summary>
    /// Cartelle di configurazione del client: i client personalizzati (es. "Azienda-RustDesk-Client") usano
    /// il nome della cartella di installazione, che può essere diverso dal nome dell'eseguibile.
    /// </summary>
    private static List<string> ClientConfigDirs(string exe)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new[] { Path.GetFileName(Path.GetDirectoryName(exe) ?? ""), Path.GetFileNameWithoutExtension(exe) }
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(appData, name, "config"))
            .ToList();
        return dirs.Count > 0 ? dirs : [StandardConfigDir()];
    }

    private static string StandardConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config");

    public static string LocateExecutable() =>
        TryLocateExecutable() ?? throw new RustDeskException(
            "Client RustDesk non trovato. Installalo, oppure indica il percorso nel portale (Impostazioni → RustDesk) " +
            @"o nel registro (HKCU\Software\HuduLauncher, valore RustDeskPath).");

    /// <summary>Percorso indicato dall'admin del portale, già verificato (dentro Programmi ed esistente).</summary>
    private static string? _portalClientPath;

    private static void UsePortalClientPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _portalClientPath = null;
            return;
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (RustDeskClientPath.IsAllowed(path, roots) && File.Exists(path))
        {
            _portalClientPath = path;
        }
        else
        {
            _portalClientPath = null;
            Log.Info("Percorso del client RustDesk indicato dal portale ignorato (non è un .exe esistente in Programmi): " + path);
        }
    }

    private static string? TryLocateExecutable()
    {
        // Prima il criterio del PC (Intune/registro), poi il portale, poi il rilevamento automatico.
        if (ConnectorSettings.RustDeskPath is { } configured && File.Exists(configured))
        {
            return configured;
        }

        if (_portalClientPath is { } portal && File.Exists(portal))
        {
            return portal;
        }

        // Il programma registrato per i link rustdesk:// (vale anche per i client personalizzati).
        using (var key = Registry.ClassesRoot.OpenSubKey(@"rustdesk\shell\open\command"))
        {
            if (key?.GetValue("") is string command)
            {
                var path = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(programFiles, "RustDesk", "rustdesk.exe");
        if (File.Exists(standard))
        {
            return standard;
        }

        // Client personalizzati (es. "Azienda-RustDesk-Client"): cartella ed eseguibile con "rustdesk" nel nome.
        try
        {
            return Directory.EnumerateDirectories(programFiles, "*rustdesk*")
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*rustdesk*.exe"))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<IntPtr> FindPortForwardWindows(string exe) =>
        Native.FindWindows(Native.ProcessIds(Path.GetFileNameWithoutExtension(exe)), PortForwardTitle);

    private static void MinimizePortForwardWindows(string exe)
    {
        foreach (var hwnd in FindPortForwardWindows(exe))
        {
            Native.Minimize(hwnd);
        }
    }

    private static void ClosePortForwardWindows(string exe)
    {
        foreach (var hwnd in FindPortForwardWindows(exe))
        {
            Native.Close(hwnd);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitListeningAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(l => l.Port == port && IPAddress.IsLoopback(l.Address)))
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        return false;
    }
}
