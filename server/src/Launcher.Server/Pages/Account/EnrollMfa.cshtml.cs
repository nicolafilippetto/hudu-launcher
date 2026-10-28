using Launcher.Server.Endpoints;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QRCoder;

namespace Launcher.Server.Pages.Account;

/// <summary>Primo accesso locale: configurazione obbligatoria dell'app di autenticazione (TOTP).</summary>
[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class EnrollMfaModel(LocalSignIn signIn, UserManager<IdentityUser> users, SettingsService settings) : LauncherPageModel
{
    [BindProperty]
    public string? Code { get; set; }

    public string? SharedKey { get; private set; }

    public string? QrDataUri { get; private set; }

    public string? Error { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await signIn.GetPendingUserAsync(HttpContext);
        if (user is null)
        {
            return RedirectToPage("/Account/LocalLogin");
        }

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/Mfa");
        }

        await LoadKeyAsync(user, reset: true);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetPendingUserAsync(HttpContext);
        if (user is null)
        {
            return RedirectToPage("/Account/LocalLogin");
        }

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/Mfa");
        }

        var code = LocalSignIn.NormalizeCode(Code);
        if (code.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            Error = "Codice non valido: controlla che l'ora del telefono sia corretta e riprova.";
            await LoadKeyAsync(user, reset: false);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        await users.ResetAccessFailedCountAsync(user);
        RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8))?.ToList() ?? [];
        await signIn.CompleteAsync(HttpContext, user, "password + MFA (prima configurazione)");
        return Page();
    }

    private async Task LoadKeyAsync(IdentityUser user, bool reset)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key) || reset)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        SharedKey = string.Join(' ', key!.Chunk(4).Select(c => new string(c)));
        var issuer = Uri.EscapeDataString(settings.Portal.Name);
        var uri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer={issuer}&digits=6";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(5, "#000000", "#ffffff", drawQuietZones: true);
        QrDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }
}
