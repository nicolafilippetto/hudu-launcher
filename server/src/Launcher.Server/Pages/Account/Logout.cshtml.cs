using Launcher.Server.Audit;
using Launcher.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Launcher.Server.Pages.Account;

public sealed class LogoutModel(AuditService audit) : LauncherPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        var email = User.GetEmail();
        await HttpContext.SignOutAsync(AuthSchemes.Portal);
        await HttpContext.SignOutAsync(AuthSchemes.MfaPending);
        await audit.WriteAsync(new AuditEntry("logout", email), HttpContext);
        return RedirectToPage("/Account/Login", new { loggedOut = true });
    }
}
