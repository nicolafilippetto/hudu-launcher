using System.Net.Http;
using System.Reflection;
using System.Text;
using Launcher.Connector.Core;
using Velopack;
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

    /// <summary>Chiave pubblica per la firma del feed; senza chiave niente aggiornamenti automatici.</summary>
    public static string? PublicKey { get; } =
        typeof(LauncherUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdatePublicKey")?.Value is { Length: > 0 } key ? key : null;

    public string? AvailableVersion => _update?.TargetFullRelease.Version.ToString();

    public bool IsDownloaded => _downloaded is not null;

    /// <summary>Versione più recente disponibile sul portale, o null se si è aggiornati (o se non si può aggiornare).</summary>
    public async Task<string?> CheckAsync(Uri portal, CancellationToken ct)
    {
        if (PublicKey is null)
        {
            Log.Info("Aggiornamenti automatici non disponibili: questa build non contiene la chiave di firma");
            return null;
        }

        var manager = new UpdateManager(new SignedPortalSource(new Uri(portal, "launcher/"), PublicKey));
        if (!manager.IsInstalled)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var update = await manager.CheckForUpdatesAsync();
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

        await _manager.DownloadUpdatesAsync(_update, progress, ct);
        _downloaded = _update.TargetFullRelease;
        Log.Info($"Aggiornamento {_downloaded.Version} scaricato e verificato");
    }

    /// <summary>Installa subito e riapre il Launcher (da usare senza connessioni aperte).</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && _downloaded is not null)
        {
            _manager.ApplyUpdatesAndRestart(_downloaded);
        }
    }

    /// <summary>Installa quando il Launcher si chiude.</summary>
    public void ApplyOnExit()
    {
        if (_manager is not null && _downloaded is not null)
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
