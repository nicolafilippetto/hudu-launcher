using System.Net;
using System.Security.Claims;
using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Launcher.Server.Security;

public static class AuthSchemes
{
    /// <summary>Sessione del portale (cookie).</summary>
    public const string Portal = "Launcher";
    /// <summary>Login con Microsoft 365.</summary>
    public const string Entra = "Entra";
    /// <summary>Token Entra del connettore Windows.</summary>
    public const string Connector = "Connector";
    /// <summary>Passaggio intermedio del login locale: password verificata, MFA da fare.</summary>
    public const string MfaPending = "MfaPending";
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Technician = "Technician";
}

public static class Policies
{
    public const string Technician = "Technician";
    public const string Admin = "Admin";
    public const string Connector = "Connector";
}

public static class LauncherClaims
{
    public const string AuthMethod = "auth_method";
    public const string ObjectId = "oid";
    public const string TenantId = "tid";
    public const string ValidatedAt = "validated_at";
    public const string MethodEntra = "entra";
    public const string MethodLocal = "local";

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email);

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    /// <summary>Email dell'utente da un token Entra: email, poi preferred_username, poi upn.</summary>
    public static string? EmailFromEntra(ClaimsPrincipal principal)
    {
        foreach (var type in new[] { "email", "preferred_username", "upn", ClaimTypes.Email, ClaimTypes.Upn })
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value) && value.Contains('@', StringComparison.Ordinal))
            {
                return value.Trim().ToLowerInvariant();
            }
        }

        return null;
    }
}

/// <summary>
/// Identità del portale. Gli utenti Microsoft 365 sono riconosciuti da tenant + object ID e
/// devono essere approvati da un amministratore; gli accessi di emergenza locali sono sempre admin.
/// </summary>
public sealed class PortalPrincipalFactory(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>true se l'email è un accesso di emergenza (break-glass) locale.</summary>
    public async Task<bool> IsBreakGlassAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = email.Trim().ToLowerInvariant();
        return await db.Admins.AnyAsync(a => a.Email == normalized, cancellationToken);
    }

    /// <summary>Utente 365 approvato, oppure null (sconosciuto, in attesa o bloccato).</summary>
    public async Task<PortalUser?> FindApprovedAsync(string? tenantId, string? objectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(objectId))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PortalUsers.AsNoTracking().FirstOrDefaultAsync(
            u => u.TenantId == tenantId && u.ObjectId == objectId && u.Status == PortalUserStatus.Approved, cancellationToken);
    }

    /// <summary>
    /// Registra un accesso con Microsoft 365: al primo accesso crea l'utente in attesa di
    /// approvazione, poi aggiorna email, nome, data e IP. Restituisce l'utente e se è nuovo.
    /// </summary>
    public async Task<(PortalUser User, bool IsNew)> RegisterEntraLoginAsync(
        string tenantId, string objectId, string email, string? name, string? ip, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var user = await db.PortalUsers.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.ObjectId == objectId, cancellationToken);
        var isNew = user is null;
        if (user is null)
        {
            user = new PortalUser { TenantId = tenantId, ObjectId = objectId, Email = email, Status = PortalUserStatus.Pending, FirstSeenAt = now };
            db.PortalUsers.Add(user);
        }

        user.Email = email;
        user.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 256)];
        user.LastLoginAt = now;
        user.LastIp = ip;
        await db.SaveChangesAsync(cancellationToken);
        return (user, isNew);
    }

    public static ClaimsPrincipal Create(string email, string? name, string authMethod, bool isAdmin, string? tenantId = null, string? objectId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, objectId ?? email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? email : name),
            new(ClaimTypes.Role, Roles.Technician),
            new(LauncherClaims.AuthMethod, authMethod),
            new(LauncherClaims.ValidatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        }

        if (!string.IsNullOrWhiteSpace(objectId) && !string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim(LauncherClaims.ObjectId, objectId));
            claims.Add(new Claim(LauncherClaims.TenantId, tenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Portal, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static ClaimsPrincipal Create(PortalUser user) =>
        Create(user.Email, user.DisplayName, LauncherClaims.MethodEntra, user.IsAdmin, user.TenantId, user.ObjectId);
}

/// <summary>
/// Configura OIDC (portale) e JwtBearer (connettore) leggendo i dati di Entra dalle impostazioni.
/// Se Entra non è configurato si usa una configurazione statica vuota, così gli handler non
/// tentano di scaricare metadati e il login 365 risulta semplicemente non disponibile.
/// </summary>
public sealed class EntraOptionsSetup(SettingsService settings) :
    IConfigureNamedOptions<OpenIdConnectOptions>,
    IConfigureNamedOptions<JwtBearerOptions>
{
    private const string PlaceholderClientId = "00000000-0000-0000-0000-000000000000";

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != AuthSchemes.Entra)
        {
            return;
        }

        var entra = settings.Entra;
        options.SignInScheme = AuthSchemes.Portal;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.EventsType = typeof(EntraOidcEvents);

        if (entra.IsConfigured)
        {
            options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
            options.ClientId = entra.ClientId;
            // Con il certificato non si invia alcun segreto: l'asserzione si aggiunge in AuthorizationCodeReceived.
            options.ClientSecret = entra.UsesCertificate ? null : entra.ClientSecret;
            options.TokenValidationParameters.ValidIssuer = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
        }
        else
        {
            options.ClientId = PlaceholderClientId;
            options.Configuration = new OpenIdConnectConfiguration();
        }
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != AuthSchemes.Connector)
        {
            return;
        }

        var entra = settings.Entra;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.EventsType = typeof(ConnectorJwtEvents);
        if (entra.IsConfigured)
        {
            options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
            options.TokenValidationParameters.ValidAudiences = [entra.ClientId!, $"api://{entra.ClientId}"];
            options.TokenValidationParameters.ValidIssuers =
            [
                $"https://login.microsoftonline.com/{entra.TenantId}/v2.0",
                $"https://sts.windows.net/{entra.TenantId}/",
            ];
        }
        else
        {
            options.Configuration = new OpenIdConnectConfiguration();
            options.TokenValidationParameters.ValidAudience = PlaceholderClientId;
        }
    }
}

/// <summary>Dopo il login 365 sostituisce l'identità Entra con quella del portale.</summary>
public sealed class EntraOidcEvents(PortalPrincipalFactory principals, SettingsService settings, AuditService audit) : OpenIdConnectEvents
{
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        if (!settings.Entra.IsConfigured)
        {
            context.Response.Redirect("/Account/Login?error=entra-not-configured");
            context.HandleResponse();
        }

        return Task.CompletedTask;
    }

    /// <summary>Scambio del codice: con il certificato il portale si presenta con un'asserzione firmata.</summary>
    public override Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        var entra = settings.Entra;
        if (entra.UsesCertificate && context.TokenEndpointRequest is { } request)
        {
            request.ClientSecret = null;
            request.ClientAssertionType = EntraCredential.AssertionType;
            request.ClientAssertion = EntraCredential.CreateAssertion(entra.TenantId!, entra.ClientId!, entra.Certificate!, entra.CertificateKey!, TimeProvider.System);
        }

        return Task.CompletedTask;
    }

    public override async Task TokenValidated(Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var tenantId = principal.FindFirstValue("tid");
        var objectId = principal.FindFirstValue("oid");
        var email = LauncherClaims.EmailFromEntra(principal);
        if (!string.Equals(tenantId, settings.Entra.TenantId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(objectId) || email is null)
        {
            await audit.WriteAsync(new AuditEntry("login", email, Success: false, Details: "Token Entra senza identificativo o di un altro tenant"), context.HttpContext);
            context.Fail("Utente non valido.");
            return;
        }

        var (user, isNew) = await principals.RegisterEntraLoginAsync(
            tenantId!.ToLowerInvariant(), objectId, email, principal.FindFirstValue("name"),
            context.HttpContext.Connection.RemoteIpAddress?.ToString(), context.HttpContext.RequestAborted);
        if (isNew)
        {
            await audit.WriteAsync(new AuditEntry("user.request", email, Details: "Primo accesso con Microsoft 365: in attesa di approvazione"), context.HttpContext);
        }

        if (user.Status != PortalUserStatus.Approved)
        {
            // Autenticato da Microsoft ma non autorizzato: nessuna sessione, solo un messaggio.
            var blocked = user.Status == PortalUserStatus.Blocked;
            await audit.WriteAsync(new AuditEntry("login", email, Success: false, Details: blocked ? "utente bloccato" : "in attesa di approvazione"), context.HttpContext);
            context.Response.Redirect("/Account/Login?status=" + (blocked ? "blocked" : "pending"));
            context.HandleResponse();
            return;
        }

        context.Principal = PortalPrincipalFactory.Create(user);
        context.Properties!.IsPersistent = false;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(settings.Portal.SessionHours);
        await audit.WriteAsync(new AuditEntry("login", user.Email, Details: "Microsoft 365"), context.HttpContext);
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        context.Response.Redirect("/Account/Login?error=entra-failed");
        context.HandleResponse();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Il token del connettore deve contenere lo scope del Launcher, venire dal tenant giusto ed
/// essere di un utente approvato nel portale (riconosciuto dall'object ID, non dall'email).
/// </summary>
public sealed class ConnectorJwtEvents(SettingsService settings, PortalPrincipalFactory principals) : JwtBearerEvents
{
    public override async Task TokenValidated(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var scopes = (principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tenantId = principal.FindFirstValue("tid");
        if (!scopes.Contains(EntraSettings.ConnectorScopeName, StringComparer.Ordinal)
            || !string.Equals(tenantId, settings.Entra.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            context.Fail("Token non valido per il Launcher.");
            return;
        }

        var user = await principals.FindApprovedAsync(tenantId!.ToLowerInvariant(), principal.FindFirstValue("oid"), context.HttpContext.RequestAborted);
        if (user is null)
        {
            context.Fail("Utente non approvato nel portale.");
            return;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName ?? user.Email)],
            AuthSchemes.Connector, ClaimTypes.Name, ClaimTypes.Role);
        context.Principal = new ClaimsPrincipal(identity);
    }
}

/// <summary>
/// Ricontrolla la sessione ogni minuto: un utente bloccato o rimosso, o un accesso di emergenza
/// eliminato, perde la sessione; un cambio di ruolo vale subito.
/// </summary>
public sealed class PortalCookieEvents(PortalPrincipalFactory principals, IDbContextFactory<AppDbContext> dbFactory) : CookieAuthenticationEvents
{
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromMinutes(1);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal!;
        var validatedAt = long.TryParse(principal.FindFirstValue(LauncherClaims.ValidatedAt), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - validatedAt < RevalidateEvery)
        {
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var email = principal.GetEmail();
        ClaimsPrincipal? renewed = null;
        if (principal.FindFirstValue(LauncherClaims.AuthMethod) == LauncherClaims.MethodLocal && email is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var normalized = email.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct) && await principals.IsBreakGlassAsync(email, ct))
            {
                renewed = PortalPrincipalFactory.Create(email, principal.FindFirstValue(ClaimTypes.Name), LauncherClaims.MethodLocal, isAdmin: true);
            }
        }
        else
        {
            var user = await principals.FindApprovedAsync(principal.FindFirstValue(LauncherClaims.TenantId), principal.FindFirstValue(LauncherClaims.ObjectId), ct);
            if (user is not null)
            {
                renewed = PortalPrincipalFactory.Create(user);
            }
        }

        if (renewed is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.Portal);
            return;
        }

        context.ReplacePrincipal(renewed);
        context.ShouldRenew = true;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatus(context, StatusCodes.Status401Unauthorized);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatus(context, StatusCodes.Status403Forbidden);

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || context.Request.Headers.XRequestedWith == "fetch")
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Elenco di reti (CIDR) da cui è ammesso il login amministrativo locale.</summary>
public static class NetworkAllowList
{
    /// <summary>
    /// true se la richiesta passa da un proxy che non è in Launcher__TrustedProxies: l'intestazione
    /// X-Forwarded-For è rimasta non elaborata e l'indirizzo visto è quello del proxy, non del client.
    /// In quel caso le reti ammesse non si possono verificare.
    /// </summary>
    public static bool HasUntrustedProxy(HttpContext http) =>
        http.Request.Headers.ContainsKey("X-Forwarded-For") && !http.Request.Headers.ContainsKey("X-Original-For");

    /// <summary>Indirizzo del client ammesso per l'accesso locale (mai tramite un proxy non riconosciuto).</summary>
    public static bool IsAllowed(string networks, HttpContext http) =>
        !HasUntrustedProxy(http) && Contains(networks, http.Connection.RemoteIpAddress);

    public static bool Contains(string networks, IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var entry in networks.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry is "*" or "0.0.0.0/0")
            {
                return true;
            }

            if (IPNetwork.TryParse(entry, out var network) && network.Contains(address))
            {
                return true;
            }

            if (IPAddress.TryParse(entry, out var single) && single.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValid(string networks) =>
        networks.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(e => e is "*" || IPNetwork.TryParse(e, out _) || IPAddress.TryParse(e, out _));
}
