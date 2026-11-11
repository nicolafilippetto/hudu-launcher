using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>
/// Modalità in background: il Launcher parte all'accesso a Windows senza finestra, resta
/// nell'area di notifica e prepara tutto prima che serva (accesso 365, aggiornamenti), così la prima
/// connessione dal portale parte subito. Usa la voce "Run" dell'utente, senza diritti amministrativi.
/// Attiva di serie; il tecnico può disattivarla nelle impostazioni, l'amministratore imporla con il
/// criterio AutoStart (1 o 0).
/// </summary>
public static class AutoStart
{
    public const string BackgroundArgument = "--background";

    /// <summary>Argomento delle versioni fino alla 1.4.12, ancora accettato dalle voci non aggiornate.</summary>
    public const string LegacyArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HuduLauncher";

    /// <summary>Criterio dell'amministratore, poi scelta del tecnico; di serie attiva.</summary>
    public static bool IsWanted => ConnectorSettings.AutoStartChoice ?? true;

    public static bool IsBackgroundStart(IEnumerable<string> args) =>
        args.Any(a => a.Equals(BackgroundArgument, StringComparison.OrdinalIgnoreCase) || a.Equals(LegacyArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scelta del tecnico dalle impostazioni: si ricorda e si applica subito.</summary>
    public static void Choose(bool enabled)
    {
        ConnectorSettings.SaveAutoStartChoice(enabled);
        Set(enabled);
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

            Log.Info(enabled ? "Avvio in background all'accesso a Windows attivato" : "Avvio in background all'accesso a Windows disattivato");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Avvio all'accesso a Windows non modificato", ex);
        }
    }

    /// <summary>
    /// All'avvio: allinea la voce "Run" alla scelta (criterio, tecnico o predefinita) e tiene aggiornato
    /// il percorso dell'eseguibile.
    /// </summary>
    public static void Refresh()
    {
        var wanted = IsWanted;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var current = key?.GetValue(ValueName) as string;
        if (wanted != (current is not null) || (wanted && current != Command))
        {
            Set(wanted);
        }
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";
}
