using System.Windows;
using System.Windows.Threading;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Launcher.Connector.Sessions;

namespace Launcher.Connector;

public partial class App : Application
{
    private readonly LaunchRequest? _initialRequest;
    private readonly CancellationTokenSource _shutdown = new();

    public App(LaunchRequest? initialRequest)
    {
        _initialRequest = initialRequest;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info($"Avvio, versione {ConnectorSettings.Version}");

        var workflow = new ConnectionWorkflow(new RustDeskTunnels(), new EntraAuth(), new OfflineCache(TimeProvider.System));
        var window = new MainWindow(workflow);
        MainWindow = window;
        window.Show();

        if (_initialRequest is not null)
        {
            window.Open(_initialRequest);
        }

        // Link successivi dal browser, ricevuti dall'istanza già aperta.
        _ = SingleInstance.ListenAsync(message => Dispatcher.BeginInvoke(() =>
        {
            if (LaunchRequest.Parse(message) is { } request)
            {
                window.Open(request);
            }
            else
            {
                window.BringToFront();
            }
        }), _shutdown.Token);

        // Pulizia dei registri vecchi, senza rallentare l'avvio.
        _ = Task.Run(Log.Cleanup);
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
