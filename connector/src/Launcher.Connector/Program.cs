using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Velopack;

namespace Launcher.Connector;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Deve essere la prima istruzione: gestisce installazione, aggiornamento e disinstallazione.
        // Nessun AppUserModelID esplicito (stringa vuota = non impostato): i collegamenti creati dall'MSI
        // non lo hanno, e con un ID diverso Windows non abbinava la finestra al collegamento, mostrando
        // l'icona generica. Senza ID esplicito l'abbinamento avviene dal percorso dell'eseguibile.
        VelopackApp.Build()
            .SetAppUserModelId("")
            .OnFirstRun(_ => ProtocolRegistration.Register())
            .OnAfterUpdateFastCallback(_ => ProtocolRegistration.Register())
            .OnBeforeUninstallFastCallback(_ => ProtocolRegistration.Unregister())
            .Run();

        // Tempo tra la creazione del processo e il codice del Launcher: caricamento di .NET e controlli
        // dell'antivirus sugli eseguibili (lunghi soprattutto al primo avvio dopo un aggiornamento).
        var startup = StartupDelay();
        WebViewOverrides.Clear();
        var request = LaunchRequest.FromArgs(args);

        // Se il Launcher è già aperto, gli si passa la richiesta e si esce subito.
        if (!SingleInstance.TryBecomePrimary())
        {
            Log.Info($"Link passato al Launcher già aperto (avvio di questo processo: {startup})");
            if (request is not null)
            {
                SingleInstance.SendToPrimary(request.ToLink());
            }
            else
            {
                SingleInstance.SendToPrimary(SingleInstance.ActivateMessage);
            }

            return 0;
        }

        ProtocolRegistration.Register();
        Log.Info($"Avvio del processo: {startup} prima del codice del Launcher");
        var app = new App(request);
        app.InitializeComponent();
        return app.Run();
    }

    private static string StartupDelay()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return $"{(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return "non misurabile";
        }
    }
}
