using System.Diagnostics;
using Launcher.Connector.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Launcher.Connector.Services;

/// <summary>
/// Token Microsoft 365 per chiamare il portale. Usa il broker di Windows (WAM): sui PC aggiunti a
/// Entra l'accesso è automatico con l'account di Windows, senza finestre.
/// </summary>
public sealed class EntraAuth
{
    private readonly Lock _gate = new();
    private IPublicClientApplication? _app;
    private string? _appKey;

    public async Task<string> GetTokenAsync(ConnectorConfig config, IntPtr window, CancellationToken ct)
    {
        var app = GetApp(config, window);
        string[] scopes = [config.Scope!];

        // Il primo accesso del giorno carica il broker di Windows e può richiedere diversi secondi:
        // fuori dal thread della finestra, che altrimenti restava bloccata.
        if (await TrySilentAsync(app, scopes, ct) is { } silent)
        {
            return silent;
        }

        Log.Info("Accesso 365 interattivo richiesto");
        var interactive = await app.AcquireTokenInteractive(scopes)
            .WithParentActivityOrWindow(window)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(ct);
        return interactive.AccessToken;
    }

    /// <summary>
    /// Prepara l'accesso all'avvio del Launcher, e poi lo rinnova periodicamente: carica il broker e ottiene
    /// un token senza mai mostrare finestre, così la connessione non aspetta. Se serve l'interazione, si
    /// rimanda alla connessione.
    /// </summary>
    public async Task WarmUpAsync(ConnectorConfig config, IntPtr window, bool periodic, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var ready = await TrySilentAsync(GetApp(config, window), [config.Scope!], ct) is not null;
        // Il rinnovo periodico si registra solo se lento o se serve l'accesso interattivo.
        if (!periodic || !ready || watch.ElapsedMilliseconds > 2000)
        {
            Log.Info($"Accesso 365 {(periodic ? "rinnovato" : "preparato all'avvio")} in {watch.ElapsedMilliseconds} ms" + (ready ? "" : " (alla prima connessione servirà l'accesso interattivo)"));
        }
    }

    private static async Task<string?> TrySilentAsync(IPublicClientApplication app, string[] scopes, CancellationToken ct)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
                var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                return result.AccessToken;
            }, ct);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private IPublicClientApplication GetApp(ConnectorConfig config, IntPtr window)
    {
        lock (_gate)
        {
            var key = $"{config.TenantId}|{config.ClientId}";
            if (_app is not null && _appKey == key)
            {
                return _app;
            }

            _app = PublicClientApplicationBuilder.Create(config.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, config.TenantId)
                .WithDefaultRedirectUri()
                .WithParentActivityOrWindow(() => window)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Hudu Launcher" })
                .Build();
            _appKey = key;
            return _app;
        }
    }
}
