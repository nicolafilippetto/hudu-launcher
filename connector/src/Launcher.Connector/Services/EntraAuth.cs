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
    private IPublicClientApplication? _app;
    private string? _appKey;

    public async Task<string> GetTokenAsync(ConnectorConfig config, IntPtr window, CancellationToken ct)
    {
        var app = GetApp(config, window);
        string[] scopes = [config.Scope!];

        try
        {
            var account = (await app.GetAccountsAsync()).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
            var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            Log.Info("Accesso 365 interattivo richiesto");
        }

        var interactive = await app.AcquireTokenInteractive(scopes)
            .WithParentActivityOrWindow(window)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(ct);
        return interactive.AccessToken;
    }

    private IPublicClientApplication GetApp(ConnectorConfig config, IntPtr window)
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
