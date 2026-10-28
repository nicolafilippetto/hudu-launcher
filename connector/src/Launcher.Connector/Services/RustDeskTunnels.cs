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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<TunnelRule>> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _passwords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _touchedPeers = new(StringComparer.Ordinal);

    /// <summary>Scatta quando i tunnel vengono riaperti: le sessioni aperte possono riconnettersi.</summary>
    public event Action? Restarted;

    public async Task<Tunnel> OpenAsync(string peerId, string? password, string remoteHost, int remotePort, IProgress<string>? progress,
        string? portalClientPath, CancellationToken ct)
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

            list.Add(rule);
            _passwords[peerId] = password;
            WritePeerFile(exe, peerId, list);
            Log.Info($"Tunnel richiesto: 127.0.0.1:{rule.LocalPort} → {remoteHost}:{remotePort} via {peerId}");

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
                // Probabilmente c'era già una finestra di tunnel verso questo dispositivo, aperta
                // prima del Launcher: si chiude tutto e si riapre con le regole aggiornate.
                Log.Warn($"Porta {rule.LocalPort} non aperta in {FirstWait.TotalSeconds}s, riapro i tunnel");
                progress?.Report("RustDesk non ha aperto il tunnel, nuovo tentativo…");
                await RestartAllAsync(exe, ct);
                if (!await WaitListeningAsync(rule.LocalPort, RetryWait, ct))
                {
                    list.Remove(rule);
                    WritePeerFile(exe, peerId, list);
                    throw new RustDeskException(
                        "RustDesk non ha aperto il tunnel. Controlla la finestra di RustDesk: il dispositivo è online e la " +
                        "password è corretta? Se non ti sei mai collegato a questo dispositivo da questo PC, fallo una volta " +
                        "dalla rubrica di RustDesk e riprova.");
                }
            }

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

    private static void Launch(string exe, string peerId, string? password)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("--port-forward");
        start.ArgumentList.Add(peerId);
        if (!string.IsNullOrEmpty(password))
        {
            start.ArgumentList.Add("--password");
            start.ArgumentList.Add(password);
        }

        using var process = Process.Start(start);
        Log.Info($"RustDesk avviato: --port-forward {peerId}");
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
    /// File del dispositivo: <c>%APPDATA%\&lt;nome client&gt;\config\peers\&lt;ID&gt;.toml</c>. Si preferisce
    /// la cartella che contiene già il file (il dispositivo è stato usato da questo PC).
    /// </summary>
    internal static string PeerFilePath(string exe, string peerId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fileName = peerId + ".toml";
        var candidates = new[] { Path.GetFileNameWithoutExtension(exe), "RustDesk" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(appData, name, "config", "peers"))
            .ToList();

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, fileName)))
            {
                return Path.Combine(dir, fileName);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(appData))
        {
            var path = Path.Combine(dir, "config", "peers", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(candidates.FirstOrDefault(Directory.Exists) ?? candidates[0], fileName);
    }

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
