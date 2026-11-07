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

        WebViewOverrides.Clear();
        var request = LaunchRequest.FromArgs(args);

        // Se il Launcher è già aperto, gli si passa la richiesta e si esce subito.
        if (!SingleInstance.TryBecomePrimary())
        {
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
        var app = new App(request);
        app.InitializeComponent();
        return app.Run();
    }
}
