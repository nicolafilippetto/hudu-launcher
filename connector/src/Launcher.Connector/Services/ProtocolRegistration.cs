using Launcher.Connector.Core;
using Microsoft.Win32;

namespace Launcher.Connector.Services;

/// <summary>
/// Registra il protocollo <c>hudulauncher://</c> per l'utente corrente (HKCU, nessun diritto
/// amministrativo). Si ripete a ogni avvio, così il percorso resta giusto dopo gli aggiornamenti.
/// </summary>
public static class ProtocolRegistration
{
    private const string Root = @"Software\Classes\" + LaunchRequest.Scheme;

    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            using var key = Registry.CurrentUser.CreateSubKey(Root);
            key.SetValue("", "URL:Hudu Launcher");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue("", $"\"{exe}\",0");
            }

            using var command = key.CreateSubKey(@"shell\open\command");
            var value = $"\"{exe}\" \"%1\"";
            if (command.GetValue("") as string != value)
            {
                command.SetValue("", value);
                Log.Info("Protocollo hudulauncher:// registrato per " + exe);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Registrazione del protocollo non riuscita", ex);
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Rimozione del protocollo non riuscita", ex);
        }
    }
}
