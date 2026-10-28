using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Launcher.Server.Pages.Account;

public sealed class LoginModel(SettingsService settings, SetupState setup) : LauncherPageModel
{
    public bool EntraAvailable => settings.Entra.IsConfigured;

    public bool LocalAvailable { get; private set; }

    public string? Message { get; private set; }

    public string? ReturnUrl { get; private set; }

    public IActionResult OnGet(string? returnUrl, string? error, bool loggedOut)
    {
        if (setup.IsActive)
        {
            return RedirectToPage("/Setup");
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        ReturnUrl = SafeReturnUrl(returnUrl);
        LocalAvailable = NetworkAllowList.Contains(settings.Portal.LocalLoginNetworks, HttpContext.Connection.RemoteIpAddress);
        Message = error switch
        {
            "entra-not-configured" => "L'accesso con Microsoft 365 non è ancora configurato. Un amministratore deve completarlo dalla sezione admin.",
            "entra-failed" => "Accesso con Microsoft 365 non riuscito o annullato. Se non sei abilitato all'app, chiedi l'accesso al reparto IT.",
            _ => loggedOut ? "Sei uscito dal portale." : null,
        };
        return Page();
    }

    public IActionResult OnPostEntra(string? returnUrl)
    {
        if (!settings.Entra.IsConfigured)
        {
            return RedirectToPage(new { error = "entra-not-configured" });
        }

        return Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, AuthSchemes.Entra);
    }

    internal string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && !returnUrl.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : "/";
}
