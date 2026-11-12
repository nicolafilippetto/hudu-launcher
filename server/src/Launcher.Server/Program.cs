using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Launcher.Server.Audit;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Endpoints;
using Launcher.Server.Integrations;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Controllo di salute per Docker: l'immagine "chiseled" non ha curl né shell.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
});

var launcherOptions = builder.Configuration.GetSection(LauncherOptions.Section).Get<LauncherOptions>() ?? new LauncherOptions();
if (builder.Environment.IsDevelopment() && launcherOptions.DataDirectory == "/data")
{
    launcherOptions.DataDirectory = Path.Combine(builder.Environment.ContentRootPath, "local-data");
}

Directory.CreateDirectory(launcherOptions.DataDirectory);
var masterKey = MasterKey.Load(launcherOptions, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(launcherOptions);
builder.Services.AddSingleton(masterKey);

// --- Data Protection: chiavi su disco, cifrate con la chiave master ---
builder.Services.AddDataProtection()
    .SetApplicationName("Launcher")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(launcherOptions.DataDirectory, "keys")));
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
    new ConfigureOptions<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(sp.GetRequiredService<MasterKey>())));

// --- Database ---
// Niente Cache=Shared: con la cache condivisa SQLite blocca le letture finché una transazione di scrittura
// (es. la sincronizzazione con Hudu) non finisce. Con WAL e connessioni separate si leggono i dati precedenti.
var connectionString = $"Data Source={Path.Combine(launcherOptions.DataDirectory, "launcher.db")};Pooling=True";
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// --- Identity: solo per gli admin con accesso locale (password + MFA obbligatoria) ---
builder.Services.AddIdentityCore<IdentityUser>(o =>
    {
        o.Password.RequiredLength = 14;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// --- Servizi applicativi ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<PortalPrincipalFactory>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<Launcher.Server.Connections.WebLoginService>();
builder.Services.AddSingleton<Launcher.Server.Connections.LauncherReleases>();
builder.Services.AddScoped<Launcher.Server.Connections.LauncherClients>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncService>());
builder.Services.AddHostedService<HousekeepingService>();
builder.Services.AddScoped<HuduClient>();
builder.Services.AddScoped<RustDeskClient>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<TicketService>();
builder.Services.AddScoped<Launcher.Server.Pages.LayoutInfo>();
builder.Services.AddScoped<Launcher.Server.Pages.Account.LocalSignIn>();
// La chiave di Hudu non si può limitare alla sola lettura: il blocco è nel client HTTP.
builder.Services.AddTransient<ReadOnlyHandler>();
builder.Services.AddHttpClient(HuduClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
    // Niente redirect automatici: .NET toglie solo Authorization, x-api-key andrebbe al nuovo indirizzo.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<ReadOnlyHandler>();
builder.Services.AddSingleton<AuditThrottle>();
builder.Services.AddHttpClient(RustDeskClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));

// --- Autenticazione ---
var secureCookies = !builder.Environment.IsDevelopment() && !launcherOptions.AllowHttp;
string CookieName(string name) => secureCookies ? "__Host-" + name : name;

builder.Services.AddScoped<EntraOidcEvents>();
builder.Services.AddScoped<ConnectorJwtEvents>();
builder.Services.AddScoped<PortalCookieEvents>();
builder.Services.ConfigureOptions<EntraOptionsSetup>();
builder.Services.AddAuthentication(AuthSchemes.Portal)
    .AddCookie(AuthSchemes.Portal, o =>
    {
        o.Cookie.Name = CookieName("launcher");
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.SlidingExpiration = false;
        o.ExpireTimeSpan = TimeSpan.FromHours(24);
        o.EventsType = typeof(PortalCookieEvents);
    })
    .AddCookie(AuthSchemes.MfaPending, o =>
    {
        o.Cookie.Name = CookieName("launcher-mfa");
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        o.SlidingExpiration = false;
        o.LoginPath = "/Account/LocalLogin";
    })
    .AddOpenIdConnect(AuthSchemes.Entra, _ => { })
    .AddJwtBearer(AuthSchemes.Connector, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Technician, p => p.AddAuthenticationSchemes(AuthSchemes.Portal).RequireAuthenticatedUser().RequireRole(Roles.Technician, Roles.Admin))
    .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(AuthSchemes.Portal).RequireAuthenticatedUser().RequireRole(Roles.Admin))
    .AddPolicy(Policies.Connector, p => p.AddAuthenticationSchemes(AuthSchemes.Connector).RequireAuthenticatedUser())
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(AuthSchemes.Portal).RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.Cookie.Name = CookieName("launcher-af");
    o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = CookieName("launcher-msg");
    o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.IsEssential = true;
});

// Le lettere accentate restano leggibili nell'HTML (l'escape dei caratteri pericolosi resta attivo).
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", Policies.Technician);
    o.Conventions.AuthorizeFolder("/Admin", Policies.Admin);
    o.Conventions.AllowAnonymousToFolder("/Account");
    o.Conventions.AuthorizePage("/Account/Logout", Policies.Technician);
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/Error");
});

// --- Limiti di richieste, per utente (o per IP se anonimo) ---
builder.Services.AddRateLimiter(o =>
{
    static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email) ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon";

    RateLimitPartition<string> Window(HttpContext http, int permits, TimeSpan window) =>
        RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(http), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = 5,
            QueueLimit = 0,
        });

    o.AddPolicy(RateLimitPolicies.Connect, http => Window(http, 30, TimeSpan.FromMinutes(5)));
    // Riscatti: per IP, ma più tecnici possono uscire dallo stesso NAT d'ufficio. I biglietti sono da 256 bit
    // e monouso: il limite protegge il server, non serve contro i tentativi.
    o.AddPolicy(RateLimitPolicies.Redeem, http => Window(http, 120, TimeSpan.FromMinutes(5)));
    o.AddPolicy(RateLimitPolicies.WebLogin, http => Window(http, 30, TimeSpan.FromMinutes(5)));
    o.AddPolicy(RateLimitPolicies.Claim, http => Window(http, 30, TimeSpan.FromMinutes(5)));
    // Solo i tentativi (POST) contano: aprire le pagine di login non consuma il limite.
    o.AddPolicy(RateLimitPolicies.Login, http => HttpMethods.IsPost(http.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("get"));
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        // Una riga per indirizzo e percorso ogni 5 minuti, con il conteggio delle altre respinte.
        var throttle = context.HttpContext.RequestServices.GetRequiredService<AuditThrottle>();
        var key = $"{context.HttpContext.Connection.RemoteIpAddress}|{context.HttpContext.Request.Path}";
        if (throttle.ShouldWrite(key, out var suppressed))
        {
            var audit = context.HttpContext.RequestServices.GetRequiredService<AuditService>();
            await audit.WriteAsync(new AuditEntry("ratelimit", context.HttpContext.User.FindFirstValue(ClaimTypes.Email), Success: false,
                Details: context.HttpContext.Request.Path + (suppressed > 0 ? $" (altre {suppressed} respinte nei 5 minuti precedenti)" : "")),
                context.HttpContext, ct);
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsync("{\"title\":\"Troppe richieste: attendere qualche minuto.\",\"status\":429}", ct);
    };
});

// --- Proxy inverso (WAF): si fidano solo gli indirizzi indicati ---
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var entry in (launcherOptions.TrustedProxies ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (System.Net.IPNetwork.TryParse(entry, out var network))
        {
            o.KnownIPNetworks.Add(network);
        }
        else if (IPAddress.TryParse(entry, out var address))
        {
            o.KnownProxies.Add(address);
        }
        else
        {
            throw new InvalidOperationException($"Launcher__TrustedProxies contiene un valore non valido: {entry}");
        }
    }
});

var app = builder.Build();

// --- Avvio: database, impostazioni, configurazione iniziale ---
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

var settingsService = app.Services.GetRequiredService<SettingsService>();
await settingsService.LoadAsync();

// 1.5.3, una volta sola: gli abbinamenti per nome con la rubrica RustDesk diventano scelte scritte sull'asset.
await using (var scope = app.Services.CreateAsyncScope())
{
    var frozen = await OwnPeerGatewayFreeze.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        settingsService.RustDesk.Enabled, DateTimeOffset.UtcNow, CancellationToken.None);
    if (frozen > 0)
    {
        app.Logger.LogInformation("Aggiornamento 1.5.3: {Count} asset con RustDesk installato ora hanno gateway e indirizzo localhost scritti nel portale", frozen);
    }

    // 1.5.7: le aggiuntive salvate con un indirizzo e senza IP ricevono l'IP del tunnel.
    var addresses = await ExtraAddressMigration.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), DateTimeOffset.UtcNow, CancellationToken.None);
    if (addresses > 0)
    {
        app.Logger.LogInformation("Aggiornamento 1.5.7: {Count} connessioni aggiuntive con indirizzo ora hanno l'IP del tunnel", addresses);
    }
}
settingsService.Changed += keys =>
{
    if (keys.Any(k => k.StartsWith("entra.", StringComparison.Ordinal)))
    {
        app.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(AuthSchemes.Entra);
        app.Services.GetRequiredService<IOptionsMonitorCache<JwtBearerOptions>>().TryRemove(AuthSchemes.Connector);
    }

    if (keys.Any(k => k.StartsWith("hudu.", StringComparison.Ordinal) || k.StartsWith("rustdesk.api", StringComparison.Ordinal) || k == SettingKeys.RustDeskAddressBook))
    {
        app.Services.GetRequiredService<SyncService>().RequestSync();
    }
};

var setupToken = await app.Services.GetRequiredService<SetupState>().InitializeAsync(launcherOptions.SetupToken, CancellationToken.None);
if (setupToken is not null)
{
    app.Logger.LogWarning("CONFIGURAZIONE INIZIALE: nessun amministratore. Apri /Setup e usa il token {SetupToken}", setupToken);
}

// --- Pipeline HTTP ---
// Le intestazioni X-Original-* le scrive il middleware dei proxy fidati: quelle inviate dal client (o da un
// proxy non fidato) si scartano, altrimenti un indirizzo esterno potrebbe sembrare interno.
app.Use((http, next) =>
{
    http.Request.Headers.Remove("X-Original-For");
    http.Request.Headers.Remove("X-Original-Proto");
    http.Request.Headers.Remove("X-Original-Host");
    return next(http);
});
app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (launcherOptions.AllowHttp)
{
    app.Logger.LogWarning("MODALITÀ TEST: Launcher__AllowHttp è attivo, il portale accetta HTTP. Da disattivare prima della pubblicazione.");
}
else if (!app.Environment.IsDevelopment())
{
    // In produzione si arriva solo in HTTPS tramite il WAF. Se la richiesta risulta HTTP, il WAF non
    // invia X-Forwarded-Proto o il suo IP non è in Launcher__TrustedProxies: meglio dirlo chiaramente.
    app.Use(async (http, next) =>
    {
        if (!http.Request.IsHttps && !http.Request.Path.StartsWithSegments("/healthz"))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync(
                "Il portale va raggiunto in HTTPS tramite il proxy. Verificare che il proxy invii X-Forwarded-Proto " +
                $"e che il suo indirizzo ({http.Connection.RemoteIpAddress}) sia in Launcher__TrustedProxies.");
            return;
        }

        await next(http);
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Pagine di errore solo per il browser: le API restituiscono il codice così com'è.
app.UseWhen(
    http => !http.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
app.UseStaticFiles(new StaticFileOptions
{
    // CSS e JavaScript hanno la versione nell'indirizzo (?v=…, cambia a ogni modifica): il browser li
    // tiene per un anno senza richiederli. Gli altri file statici (icone dell'app) per un giorno.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=86400",
});
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous().DisableRateLimiting();
app.MapBrandingEndpoints();
app.MapLauncherApi();
app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Esposto per i test di integrazione.</summary>
public partial class Program;
