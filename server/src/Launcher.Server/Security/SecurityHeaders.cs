using System.Security.Cryptography;
using System.Text;
using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Security;

/// <summary>Intestazioni di sicurezza per tutte le risposte.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self' https://login.microsoftonline.com";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), clipboard-write=(self)";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (context.Request.IsHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            }

            var path = context.Request.Path;
            if (!path.StartsWithSegments("/css") && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/img") && !path.StartsWithSegments("/branding"))
            {
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// Configurazione iniziale: finché non esiste nessun admin, la pagina /Setup permette di creare il
/// primo account locale presentando il token scritto nei log all'avvio (o fornito via variabile).
/// </summary>
public sealed class SetupState(IDbContextFactory<AppDbContext> dbFactory)
{
    private byte[]? _tokenHash;

    public bool IsActive => _tokenHash is not null;

    public async Task<string?> InitializeAsync(string? configuredToken, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Admins.AnyAsync(ct))
        {
            _tokenHash = null;
            return null;
        }

        var token = string.IsNullOrWhiteSpace(configuredToken)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            : configuredToken.Trim();
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return string.IsNullOrWhiteSpace(configuredToken) ? token : "(quello impostato in Launcher__SetupToken)";
    }

    public bool Verify(string? token)
    {
        var expected = _tokenHash;
        if (expected is null || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
    }

    public void Complete() => _tokenHash = null;
}
