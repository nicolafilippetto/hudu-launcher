using System.Diagnostics;
using System.Text;
using System.Windows;
using Launcher.Connector.Core;
using Launcher.Connector.Sessions;
using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>
/// WinBox di MikroTik: il percorso di winbox.exe si chiede alla prima connessione (o arriva dal criterio
/// WinboxPath) e si ricorda; il Launcher lo apre con indirizzo, utente e password.
/// </summary>
public static class Winbox
{
    /// <summary>Percorso di winbox.exe; se manca lo chiede. Null se il tecnico annulla.</summary>
    public static string? Locate(Window? owner)
    {
        if (ConnectorSettings.WinboxPath is { } configured && IsExe(configured))
        {
            return configured;
        }

        if (ConnectorSettings.WinboxPathFromPolicy)
        {
            Dialogs.Show(owner, "WinBox non trovato",
                "Il percorso di WinBox impostato dall'amministratore non esiste su questo PC.", DialogKind.Warning, ConnectorSettings.WinboxPath);
            return null;
        }

        if (!Dialogs.Confirm(owner, "Dov'è WinBox?",
                "Per aprire le connessioni MikroTik il Launcher usa WinBox già presente sul PC. Indica una volta dove si trova winbox.exe " +
                "(si può cambiare in seguito dalle impostazioni del Launcher).", "Sfoglia…"))
        {
            return null;
        }

        return Browse(owner);
    }

    /// <summary>Scelta di winbox.exe dalle impostazioni o alla prima connessione; salva e restituisce il percorso.</summary>
    public static string? Browse(Window? owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona winbox.exe",
            Filter = "WinBox (winbox*.exe)|winbox*.exe|Programmi (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (ConnectorSettings.WinboxPath is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(owner) != true || !IsExe(dialog.FileName))
        {
            return null;
        }

        ConnectorSettings.SaveWinboxPath(dialog.FileName);
        Log.Info("Percorso di WinBox impostato: " + dialog.FileName);
        return dialog.FileName;
    }

    public static void Start(string exe, SessionEndpoint endpoint, CredentialInfo? credential)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        foreach (var argument in WinboxCommand.Arguments(endpoint, credential))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        // Mai la password nel registro: solo indirizzo e utente.
        Log.Info($"WinBox avviato verso {endpoint.Host}:{endpoint.Port}" + (credential?.Username is { } user ? $" come {user}" : " senza credenziale"));
    }

    private static bool IsExe(string path) =>
        File.Exists(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// tnsnames.ora del tecnico: il Launcher aggiunge in fondo un blocco con le voci HUDULAUNCHER_1, _2…
/// per le connessioni Oracle aperte e lo toglie quando si chiudono. Il resto del file resta identico,
/// byte per byte; prima della prima modifica se ne tiene una copia.
/// </summary>
public sealed class OracleTnsNames
{
    /// <summary>Latin-1 legge e riscrive qualsiasi byte così com'è: codifica e caratteri del file non cambiano.</summary>
    private static readonly Encoding Bytes = Encoding.Latin1;

    private readonly Dictionary<string, TnsEntry> _active = new(StringComparer.Ordinal);
    private bool _backedUp;

    /// <summary>Aggiunge la voce di una connessione aperta; restituisce l'alias e il file usato.</summary>
    public (string Alias, string File) Add(Window? owner, string host, int port, DatabaseInfo database)
    {
        var file = WritableFile(owner) ?? throw new InvalidOperationException(
            "Connessione Oracle annullata: serve un tnsnames.ora modificabile in cui aggiungere la voce HUDULAUNCHER.");
        var number = 1;
        while (_active.ContainsKey(TnsNames.Alias(number)))
        {
            number++;
        }

        var entry = new TnsEntry(TnsNames.Alias(number), host, port, database.Name, database.IsSid);
        TnsNames.Render(entry); // nomi non validi: errore prima di toccare il file
        _active[entry.Alias] = entry;
        try
        {
            Write(file);
        }
        catch
        {
            _active.Remove(entry.Alias);
            throw;
        }

        Log.Info($"tnsnames.ora: voce {entry.Alias} → {host}:{port} ({(entry.IsSid ? "SID" : "service")} {entry.DatabaseName}) in {file}");
        return (entry.Alias, file);
    }

    /// <summary>Connessione chiusa: la sua voce sparisce dal file.</summary>
    public void Remove(string alias)
    {
        if (!_active.Remove(alias) || ConnectorSettings.TnsNamesPath is not { } file)
        {
            return;
        }

        TryWrite(file);
        Log.Info($"tnsnames.ora: voce {alias} tolta");
    }

    /// <summary>All'avvio e all'uscita: niente voci rimaste da sessioni chiuse (o da una chiusura improvvisa).</summary>
    public void Cleanup()
    {
        _active.Clear();
        try
        {
            if (ConnectorSettings.TnsNamesPath is { } file && File.Exists(file)
                && TnsNames.ManagedAliases(Bytes.GetString(File.ReadAllBytes(file))).Count > 0)
            {
                TryWrite(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("tnsnames.ora non letto per la pulizia: " + ex.Message);
        }
    }

    /// <summary>File scelto e scrivibile; se manca o è in sola lettura lo chiede. Null se il tecnico annulla.</summary>
    public static string? WritableFile(Window? owner)
    {
        var current = ConnectorSettings.TnsNamesPath;
        if (current is not null && IsWritable(current, out _))
        {
            return current;
        }

        if (ConnectorSettings.TnsNamesPathFromPolicy)
        {
            Dialogs.Show(owner, "tnsnames.ora non modificabile",
                "Il file indicato dall'amministratore non esiste o è in sola lettura: il Launcher non può aggiungere la connessione.",
                DialogKind.Warning, current);
            return null;
        }

        var message = current is null
            ? "Per le connessioni Oracle il Launcher aggiunge in fondo al tuo tnsnames.ora una voce chiamata HUDULAUNCHER_1 " +
              "(poi _2, _3…), da scegliere nel tuo programma per il database. Indica una volta il file da usare: deve essere modificabile."
            : "Il tnsnames.ora scelto non è più modificabile (o non esiste più). Scegline un altro, oppure annulla la connessione.";
        if (!Dialogs.Confirm(owner, "Quale tnsnames.ora?", message, "Sfoglia…", "Annulla la connessione", detail: current))
        {
            return null;
        }

        return Browse(owner);
    }

    /// <summary>
    /// Scelta del file dalle impostazioni o alla connessione: si salva solo se è davvero scrivibile,
    /// altrimenti si avvisa e si propone di sceglierne un altro.
    /// </summary>
    public static string? Browse(Window? owner)
    {
        while (true)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Seleziona il tnsnames.ora",
                Filter = "tnsnames.ora|tnsnames.ora|File Oracle (*.ora)|*.ora|Tutti i file|*.*",
                CheckFileExists = true,
            };
            if (ConnectorSettings.TnsNamesPath is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }

            if (dialog.ShowDialog(owner) != true)
            {
                return null;
            }

            if (IsWritable(dialog.FileName, out var reason))
            {
                ConnectorSettings.SaveTnsNamesPath(dialog.FileName);
                Log.Info("tnsnames.ora impostato: " + dialog.FileName);
                return dialog.FileName;
            }

            Log.Warn($"tnsnames.ora non scrivibile: {dialog.FileName} ({reason})");
            if (!Dialogs.Confirm(owner, "File in sola lettura",
                    "Il Launcher non può modificare questo file, quindi non lo salva. Di solito succede con i file dentro " +
                    "C:\\Program Files: scegli un tnsnames.ora nella tua cartella (quello indicato da TNS_ADMIN, o la copia usata dal tuo programma).",
                    "Scegli un altro file", "Annulla", DialogKind.Warning, $"{dialog.FileName}\n{reason}"))
            {
                return null;
            }
        }
    }

    /// <summary>Prova davvero ad aprire il file in scrittura (attributo, permessi, file bloccato).</summary>
    public static bool IsWritable(string path, out string? reason)
    {
        try
        {
            if (!File.Exists(path))
            {
                reason = "il file non esiste";
                return false;
            }

            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                reason = "il file è segnato di sola lettura";
                return false;
            }

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
            }

            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private void Write(string file)
    {
        var original = File.ReadAllBytes(file);
        if (!_backedUp)
        {
            Directory.CreateDirectory(ConnectorSettings.DataDirectory);
            File.WriteAllBytes(ConnectorSettings.TnsNamesBackupFile, original);
            _backedUp = true;
        }

        var updated = TnsNames.Apply(Bytes.GetString(original), _active.Values);
        File.WriteAllBytes(file, Bytes.GetBytes(updated));
    }

    private void TryWrite(string file)
    {
        try
        {
            Write(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("tnsnames.ora non aggiornato: " + ex.Message);
        }
    }
}
