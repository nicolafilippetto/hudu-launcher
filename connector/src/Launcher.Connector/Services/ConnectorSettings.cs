using System.Reflection;
using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>
/// Impostazioni del Launcher. Si leggono dal registro (prima i criteri distribuiti da Intune,
/// poi le preferenze dell'utente); l'indirizzo del portale ha un valore predefinito di compilazione.
/// </summary>
public static class ConnectorSettings
{
    private const string PolicyKey = @"Software\Policies\HuduLauncher";
    private const string UserKey = @"Software\HuduLauncher";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuduLauncherData");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts.json");

    public static string WebViewDataDirectory => Path.Combine(DataDirectory, "WebView2");

    /// <summary>Profili del browser integrato delle pagine web: uno per scheda, o per asset se si ricorda l'accesso.</summary>
    public static string WebProfilesDirectory => Path.Combine(DataDirectory, "WebProfiles");

    /// <summary>Cancella i profili web temporanei rimasti da una chiusura improvvisa.</summary>
    public static void CleanupWebProfiles()
    {
        if (!Directory.Exists(WebProfilesDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(WebProfilesDirectory, "tmp-*"))
        {
            TryDeleteDirectory(directory);
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // ancora in uso: si riprova più tardi o al prossimo avvio
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string Version { get; } =
        typeof(ConnectorSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Indirizzo del portale (es. https://launcher.example.com).</summary>
    public static string? PortalUrl => Read("PortalUrl") ?? DefaultPortalUrl;

    /// <summary>true se l'indirizzo del portale è imposto da un criterio (Intune/GPO) e non è modificabile.</summary>
    public static bool PortalUrlFromPolicy => ReadFrom("PortalUrl", policyOnly: true) is not null;

    /// <summary>Salva l'indirizzo del portale nelle preferenze dell'utente (HKCU).</summary>
    public static void SavePortalUrl(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        key.SetValue("PortalUrl", url, RegistryValueKind.String);
        Log.Info("Indirizzo del portale impostato: " + url);
    }

    /// <summary>Barra dei comandi delle sessioni ridotta alle sole icone (preferenza dell'utente).</summary>
    public static bool CompactToolbar
    {
        get => Read("CompactToolbar") == "1";
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserKey);
            key.SetValue("CompactToolbar", value ? "1" : "0", RegistryValueKind.String);
        }
    }

    /// <summary>Il tecnico ha già visto l'avviso che il Launcher, chiuso, resta nell'area di notifica.</summary>
    public static bool TrayHintShown
    {
        get => Read("TrayHintShown") == "1";
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserKey);
            key.SetValue("TrayHintShown", value ? "1" : "0", RegistryValueKind.String);
        }
    }

    /// <summary>Criterio dell'amministratore per la modalità in background: true, false o null (sceglie il tecnico).</summary>
    public static bool? AutoStartPolicy => ParseFlag(ReadFrom("AutoStart", policyOnly: true));

    /// <summary>Criterio dell'amministratore o, se non c'è, scelta del tecnico; null se nessuno ha scelto.</summary>
    public static bool? AutoStartChoice => ParseFlag(Read("AutoStart"));

    public static void SaveAutoStartChoice(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        key.SetValue("AutoStart", enabled ? "1" : "0", RegistryValueKind.String);
    }

    private static bool? ParseFlag(string? value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>Percorso del client RustDesk, se non è in una posizione standard.</summary>
    public static string? RustDeskPath => Read("RustDeskPath");

    /// <summary>winbox.exe di MikroTik: criterio dell'amministratore o scelta del tecnico alla prima connessione.</summary>
    public static string? WinboxPath => Read("WinboxPath");

    public static bool WinboxPathFromPolicy => ReadFrom("WinboxPath", policyOnly: true) is not null;

    public static void SaveWinboxPath(string? path) => SaveUserValue("WinboxPath", path);

    /// <summary>tnsnames.ora del tecnico in cui il Launcher aggiunge le voci HUDULAUNCHER_n.</summary>
    public static string? TnsNamesPath => Read("TnsNamesPath");

    public static bool TnsNamesPathFromPolicy => ReadFrom("TnsNamesPath", policyOnly: true) is not null;

    public static void SaveTnsNamesPath(string? path) => SaveUserValue("TnsNamesPath", path);

    /// <summary>Posizione, dimensione e stato della finestra principale (vedi <see cref="Core.WindowBounds"/>).</summary>
    public static string? WindowPlacement
    {
        get => ReadFrom("WindowPlacement", policyOnly: false);
        set => SaveUserValue("WindowPlacement", value);
    }

    /// <summary>Copia del tnsnames.ora prima della prima modifica del Launcher (per sicurezza).</summary>
    public static string TnsNamesBackupFile => Path.Combine(DataDirectory, "tnsnames.backup.ora");

    private static void SaveUserValue(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        if (string.IsNullOrEmpty(value))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static string? DefaultPortalUrl =>
        typeof(ConnectorSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DefaultPortalUrl")?.Value is { Length: > 0 } value ? value : null;

    private static string? Read(string name) => ReadFrom(name, policyOnly: false);

    private static string? ReadFrom(string name, bool policyOnly)
    {
        var sources = new List<(RegistryKey Hive, string Key)> { (Registry.CurrentUser, PolicyKey), (Registry.LocalMachine, PolicyKey) };
        if (!policyOnly)
        {
            sources.Add((Registry.CurrentUser, UserKey));
        }

        foreach (var (hive, key) in sources)
        {
            using var subKey = hive.OpenSubKey(key);
            if (subKey?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

/// <summary>Registro su file, senza segreti. Un file al giorno, conservati 14 giorni.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static string CurrentFile => Path.Combine(ConnectorSettings.LogDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConnectorSettings.LogDirectory);
                File.AppendAllText(CurrentFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // il registro non deve mai bloccare l'app
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Cleanup()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(ConnectorSettings.LogDirectory, "launcher-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
