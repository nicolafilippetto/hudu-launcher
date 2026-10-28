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

/// <summary>Costruisce l'identità del portale e decide il ruolo (admin se l'email è nell'elenco).</summary>
public sealed class PortalPrincipalFactory(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<bool> IsAdminAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = email.Trim().ToLowerInvariant();
        return await db.Admins.AnyAsync(a => a.Email == normalized, cancellationToken);
    }

    public async Task<ClaimsPrincipal> CreateAsync(string email, string? name, string? objectId, string authMethod, CancellationToken cancellationToken = default)
    {
        var isAdmin = await IsAdminAsync(email, cancellationToken);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
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

        if (!string.IsNullOrWhiteSpace(objectId))
        {
            claims.Add(new Claim(LauncherClaims.ObjectId, objectId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Portal, ClaimTypes.Name, ClaimTypes.Role));
    }
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
            options.ClientSecret = entra.ClientSecret;
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

    public override async Task TokenValidated(Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var tenantId = principal.FindFirstValue("tid");
        var email = LauncherClaims.EmailFromEntra(principal);
        if (!string.Equals(tenantId, settings.Entra.TenantId, StringComparison.OrdinalIgnoreCase) || email is null)
        {
            await audit.WriteAsync(new AuditEntry("login", email, Success: false, Details: "Token Entra senza email o di un altro tenant"), context.HttpContext);
            context.Fail("Utente non valido.");
            return;
        }

        context.Principal = await principals.CreateAsync(
            email,
            principal.FindFirstValue("name"),
            principal.FindFirstValue("oid"),
            LauncherClaims.MethodEntra,
            context.HttpContext.RequestAborted);
        context.Properties!.IsPersistent = false;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(settings.Portal.SessionHours);
        await audit.WriteAsync(new AuditEntry("login", email, Details: "Microsoft 365"), context.HttpContext);
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        context.Response.Redirect("/Account/Login?error=entra-failed");
        context.HandleResponse();
        return Task.CompletedTask;
    }
}

/// <summary>Il token del connettore deve contenere lo scope del Launcher e venire dal tenant giusto.</summary>
public sealed class ConnectorJwtEvents(SettingsService settings) : JwtBearerEvents
{
    public override Task TokenValidated(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var scopes = (principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tenantId = principal.FindFirstValue("tid");
        var email = LauncherClaims.EmailFromEntra(principal);
        if (!scopes.Contains(EntraSettings.ConnectorScopeName, StringComparer.Ordinal)
            || !string.Equals(tenantId, settings.Entra.TenantId, StringComparison.OrdinalIgnoreCase)
            || email is null)
        {
            context.Fail("Token non valido per il Launcher.");
            return Task.CompletedTask;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, principal.FindFirstValue("name") ?? email)],
            AuthSchemes.Connector, ClaimTypes.Name, ClaimTypes.Role);
        context.Principal = new ClaimsPrincipal(identity);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Ricontrolla la sessione ogni 5 minuti: un admin rimosso dall'elenco perde subito il ruolo,
/// un account locale eliminato perde la sessione.
/// </summary>
public sealed class PortalCookieEvents(PortalPrincipalFactory principals, IDbContextFactory<AppDbContext> dbFactory) : CookieAuthenticationEvents
{
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromMinutes(5);

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

        var email = principal.GetEmail();
        var method = principal.FindFirstValue(LauncherClaims.AuthMethod);
        if (email is null)
        {
            context.RejectPrincipal();
            return;
        }

        if (method == LauncherClaims.MethodLocal)
        {
            await using var db = await dbFactory.CreateDbContextAsync(context.HttpContext.RequestAborted);
            var normalized = email.ToUpperInvariant();
            var exists = await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, context.HttpContext.RequestAborted);
            if (!exists || !await principals.IsAdminAsync(email, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.Portal);
                return;
            }
        }

        context.ReplacePrincipal(await principals.CreateAsync(
            email,
            principal.FindFirstValue(ClaimTypes.Name),
            principal.FindFirstValue(LauncherClaims.ObjectId),
            method ?? LauncherClaims.MethodEntra,
            context.HttpContext.RequestAborted));
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
