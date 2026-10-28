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

    /// <summary>Percorso del client RustDesk, se non è in una posizione standard.</summary>
    public static string? RustDeskPath => Read("RustDeskPath");

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
