using Launcher.Server.Audit;
using Launcher.Server.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Launcher.Server.Pages.Account;

[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class MfaModel(LocalSignIn signIn, UserManager<IdentityUser> users, AuditService audit) : LauncherPageModel
{
    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public bool UseRecovery { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await signIn.GetPendingUserAsync(HttpContext);
        return user is null ? RedirectToPage("/Account/LocalLogin") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetPendingUserAsync(HttpContext);
        if (user is null)
        {
            return RedirectToPage("/Account/LocalLogin");
        }

        if (!user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/EnrollMfa");
        }

        var code = LocalSignIn.NormalizeCode(Code);
        var ok = UseRecovery
            ? (await users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded
            : code.Length == 6 && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!ok)
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(new AuditEntry("login.local", user.Email, Success: false, Details: "codice MFA errato"), HttpContext);
            Error = "Codice non valido.";
            return Page();
        }

        await users.ResetAccessFailedCountAsync(user);
        await signIn.CompleteAsync(HttpContext, user, UseRecovery ? "password + codice di recupero" : "password + MFA");
        return LocalRedirect("/Admin");
    }
}
