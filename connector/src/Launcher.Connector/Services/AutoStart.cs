using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>
/// Avvio del Launcher all'accesso a Windows, ridotto a icona (voce "Run" dell'utente, nessun diritto
/// amministrativo). Il primo avvio della giornata (caricamento dell'app e del broker 365) avviene così
/// all'accesso, e la prima connessione dal portale parte subito. Facoltativo: lo sceglie il tecnico
/// nelle impostazioni, oppure l'amministratore con il criterio AutoStart (1 o 0).
/// </summary>
public static class AutoStart
{
    public const string MinimizedArgument = "--minimized";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HuduLauncher";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info(enabled ? "Avvio all'accesso a Windows attivato" : "Avvio all'accesso a Windows disattivato");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Avvio all'accesso a Windows non modificato", ex);
        }
    }

    /// <summary>
    /// All'avvio: applica il criterio dell'amministratore, se c'è, e tiene aggiornato il percorso
    /// dell'eseguibile nella voce già presente.
    /// </summary>
    public static void Refresh()
    {
        var wanted = ConnectorSettings.AutoStartPolicy ?? IsEnabled;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var current = key?.GetValue(ValueName) as string;
        if (wanted != (current is not null) || (wanted && current != Command))
        {
            Set(wanted);
        }
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArgument}";
}
