using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Launcher.Server.Audit;
using Launcher.Server.Endpoints;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Launcher.Server.Pages.Account;

/// <summary>
/// Accesso locale per gli admin (emergenza, se 365 non funziona). Solo dalle reti ammesse, con
/// blocco dopo 5 tentativi e MFA TOTP obbligatoria al passo successivo.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class LocalLoginModel(SettingsService settings, UserManager<IdentityUser> users, PortalPrincipalFactory principals, AuditService audit) : LauncherPageModel
{
    public const string PendingUserClaim = "pending_user";

    [BindProperty, Required, EmailAddress]
    public string LoginEmail { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public string? Error { get; private set; }

    public bool Allowed { get; private set; }

    public IActionResult OnGet()
    {
        Allowed = IsAllowedNetwork();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Allowed = IsAllowedNetwork();
        if (!Allowed)
        {
            return Page();
        }

        const string generic = "Email o password non corretti, oppure account bloccato temporaneamente.";
        if (!ModelState.IsValid)
        {
            Error = generic;
            return Page();
        }

        var email = LoginEmail.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(email);
        if (user is null || await users.IsLockedOutAsync(user) || !await principals.IsBreakGlassAsync(email))
        {
            await audit.WriteAsync(new AuditEntry("login.local", email, Success: false, Details: user is null ? "utente inesistente" : "bloccato o non admin"), HttpContext);
            Error = generic;
            return Page();
        }

        if (!await users.CheckPasswordAsync(user, Password))
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(new AuditEntry("login.local", email, Success: false, Details: "password errata"), HttpContext);
            Error = generic;
            return Page();
        }

        // Il contatore dei tentativi si azzera solo dopo il secondo fattore (pagina Mfa): azzerarlo qui
        // permetterebbe a chi conosce la password di provare codici MFA senza mai bloccare l'account.
        var pending = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PendingUserClaim, user.Id)], AuthSchemes.MfaPending));
        await HttpContext.SignInAsync(AuthSchemes.MfaPending, pending);
        return RedirectToPage(user.TwoFactorEnabled ? "/Account/Mfa" : "/Account/EnrollMfa");
    }

    private bool IsAllowedNetwork() =>
        NetworkAllowList.IsAllowed(settings.Portal.LocalLoginNetworks, HttpContext);
}
