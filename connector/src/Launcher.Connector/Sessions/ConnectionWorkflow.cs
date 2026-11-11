using System.Net.Http;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Dal biglietto alla sessione: accesso 365, riscatto sul portale, tunnel RustDesk e apertura
/// della scheda giusta (RDP, SSH, Telnet o web). Gestisce anche la cache d'emergenza offline.
/// </summary>
public sealed class ConnectionWorkflow(RustDeskTunnels tunnels, EntraAuth auth, OfflineCache offline)
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly KnownHosts _knownHosts = new(ConnectorSettings.KnownHostsFile);
    private ConnectorConfig? _config;

    /// <summary>Il portale chiede una versione più recente del Launcher (versione minima impostata dagli admin).</summary>
    public event Action? UpdateRequired;

    /// <summary>
    /// Ogni richiesta al portale dice versione del Launcher e nome del PC: gli admin vedono chi usa quale
    /// versione e il portale può chiedere l'aggiornamento.
    /// </summary>
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Launcher-Version", ConnectorSettings.Version);
        var machine = new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(64).ToArray());
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Launcher-Machine", machine);
        return http;
    }

    public OfflineCache Offline => offline;

    /// <summary>
    /// All'avvio e poi periodicamente: configurazione del portale e accesso 365 preparati in background,
    /// senza finestre, così la connessione non aspetta il caricamento del broker di Windows e il token
    /// resta fresco anche dopo ore di inattività.
    /// </summary>
    public async Task WarmUpAsync(IntPtr window, bool periodic = false)
    {
        if (PortalClient.TryNormalizePortalUrl(ConnectorSettings.PortalUrl) is not { } url || url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            _config ??= await new PortalClient(Http, url).GetConfigAsync(CancellationToken.None);
            await auth.WarmUpAsync(_config, window, periodic, CancellationToken.None);
        }
        catch (Exception ex) when (ex is PortalException or HttpRequestException or TaskCanceledException or Microsoft.Identity.Client.MsalException)
        {
            Log.Info("Accesso 365 non preparato all'avvio: " + ex.Message);
        }
    }

    /// <param name="confirmPortal">Chiede all'utente se usare il portale indicato dal link (primo avvio).</param>
    public async Task RunAsync(LaunchRequest request, SessionTab tab, IntPtr window, Func<Uri, bool> confirmPortal)
    {
        var ct = tab.Cancellation;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (portal, token) = await SignInAsync(request, window, confirmPortal, step => tab.Progress.Step(ConnectStep.SignIn, step), ct);
            var signIn = watch.ElapsedMilliseconds;

            tab.Progress.Step(ConnectStep.Redeem);
            var info = await portal.RedeemAsync(request.Ticket, token, ct);
            Log.Info($"Biglietto riscattato: accesso 365 {signIn} ms, portale {watch.ElapsedMilliseconds - signIn} ms");
            await StartSessionAsync(info, tab, ct, WebLoginSource(info, portal, request.LocalAuth, window));
        }
        catch (OperationCanceledException)
        {
            // scheda chiusa durante la connessione
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Log.Error("Connessione non riuscita", ex);
            tab.Progress.Fail(ex.Message);
            if (ex is PortalException { UpdateRequired: true })
            {
                UpdateRequired?.Invoke();
            }
        }
    }

    /// <summary>
    /// Prepara la cache d'emergenza: scambia il biglietto con le connessioni dirette degli asset scelti
    /// nel portale e le tiene in memoria. Restituisce quante sono e fino a quando valgono.
    /// </summary>
    public async Task<(int Count, DateTimeOffset ExpiresAt)> PrepareOfflineAsync(LaunchRequest request, IntPtr window, Func<Uri, bool> confirmPortal, CancellationToken ct)
    {
        var (portal, token) = await SignInAsync(request, window, confirmPortal, _ => { }, ct);
        var bundle = await portal.RedeemOfflineAsync(request.Ticket, token, ct);
        offline.Replace(bundle.Connections, TimeSpan.FromSeconds(Math.Clamp(bundle.ValidSeconds, 60, 8 * 3600)));
        Log.Info($"Cache offline preparata: {bundle.Connections.Count} connessioni per {bundle.ValidSeconds / 60} minuti");
        return (offline.Items.Count, offline.ExpiresAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Apre una connessione dalla cache offline: nessun contatto con il portale né con RustDesk.</summary>
    public async Task RunCachedAsync(ConnectionInfo info, SessionTab tab)
    {
        try
        {
            tab.Progress.Step(ConnectStep.SignIn, "non serve, dalla cache offline");
            tab.Progress.Step(ConnectStep.Redeem, "dalla cache offline");
            Log.Info($"Connessione dalla cache offline: {info.Protocol} {info.Name} ({info.Company})");
            await StartSessionAsync(info, tab, tab.Cancellation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Log.Error("Connessione offline non riuscita", ex);
            tab.Progress.Fail(ex.Message);
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is PortalException or Services.RustDeskException or ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or HttpRequestException;

    /// <summary>Portale da usare e token 365 (null per i biglietti degli admin locali).</summary>
    private async Task<(PortalClient Portal, string? Token)> SignInAsync(
        LaunchRequest request, IntPtr window, Func<Uri, bool> confirmPortal, Action<string?> step, CancellationToken ct)
    {
        var portal = new PortalClient(Http, ResolvePortal(request, confirmPortal));
        if (request.LocalAuth)
        {
            // Biglietto di un admin locale: il portale lo accetta solo dallo stesso indirizzo del browser.
            Log.Info("Biglietto di admin locale: riscatto senza Microsoft 365");
            step("non richiesto (admin locale)");
            return (portal, null);
        }

        if (!portal.IsSecure)
        {
            throw new PortalException("L'accesso con Microsoft 365 richiede il portale in https. In prova su http funzionano solo le connessioni degli admin locali.");
        }

        // Prima di tutto si "prenota" il biglietto: il portale gli concede il tempo dell'accesso 365,
        // che al primo uso del giorno può richiedere più dei secondi normali di validità.
        await portal.ClaimAsync(request.Ticket, ct);
        step(null);
        _config ??= await portal.GetConfigAsync(ct);
        try
        {
            return (portal, await auth.GetTokenAsync(_config, window, ct));
        }
        catch (Microsoft.Identity.Client.MsalException ex)
        {
            _config = null;
            throw new PortalException("Accesso a Microsoft 365 non riuscito: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Per le pagine web: chiede al portale la password della credenziale scelta, al momento di
    /// compilare l'accesso, con un token 365 fresco (o senza, per gli admin locali).
    /// </summary>
    private Func<int, CancellationToken, Task<CredentialInfo>>? WebLoginSource(ConnectionInfo info, PortalClient portal, bool localAuth, IntPtr window)
    {
        if (info.WebLogin is not { Candidates.Count: > 0 } login)
        {
            return null;
        }

        return async (credentialId, ct) =>
        {
            string? token = null;
            if (!localAuth)
            {
                try
                {
                    _config ??= await portal.GetConfigAsync(ct);
                    token = await auth.GetTokenAsync(_config, window, ct);
                }
                catch (Microsoft.Identity.Client.MsalException ex)
                {
                    throw new PortalException("Accesso a Microsoft 365 non riuscito: " + ex.Message, ex);
                }
            }

            return await portal.GetWebLoginAsync(login.Grant, credentialId, token, ct);
        };
    }

    /// <summary>Tunnel (se tramite jump host) e scheda della sessione.</summary>
    private async Task StartSessionAsync(ConnectionInfo info, SessionTab tab, CancellationToken ct,
        Func<int, CancellationToken, Task<CredentialInfo>>? webLogin = null)
    {
        tab.SetConnection(info);
        Log.Info($"Connessione {info.Protocol} a {info.Name} ({info.Company}) {info.Route}");
        SessionEndpoint endpoint;
        if (info.Gateway is { } gateway)
        {
            tab.Progress.Step(ConnectStep.Tunnel, gateway.Id);
            var status = new Progress<string>(tab.Progress.Detail);
            var tunnel = await tunnels.OpenAsync(gateway.Id, gateway.Password, gateway.Name, info.Target.Host, info.Target.Port, status, gateway.ClientPath, ct);
            tab.AttachTunnel(tunnel);
            endpoint = SessionEndpoint.ForTunnel(tunnel.LocalPort);
        }
        else
        {
            // Diretta: il PC del tecnico raggiunge già il dispositivo (rete del cliente o VPN).
            tab.Progress.Step(ConnectStep.Tunnel, $"non serve, connessione diretta a {info.Target.Host}:{info.Target.Port}");
            endpoint = SessionEndpoint.ForTarget(info.Target);
        }

        tab.Progress.Step(ConnectStep.Session, info.Name);
        ISessionView view = info.Protocol switch
        {
            ConnectionProtocol.Rdp => new RdpSessionView(info, endpoint, tunnels),
            ConnectionProtocol.Ssh => new TerminalSessionView(info, endpoint, TerminalKind.Ssh, _knownHosts),
            ConnectionProtocol.Telnet => new TerminalSessionView(info, endpoint, TerminalKind.Telnet, _knownHosts),
            _ when info.WebOrDefault.External => new WebSessionView(info, endpoint),
            _ => new BrowserSessionView(info, endpoint, _knownHosts, webLogin),
        };
        tab.ShowSession(view);
        await view.StartAsync();

        if (info.Warning is not null)
        {
            tab.ShowWarning(info.Warning);
        }
    }

    /// <summary>
    /// Indirizzo del portale: quello delle impostazioni; al primo avvio quello del link, se l'utente lo conferma.
    /// Un link di un portale diverso da quello configurato viene rifiutato.
    /// </summary>
    private static Uri ResolvePortal(LaunchRequest request, Func<Uri, bool> confirmPortal)
    {
        if (ConnectorSettings.PortalUrl is { } configured)
        {
            var portal = PortalClient.ValidatePortalUrl(configured);
            if (request.Portal is not null && !PortalClient.SameOrigin(request.Portal, portal))
            {
                throw new PortalException(
                    $"Il link arriva da {request.Portal.GetLeftPart(UriPartial.Authority)}, ma il Launcher usa {portal.GetLeftPart(UriPartial.Authority)}. " +
                    "Se il portale è cambiato, aggiorna l'indirizzo in Impostazioni (ingranaggio in basso a destra).");
            }

            return portal;
        }

        if (request.Portal is null)
        {
            throw new PortalException("Indirizzo del portale non configurato: impostalo in Impostazioni (ingranaggio in basso a destra).");
        }

        if (!confirmPortal(request.Portal))
        {
            throw new PortalException("Indirizzo del portale non confermato: connessione annullata.");
        }

        ConnectorSettings.SavePortalUrl(request.Portal.GetLeftPart(UriPartial.Authority));
        return request.Portal;
    }

    public void Shutdown() => tunnels.CleanupOnExit();
}
