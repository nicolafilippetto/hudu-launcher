using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Server.Data;
using Launcher.Server.Integrations;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Launcher.Server.Tests;

/// <summary>
/// Avvia il server vero con: dati in una cartella temporanea, un finto Hudu (gestore HTTP),
/// un endpoint di test per entrare come tecnico 365 e token del connettore firmati da una chiave di test.
/// </summary>
public class LauncherFactory : WebApplicationFactory<Program>
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string ClientId = "22222222-2222-2222-2222-222222222222";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "launcher-tests-" + Guid.NewGuid().ToString("N"));

    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "test" };

    public LauncherFactory()
    {
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Launcher__DataDirectory", _dataDir);
        Environment.SetEnvironmentVariable("Launcher__MasterKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Launcher__SetupToken", "setup-di-test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(HuduClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeHudu());
            // RustDesk irraggiungibile: ogni richiesta va in timeout, come da una rete che scarta i pacchetti.
            services.AddHttpClient(RustDeskClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(CreateRustDeskHandler);
            // Niente sincronizzazioni in background durante i test: i dati li prepara SeedAsync.
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList())
            {
                services.Remove(hosted);
            }
            services.PostConfigure<JwtBearerOptions>(AuthSchemes.Connector, o =>
            {
                o.Configuration = new OpenIdConnectConfiguration();
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuers = [$"https://login.microsoftonline.com/{TenantId}/v2.0"];
                o.TokenValidationParameters.ValidAudiences = [$"api://{ClientId}"];
            });
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestSignInStartupFilter>();
        });
    }

    /// <summary>RustDesk finto: per impostazione predefinita va sempre in timeout.</summary>
    protected virtual HttpMessageHandler CreateRustDeskHandler() => new TimeoutHandler();

    public string CreateConnectorToken(string email, string scope = EntraSettings.ConnectorScopeName, string tenant = TenantId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0",
            Audience = $"api://{ClientId}",
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["preferred_username"] = email,
                ["name"] = email,
                ["tid"] = tenant,
                ["scp"] = scope,
            },
        });

    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EntraTenantId] = TenantId,
            [SettingKeys.EntraClientId] = ClientId,
            [SettingKeys.EntraClientSecret] = "segreto",
            [SettingKeys.HuduBaseUrl] = "https://hudu.test",
            [SettingKeys.HuduApiKey] = "chiave",
            [SettingKeys.RustDeskConnectionPassword] = "password-rustdesk",
            [SettingKeys.RustDeskApiUrl] = "https://rustdesk.test",
            [SettingKeys.RustDeskApiToken] = "token",
            [SettingKeys.AllowLocalAdminConnections] = "False",
        }, "test");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Companies.AnyAsync())
        {
            return;
        }

        db.Admins.Add(new AdminEntry { Email = "capo@example.com", CreatedAt = DateTimeOffset.UtcNow });
        db.LayoutConfigs.Add(new LayoutConfig { LayoutId = 28, LayoutName = "Virtual Machines", Enabled = true, IpFieldLabel = "IP Address" });
        db.Companies.Add(new CachedCompany { Id = 1, Name = "Cliente Uno" });
        db.Companies.Add(new CachedCompany { Id = 2, Name = "Cliente Due" });
        db.Assets.Add(new CachedAsset { Id = 10, CompanyId = 1, LayoutId = 28, LayoutName = "Virtual Machines", Name = "SRV-DC01", PrimaryIp = "10.0.0.10", OperatingSystem = "Windows Server 2022", SearchText = "srv-dc01" });
        db.Assets.Add(new CachedAsset { Id = 20, CompanyId = 2, LayoutId = 28, LayoutName = "Virtual Machines", Name = "SRV-NOGW", PrimaryIp = "10.0.0.20", SearchText = "srv-nogw" });
        db.Passwords.Add(new CachedPassword { Id = 501, CompanyId = 1, Name = "Admin dominio", Username = "DOM\\admin", AssetId = 10 });
        db.Peers.Add(new CachedPeer { Id = "100200300", Hostname = "JUMP01" });
        db.CompanySettings.Add(new CompanySetting { CompanyId = 1, GatewayPeerId = "100200300" });
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // file ancora aperto da SQLite: la cartella temporanea verrà pulita dal sistema
        }
    }

    /// <summary>Hudu finto: risponde solo alla lettura della credenziale 501.</summary>
    private sealed class FakeHudu : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("x-api-key", out var keys) && keys.Single() == "chiave"
                && request.RequestUri!.AbsolutePath == "/api/v1/asset_passwords/501")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { asset_password = new { id = 501, company_id = 1, name = "Admin dominio", username = "DOM\\admin", password = "P@ssw0rd!" } }),
                });
            }

            // Elenchi vuoti per la sincronizzazione (companies, asset_layouts, assets, asset_passwords).
            var name = request.RequestUri!.AbsolutePath.Replace("/api/v1/", "", StringComparison.Ordinal);
            if (name == "ip_addresses")
            {
                // Come Hudu vero: niente paginazione (400) e risposta come array semplice.
                if (request.RequestUri!.Query.Contains("page", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"page is not a valid filter parameter.\"}", System.Text.Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [{"id":1,"address":"192.168.1.1","end_address":null,"address_type":"single","status":"Assigned","asset_id":null,"company_id":2,"discarded_at":null},
                         {"id":2,"address":"192.168.1.41","end_address":null,"address_type":"single","status":"Assigned","asset_id":20,"asset_name":"SRV-NOGW","fqdn":null,"company_id":2,"discarded_at":null},
                         {"id":3,"address":"192.168.1.99","status":"Assigned","asset_id":20,"company_id":2,"discarded_at":"2026-01-01T00:00:00Z"}]
                        """, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (name is "companies" or "asset_layouts" or "assets" or "asset_passwords")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"{name}\":[]}}", System.Text.Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("timeout", new TimeoutException());
    }

    /// <summary>Endpoint solo per i test: entra nel portale come utente 365 senza passare da Entra.</summary>
    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Use e non Map: con Map il cookie avrebbe come percorso /test-signin.
            app.Use(async (http, nextMiddleware) =>
            {
                if (http.Request.Path != "/test-signin")
                {
                    await nextMiddleware(http);
                    return;
                }

                var factory = http.RequestServices.GetRequiredService<PortalPrincipalFactory>();
                var principal = await factory.CreateAsync(http.Request.Query["email"]!, null, "oid", http.Request.Query["method"].FirstOrDefault() ?? LauncherClaims.MethodEntra);
                await http.SignInAsync(AuthSchemes.Portal, principal);
            });
            next(app);
        };
    }
}

// Stessa collezione dei test di sincronizzazione: le istanze del server leggono la cartella dati
// da variabili d'ambiente del processo, quindi non devono avviarsi in parallelo.
[Collection("Server")]
public sealed partial class IntegrationTests(LauncherFactory factory) : IClassFixture<LauncherFactory>
{
    private async Task<HttpClient> SignedInClientAsync(string email, string method = LauncherClaims.MethodEntra)
    {
        await factory.SeedAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync(new Uri($"/test-signin?email={email}&method={method}", UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var page = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.True(page.IsSuccessStatusCode, $"GET / -> {(int)page.StatusCode} {page.Headers.Location}");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return TokenRegex().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostApiAsync(HttpClient client, string path, object body)
    {
        var token = await AntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> RedeemAsync(string ticket, string? bearer)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/connector/redeem", UriKind.Relative)) { Content = JsonContent.Create(new { ticket }) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> CreateTicketAsync(HttpClient client, int assetId = 10, string auth = LauncherClaims.MethodEntra)
    {
        var response = await PostApiAsync(client, "/api/connect", new { assetId, key = "primary" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("url").GetString()!);
        Assert.Equal("hudulauncher", url.Scheme);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("https://localhost", query["portal"]);
        Assert.Equal(auth, query["auth"]);
        return query["ticket"]!;
    }

    private async Task SetSettingAsync(string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveAsync(new Dictionary<string, string?> { [key] = value }, "test");
    }

    [Fact]
    public async Task Health_and_security_headers()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Ticket_is_redeemed_once_by_the_same_user_with_credentials()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);

        var response = await RedeemAsync(ticket, factory.CreateConnectorToken("Tecnico@Example.com"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Rdp", body.GetProperty("protocol").GetString());
        Assert.Equal("10.0.0.10", body.GetProperty("target").GetProperty("host").GetString());
        Assert.Equal(3389, body.GetProperty("target").GetProperty("port").GetInt32());
        Assert.Equal("100200300", body.GetProperty("gateway").GetProperty("id").GetString());
        Assert.Equal("password-rustdesk", body.GetProperty("gateway").GetProperty("password").GetString());
        Assert.Equal("DOM\\admin", body.GetProperty("credential").GetProperty("username").GetString());
        Assert.Equal("P@ssw0rd!", body.GetProperty("credential").GetProperty("password").GetString());

        var again = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Ticket_of_another_user_is_rejected_and_burned()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);

        var stolen = await RedeemAsync(ticket, factory.CreateConnectorToken("ladro@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        Assert.DoesNotContain("P@ssw0rd", await stolen.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var owner = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
    }

    [Fact]
    public async Task Expired_ticket_is_rejected()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Tickets.SingleAsync(t => t.Id == Connections.TicketService.Hash(ticket), TestContext.Current.CancellationToken);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("User.Read", LauncherFactory.TenantId)]
    [InlineData(EntraSettings.ConnectorScopeName, "33333333-3333-3333-3333-333333333333")]
    public async Task Connector_token_needs_scope_and_tenant(string scope, string tenant)
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);
        var response = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com", scope, tenant));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connection_without_gateway_cannot_be_started()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var response = await PostApiAsync(client, "/api/connect", new { assetId = 20, key = "primary" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Direct_connection_needs_no_gateway_and_gets_no_rustdesk_password()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var response = await PostApiAsync(client, "/api/connect", new { assetId = 20, key = "primary", mode = "direct" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("url").GetString()!);
        var ticket = System.Web.HttpUtility.ParseQueryString(url.Query)["ticket"]!;

        var redeem = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var text = await redeem.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("gateway").ValueKind);
        Assert.Equal("10.0.0.20", body.GetProperty("target").GetProperty("host").GetString());
        Assert.DoesNotContain("password-rustdesk", text, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "ticket.redeem" && e.AssetId == 20 && e.Details!.Contains("diretta"), TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> RedeemOfflineAsync(string ticket, string? bearer)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/connector/redeem-offline", UriKind.Relative)) { Content = JsonContent.Create(new { ticket }) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> TicketFromAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("url").GetString()!);
        Assert.Equal("offline", url.Host);
        return System.Web.HttpUtility.ParseQueryString(url.Query)["ticket"]!;
    }

    [Fact]
    public async Task Offline_bundle_contains_direct_connections_with_credentials()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await TicketFromAsync(await PostApiAsync(client, "/api/offline", new { assetIds = new[] { 10, 20 } }));

        var response = await RedeemOfflineAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(3600, body.GetProperty("validSeconds").GetInt32());
        var connections = body.GetProperty("connections").EnumerateArray().ToList();
        Assert.Equal(2, connections.Count);
        Assert.All(connections, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("gateway").ValueKind));
        Assert.Contains(connections, c => c.GetProperty("name").GetString() == "SRV-DC01" && c.GetProperty("credential").GetProperty("password").GetString() == "P@ssw0rd!");
        Assert.DoesNotContain("password-rustdesk", text, StringComparison.Ordinal);

        // Biglietto monouso anche per il pacchetto.
        Assert.Equal(HttpStatusCode.Forbidden, (await RedeemOfflineAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "offline.prepare" && e.UserEmail == "tecnico@example.com", TestContext.Current.CancellationToken));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "offline.redeem" && e.AssetId == 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_and_single_tickets_cannot_be_swapped()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var offlineTicket = await TicketFromAsync(await PostApiAsync(client, "/api/offline", new { assetIds = new[] { 10 } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await RedeemAsync(offlineTicket, factory.CreateConnectorToken("tecnico@example.com"))).StatusCode);

        var singleTicket = await CreateTicketAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await RedeemOfflineAsync(singleTicket, factory.CreateConnectorToken("tecnico@example.com"))).StatusCode);
    }

    [Fact]
    public async Task Offline_respects_the_admin_limit()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        await SetSettingAsync(SettingKeys.OfflineMaxAssets, "1");
        try
        {
            var tooMany = await PostApiAsync(client, "/api/offline", new { assetIds = new[] { 10, 20 } });
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

            await SetSettingAsync(SettingKeys.OfflineMaxAssets, "0");
            var disabled = await PostApiAsync(client, "/api/offline", new { assetIds = new[] { 10 } });
            Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        }
        finally
        {
            await SetSettingAsync(SettingKeys.OfflineMaxAssets, "10");
        }
    }

    [Fact]
    public async Task Gateway_ticket_still_uses_the_jump_host()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);
        var redeem = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        var body = await redeem.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("100200300", body.GetProperty("gateway").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Local_admin_cannot_use_the_connector_by_default()
    {
        var client = await SignedInClientAsync("capo@example.com", LauncherClaims.MethodLocal);
        var response = await PostApiAsync(client, "/api/connect", new { assetId = 10, key = "primary" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Local_admin_ticket_works_without_365_when_enabled()
    {
        var client = await SignedInClientAsync("capo@example.com", LauncherClaims.MethodLocal);
        await SetSettingAsync(SettingKeys.AllowLocalAdminConnections, "True");
        var ticket = await CreateTicketAsync(client, auth: LauncherClaims.MethodLocal);

        var response = await RedeemAsync(ticket, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("P@ssw0rd!", body.GetProperty("credential").GetProperty("password").GetString());

        var again = await RedeemAsync(ticket, null);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Local_admin_ticket_is_bound_to_the_browser_address()
    {
        var client = await SignedInClientAsync("capo@example.com", LauncherClaims.MethodLocal);
        await SetSettingAsync(SettingKeys.AllowLocalAdminConnections, "True");
        var ticket = await CreateTicketAsync(client, auth: LauncherClaims.MethodLocal);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Tickets.SingleAsync(t => t.Id == Connections.TicketService.Hash(ticket), TestContext.Current.CancellationToken);
            row.ClientIp = "203.0.113.9";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await RedeemAsync(ticket, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("P@ssw0rd", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_admin_ticket_stops_working_when_the_option_is_turned_off()
    {
        var client = await SignedInClientAsync("capo@example.com", LauncherClaims.MethodLocal);
        await SetSettingAsync(SettingKeys.AllowLocalAdminConnections, "True");
        var ticket = await CreateTicketAsync(client, auth: LauncherClaims.MethodLocal);
        await SetSettingAsync(SettingKeys.AllowLocalAdminConnections, "False");

        var response = await RedeemAsync(ticket, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Entra_ticket_without_token_is_rejected_and_burned()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var ticket = await CreateTicketAsync(client);

        var anonymous = await RedeemAsync(ticket, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var owner = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
    }

    [Fact]
    public async Task Technicians_cannot_open_admin_pages()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var response = await client.GetAsync(new Uri("/Admin/Settings", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_email_from_365_gets_admin_pages()
    {
        var client = await SignedInClientAsync("capo@example.com");
        var response = await client.GetAsync(new Uri("/Admin/Settings", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("segreto", html, StringComparison.Ordinal);
        Assert.DoesNotContain("password-rustdesk", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_cannot_be_copied_from_the_portal()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var response = await PostApiAsync(client, "/api/password", new { assetId = 10, key = "primary" });
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("P@ssw0rd", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_requires_antiforgery_token()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var response = await client.PostAsJsonAsync(new Uri("/api/connect", UriKind.Relative), new { assetId = 10, key = "primary" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Settings_secrets_are_encrypted_at_rest()
    {
        await factory.SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Settings.SingleAsync(s => s.Key == SettingKeys.RustDeskConnectionPassword, TestContext.Current.CancellationToken);
        Assert.True(row.IsSecret);
        Assert.DoesNotContain("password-rustdesk", row.Value!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_rustdesk_shows_a_message_instead_of_an_error()
    {
        var client = await SignedInClientAsync("capo@example.com");
        var page = await client.GetStringAsync(new Uri("/Admin/Settings", UriKind.Relative), TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = TokenRegex().Match(page).Groups[1].Value });
        var response = await client.PostAsync(new Uri("/Admin/Settings?handler=TestRustDesk", UriKind.Relative), form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var after = await client.GetStringAsync(new Uri("/Admin/Settings", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("Server RustDesk non raggiungibile (rustdesk.test:443)", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_inside_a_company_stays_in_the_company()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var everywhere = await client.GetStringAsync(new Uri("/?handler=Search&q=srv", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("SRV-DC01", everywhere, StringComparison.Ordinal);
        Assert.Contains("SRV-NOGW", everywhere, StringComparison.Ordinal);

        var scoped = await client.GetStringAsync(new Uri("/?handler=Search&q=srv&c=1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("SRV-DC01", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("SRV-NOGW", scoped, StringComparison.Ordinal);
        Assert.Contains("data-search-everywhere", scoped, StringComparison.Ordinal);

        var page = await client.GetStringAsync(new Uri("/?c=1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("data-scope-id=\"1\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rdp_options_combine_admin_defaults_and_asset_overrides()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        await SetSettingAsync(SettingKeys.RdpDefaults, "{\"drives\":\"true\"}");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AssetSettings.Add(new AssetSetting { AssetId = 10, RdpOptions = "{\"adminSession\":\"true\",\"audio\":\"local\",\"bogus\":\"1\"}" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var ticket = await CreateTicketAsync(client);
            var response = await RedeemAsync(ticket, factory.CreateConnectorToken("tecnico@example.com"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rdp = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("rdp");
            Assert.True(rdp.GetProperty("adminSession").GetBoolean());
            Assert.True(rdp.GetProperty("drives").GetBoolean());
            Assert.True(rdp.GetProperty("clipboard").GetBoolean());
            Assert.False(rdp.GetProperty("printers").GetBoolean());
            Assert.Equal("local", rdp.GetProperty("audio").GetString());
            Assert.Equal("remote", rdp.GetProperty("keyboard").GetString());
            Assert.Equal("resize", rdp.GetProperty("display").GetString());
        }
        finally
        {
            await SetSettingAsync(SettingKeys.RdpDefaults, "");
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AssetSettings.Where(a => a.AssetId == 10).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Hudu_client_can_only_read(string method)
    {
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(HuduClient.HttpClientName);
        using var write = new HttpRequestMessage(new HttpMethod(method), new Uri("https://hudu.test/api/v1/asset_passwords/501"));
        await Assert.ThrowsAsync<HuduException>(() => client.SendAsync(write, TestContext.Current.CancellationToken));

        using var read = new HttpRequestMessage(HttpMethod.Get, new Uri("https://hudu.test/api/v1/companies"));
        using var response = await client.SendAsync(read, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_button_works_and_needs_the_antiforgery_token()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var form = LogoutFormRegex().Match(html);
        Assert.True(form.Success, "form di logout non trovato");
        Assert.Contains("__RequestVerificationToken", form.Value, StringComparison.Ordinal);

        var forged = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative), new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var token = TokenRegex().Match(form.Value).Groups[1].Value;
        var logout = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("loggedOut", logout.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Company_refresh_is_limited_to_once_a_minute()
    {
        await factory.SeedAsync();
        var sync = factory.Services.GetRequiredService<SyncService>();
        try
        {
            var first = await sync.RefreshCompanyAsync(2, "tecnico@example.com", TestContext.Current.CancellationToken);
            Assert.True(first.Success, first.Message);
            Assert.Contains("1 IP dall'IPAM", first.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("IPAM non letto", first.Message, StringComparison.Ordinal);
            var second = await sync.RefreshCompanyAsync(2, "tecnico@example.com", TestContext.Current.CancellationToken);
            Assert.False(second.Success);
            Assert.Contains("riprova", second.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Hudu finto restituisce elenchi vuoti: si rimette l'asset di prova del cliente 2.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Assets.AnyAsync(a => a.Id == 20, TestContext.Current.CancellationToken))
            {
                db.Assets.Add(new CachedAsset { Id = 20, CompanyId = 2, LayoutId = 28, LayoutName = "Virtual Machines", Name = "SRV-NOGW", PrimaryIp = "10.0.0.20", SearchText = "srv-nogw" });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task New_admin_can_get_a_break_glass_account()
    {
        var client = await SignedInClientAsync("capo@example.com");
        var page = await client.GetStringAsync(new Uri("/Admin/Admins", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("data-breakglass-form", page, StringComparison.Ordinal);
        var token = TokenRegex().Match(page).Groups[1].Value;

        async Task<HttpResponseMessage> AddAsync(string email, string? password) =>
            await client.PostAsync(new Uri("/Admin/Admins?handler=Add", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["email"] = email,
                ["createLocal"] = password is null ? "false" : "true",
                ["password"] = password ?? "",
            }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, (await AddAsync("debole@example.com", "corta")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AddAsync("nuovo@example.com", "Emergenza-2026-Lunga")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AddAsync("solo365@example.com", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Admins.AnyAsync(a => a.Email == "debole@example.com", TestContext.Current.CancellationToken));
        Assert.True(await db.Admins.AnyAsync(a => a.Email == "nuovo@example.com", TestContext.Current.CancellationToken));
        Assert.True(await db.Users.AnyAsync(u => u.Email == "nuovo@example.com", TestContext.Current.CancellationToken));
        Assert.True(await db.Admins.AnyAsync(a => a.Email == "solo365@example.com", TestContext.Current.CancellationToken));
        Assert.False(await db.Users.AnyAsync(u => u.Email == "solo365@example.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Editor_accepts_only_ipam_addresses()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Assets.Where(a => a.Id == 10).ExecuteUpdateAsync(u => u.SetProperty(a => a.IpamAddresses, "10.0.0.10\n10.0.1.10"), TestContext.Current.CancellationToken);
        }

        try
        {
            var editor = await client.GetStringAsync(new Uri("/?handler=Editor&assetId=10", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Contains("<option value=\"10.0.1.10\">", editor, StringComparison.Ordinal);
            var page = await client.GetStringAsync(new Uri("/?c=1", UriKind.Relative), TestContext.Current.CancellationToken);
            var token = TokenRegex().Match(page).Groups[1].Value;

            async Task<HttpResponseMessage> SaveAsync(string host) =>
                await client.PostAsync(new Uri("/?handler=SaveAsset", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["AssetId"] = "10",
                    ["Host"] = host,
                }), TestContext.Current.CancellationToken);

            var rejected = await SaveAsync("192.168.99.99");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Contains("non è nell&#x27;IPAM", await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await SaveAsync("10.0.1.10")).StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("10.0.1.10", (await db.AssetSettings.FindAsync([10], TestContext.Current.CancellationToken))?.Host);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AssetSettings.Where(a => a.AssetId == 10).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.Assets.Where(a => a.Id == 10).ExecuteUpdateAsync(u => u.SetProperty(a => a.IpamAddresses, (string?)null), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Bulk_edit_sets_gateway_and_hides_assets_of_several_companies()
    {
        var client = await SignedInClientAsync("tecnico@example.com");
        var page = await client.GetStringAsync(new Uri("/?c=1", UriKind.Relative), TestContext.Current.CancellationToken);
        var token = TokenRegex().Match(page).Groups[1].Value;
        Assert.Contains("data-pick=\"10\"", page, StringComparison.Ordinal);

        async Task<HttpResponseMessage> BulkAsync(Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token;
            values["AssetIds"] = "10,20";
            return await client.PostAsync(new Uri("/?handler=Bulk", UriKind.Relative), new FormUrlEncodedContent(values), TestContext.Current.CancellationToken);
        }

        try
        {
            var editor = await client.GetStringAsync(new Uri("/?handler=BulkEditor&ids=10,20", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Contains("2 clienti", editor, StringComparison.Ordinal);

            // Credenziale di un cliente su asset di due clienti: rifiutata.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await BulkAsync(new() { ["PasswordId"] = "5" })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await BulkAsync(new())).StatusCode);

            var ok = await BulkAsync(new() { ["GatewayMode"] = "set", ["Gateway"] = "999888777", ["Visibility"] = "hide" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await db.AssetSettings.AsNoTracking().Where(a => a.AssetId == 10 || a.AssetId == 20).ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, settings.Count);
                Assert.All(settings, s => Assert.True(s.Hidden && s.GatewayPeerId == "999888777"));
                Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Details!.StartsWith("Modifica in blocco"), TestContext.Current.CancellationToken));
            }

            // Tornare al gateway automatico e visibili elimina le impostazioni rimaste vuote.
            Assert.Equal(HttpStatusCode.OK, (await BulkAsync(new() { ["GatewayMode"] = "auto", ["Visibility"] = "show" })).StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.False(await db.AssetSettings.AnyAsync(a => a.AssetId == 10 || a.AssetId == 20, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AssetSettings.Where(a => a.AssetId == 10 || a.AssetId == 20).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    [GeneratedRegex("<form method=\"post\" action=\"/Account/Logout\">.*?</form>", RegexOptions.Singleline)]
    private static partial Regex LogoutFormRegex();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
