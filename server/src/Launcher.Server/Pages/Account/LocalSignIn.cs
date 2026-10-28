using System.Security.Claims;
using Launcher.Server.Audit;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Launcher.Server.Pages.Account;

/// <summary>Passaggi comuni del login locale: utente in attesa di MFA e accesso finale.</summary>
public sealed class LocalSignIn(UserManager<IdentityUser> users, SettingsService settings, AuditService audit)
{
    public async Task<IdentityUser?> GetPendingUserAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(AuthSchemes.MfaPending);
        var userId = result.Principal?.FindFirstValue(LocalLoginModel.PendingUserClaim);
        if (userId is null)
        {
            return null;
        }

        var user = await users.FindByIdAsync(userId);
        return user is null || await users.IsLockedOutAsync(user) ? null : user;
    }

    public async Task CompleteAsync(HttpContext http, IdentityUser user, string detail)
    {
        await http.SignOutAsync(AuthSchemes.MfaPending);
        var principal = PortalPrincipalFactory.Create(user.Email!, user.Email, LauncherClaims.MethodLocal, isAdmin: true);
        await http.SignInAsync(AuthSchemes.Portal, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Min(settings.Portal.SessionHours, 4)),
        });
        await audit.WriteAsync(new AuditEntry("login.local", user.Email, Details: detail), http);
    }

    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
}
