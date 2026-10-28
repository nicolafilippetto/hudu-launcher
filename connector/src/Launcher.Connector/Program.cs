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
        VelopackApp.Build()
            .OnFirstRun(_ => ProtocolRegistration.Register())
            .OnAfterUpdateFastCallback(_ => ProtocolRegistration.Register())
            .OnBeforeUninstallFastCallback(_ => ProtocolRegistration.Unregister())
            .Run();

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
