using System.Windows;
using System.Windows.Threading;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Launcher.Connector.Sessions;

namespace Launcher.Connector;

public partial class App : Application
{
    private readonly LaunchRequest? _initialRequest;
    private readonly bool _background;
    private readonly CancellationTokenSource _shutdown = new();

    /// <param name="background">Avvio in background (accesso a Windows o dopo un aggiornamento silenzioso): nessuna finestra.</param>
    public App(LaunchRequest? initialRequest, bool background = false)
    {
        _initialRequest = initialRequest;
        _background = background && initialRequest is null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info($"Avvio, versione {ConnectorSettings.Version}");

        // Link successivi dal browser, ricevuti dall'istanza già aperta. Si ascolta subito, prima di creare
        // la finestra: i messaggi arrivati durante l'avvio si consegnano appena l'avvio finisce.
        MainWindow? window = null;
        _ = SingleInstance.ListenAsync(message => Dispatcher.BeginInvoke(() =>
        {
            if (window is null)
            {
                return;
            }

            if (LaunchRequest.Parse(message) is { } request)
            {
                window.Open(request);
            }
            else
            {
                window.BringToFront();
            }
        }), _shutdown.Token);

        var workflow = new ConnectionWorkflow(new RustDeskTunnels(), new EntraAuth(), new OfflineCache(TimeProvider.System));
        window = new MainWindow(workflow);
        MainWindow = window;
        // La finestra nascosta non chiude l'app: si esce solo da Esci (icona) o chiudendo senza background.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => window.PrepareForSessionEnd();
        if (!_background)
        {
            window.Show();
        }

        window.Start(_background);

        if (_initialRequest is not null)
        {
            window.Open(_initialRequest);
        }


        // Pulizia dei registri vecchi, senza rallentare l'avvio.
        _ = Task.Run(Log.Cleanup);
        _ = Task.Run(ConnectorSettings.CleanupWebProfiles);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        Log.Info("Uscita");
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Errore non gestito", e.Exception);
        Dialogs.Show(null, "Si è verificato un errore imprevisto", e.Exception.Message, DialogKind.Error, "Dettagli nel registro: " + Log.CurrentFile);
        e.Handled = true;
    }
}
