using System.Net.Http;
using System.Reflection;
using System.Text;
using Launcher.Connector.Core;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Launcher.Connector.Services;

/// <summary>
/// Aggiornamenti dal portale: il portale serve il feed di Velopack incluso nella sua immagine Docker,
/// ma il Launcher accetta solo un feed firmato dalla pipeline (chiave pubblica inserita in compilazione).
/// I pacchetti scaricati vengono poi verificati da Velopack con gli hash del feed.
/// </summary>
public sealed class LauncherUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private UpdateManager? _manager;
    private UpdateInfo? _update;
    private VelopackAsset? _downloaded;
    private bool _applyScheduled;

    /// <summary>Chiave pubblica per la firma del feed; senza chiave niente aggiornamenti automatici.</summary>
    public static string? PublicKey { get; } =
        typeof(LauncherUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdatePublicKey")?.Value is { Length: > 0 } key ? key : null;

    public string? AvailableVersion => _update?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Percorsi dell'installazione per Velopack. Negli avvii normali VelopackApp.Run non si esegue
    /// (vedi Program), quindi il locator si crea qui, solo quando serve un aggiornamento.
    /// </summary>
    private static IVelopackLocator Locator() =>
        VelopackLocator.IsCurrentSet ? VelopackLocator.Current : VelopackLocator.CreateDefaultForPlatform();

    /// <summary>
    /// Toglie i pacchetti di versioni precedenti a quella in uso (cartella packages dell'installazione),
    /// come faceva VelopackApp.Run, ma decidendo dal nome del file senza aprire gli archivi. I pacchetti
    /// della versione in uso o più recenti (aggiornamento già scaricato) restano.
    /// </summary>
    public static void RemoveOldPackages()
    {
        try
        {
            var current = ConnectorSettings.Version;
            var packages = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "packages"));
            if (!Directory.Exists(packages) || !AppVersion.TryParse(current, out _))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(packages, "*.nupkg"))
            {
                if (PackageFiles.IsOlder(Path.GetFileName(file), current))
                {
                    File.Delete(file);
                    Log.Info("Pacchetto vecchio rimosso: " + Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Pulizia dei pacchetti vecchi non riuscita: " + ex.Message);
        }
    }

    public bool IsDownloaded => _downloaded is not null;

    /// <summary>Versione più recente disponibile sul portale, o null se si è aggiornati (o se non si può aggiornare).</summary>
    public async Task<string?> CheckAsync(Uri portal, CancellationToken ct)
    {
        if (PublicKey is null)
        {
            Log.Info("Aggiornamenti automatici non disponibili: questa build non contiene la chiave di firma");
            return null;
        }

        // Fuori dal thread della finestra: Velopack legge i pacchetti locali e il feed anche in modo
        // sincrono, e la finestra non deve bloccarsi.
        var key = PublicKey;
        var (manager, update) = await Task.Run<(UpdateManager?, UpdateInfo?)>(async () =>
        {
            var manager = new UpdateManager(new SignedPortalSource(new Uri(portal, "launcher/"), key), null, Locator());
            if (!manager.IsInstalled)
            {
                return (null, null);
            }

            ct.ThrowIfCancellationRequested();
            return (manager, await manager.CheckForUpdatesAsync().ConfigureAwait(false));
        }, ct);
        if (manager is null)
        {
            return null;
        }

        _manager = manager;
        _update = update;
        if (update is not null)
        {
            Log.Info($"Aggiornamento disponibile sul portale: {update.TargetFullRelease.Version} (installata {ConnectorSettings.Version})");
        }

        return AvailableVersion;
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
    {
        if (_manager is null || _update is null)
        {
            throw new InvalidOperationException("Nessun aggiornamento da scaricare.");
        }

        // Download, verifica degli hash ed eventuali pacchetti delta: lavoro pesante, fuori dalla finestra.
        var (manager, update) = (_manager, _update);
        await Task.Run(() => manager.DownloadUpdatesAsync(update, progress, cancelToken: ct), ct);
        _downloaded = update.TargetFullRelease;
        Log.Info($"Aggiornamento {_downloaded.Version} scaricato e verificato");
    }

    /// <summary>
    /// Prepara l'installazione immediata (senza connessioni aperte): l'updater di Velopack attende
    /// che il Launcher si chiuda, installa mostrando l'avanzamento e lo riapre. Il chiamante deve poi
    /// chiudere l'app normalmente: uscire di colpo (come fa ApplyUpdatesAndRestart) con i controlli
    /// COM e WebView2 ancora attivi bloccava la finestra per qualche secondo ("Non risponde").
    /// </summary>
    /// <returns>true se l'updater è partito e l'app va chiusa.</returns>
    public bool PrepareRestart()
    {
        if (_manager is null || _downloaded is null)
        {
            return false;
        }

        _manager.WaitExitThenApplyUpdates(_downloaded, silent: false, restart: true);
        _applyScheduled = true;
        return true;
    }

    /// <summary>Installa quando il Launcher si chiude.</summary>
    public void ApplyOnExit()
    {
        if (_manager is not null && _downloaded is not null && !_applyScheduled)
        {
            _manager.WaitExitThenApplyUpdates(_downloaded, silent: true, restart: false);
        }
    }

    /// <summary>Feed del portale accettato solo con la firma della pipeline; i pacchetti si scaricano dallo stesso indirizzo.</summary>
    private sealed class SignedPortalSource(Uri feedBase, string publicKey) : IUpdateSource
    {
        private readonly SimpleWebSource _files = new(feedBase);

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            var name = $"releases.{(string.IsNullOrEmpty(channel) ? "win" : channel)}.json";
            var feed = await Http.GetByteArrayAsync(new Uri(feedBase, name));
            var signature = await Http.GetByteArrayAsync(new Uri(feedBase, name + ".sig"));
            if (!UpdateSignature.Verify(feed, signature, publicKey))
            {
                Log.Warn("Feed degli aggiornamenti del portale senza firma valida: ignorato");
                throw new InvalidOperationException("Feed degli aggiornamenti senza firma valida.");
            }

            return VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feed));
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
            _files.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
