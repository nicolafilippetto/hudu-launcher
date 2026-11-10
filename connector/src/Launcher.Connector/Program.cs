using System.Diagnostics;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Velopack;

namespace Launcher.Connector;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Tempo tra la creazione del processo e il codice del Launcher: avvio di .NET e controlli
        // dell'antivirus sugli eseguibili.
        var startup = StartupDelay();

        // Velopack serve solo quando è lui ad avviare il Launcher: installazione, aggiornamento,
        // disinstallazione (argomenti --veloapp-…), primo avvio e riavvio dopo un aggiornamento.
        // In quei casi deve essere la prima cosa che si esegue. Negli avvii normali non si chiama:
        // ad ogni avvio apriva due volte il pacchetto dell'app (~100 MB), e la scansione antivirus
        // dell'archivio teneva fermo l'avvio per molti secondi, anche solo per passare un link.
        var velopackHook = args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase));
        var velopackStart = velopackHook
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELOPACK_FIRSTRUN"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELOPACK_RESTART"));

        LaunchRequest? request = null;
        if (!velopackHook)
        {
            request = LaunchRequest.FromArgs(args);

            // Se il Launcher è già aperto, gli si passa la richiesta e si esce subito.
            if (!SingleInstance.TryBecomePrimary())
            {
                Log.Info($"Link passato al Launcher già aperto (avvio di questo processo: {startup})");
                SingleInstance.SendToPrimary(request?.ToLink() ?? SingleInstance.ActivateMessage);
                return 0;
            }
        }

        if (velopackStart)
        {
            var watch = Stopwatch.StartNew();
            // Nessun AppUserModelID esplicito (stringa vuota = non impostato): i collegamenti creati
            // dall'MSI non lo hanno, e con un ID diverso Windows non abbinava la finestra al
            // collegamento, mostrando l'icona generica.
            VelopackApp.Build()
                .SetAppUserModelId("")
                .OnFirstRun(_ => ProtocolRegistration.Register())
                .OnAfterUpdateFastCallback(_ => ProtocolRegistration.Register())
                .OnBeforeUninstallFastCallback(_ => ProtocolRegistration.Unregister())
                .Run();
            Log.Info($"Avvio da Velopack (installazione o aggiornamento): {watch.ElapsedMilliseconds} ms");
        }
        else
        {
            // La pulizia dei pacchetti vecchi che faceva Velopack: solo dai nomi dei file, senza aprirli.
            _ = Task.Run(LauncherUpdater.RemoveOldPackages);
        }

        WebViewOverrides.Clear();
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
            using var process = Process.GetCurrentProcess();
            return $"{(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return "non misurabile";
        }
    }
}
