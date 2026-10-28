using System.ComponentModel.DataAnnotations;
using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Endpoints;
using Launcher.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Launcher.Server.Pages;

/// <summary>Creazione del primo amministratore (account locale con MFA).</summary>
[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class SetupModel(SetupState setup, UserManager<IdentityUser> users, AppDbContext db, AuditService audit) : LauncherPageModel
{
    [BindProperty, Required]
    public string Token { get; set; } = "";

    [BindProperty, Required, EmailAddress]
    public string AdminEmail { get; set; } = "";

    [BindProperty]
    public string? DisplayName { get; set; }

    [BindProperty, Required]
    public string Password { get; set; } = "";

    [BindProperty, Required]
    public string ConfirmPassword { get; set; } = "";

    public List<string> Errors { get; } = [];

    public IActionResult OnGet() => setup.IsActive ? Page() : RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        if (!setup.IsActive)
        {
            return RedirectToPage("/Index");
        }

        if (!setup.Verify(Token))
        {
            Errors.Add("Token di configurazione non valido: lo trovi nei log del container all'avvio.");
            await audit.WriteAsync(new AuditEntry("setup", AdminEmail, Success: false, Details: "token errato"), HttpContext);
            return Page();
        }

        if (!ModelState.IsValid)
        {
            Errors.Add("Compila tutti i campi con un'email valida.");
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            Errors.Add("Le due password non coincidono.");
            return Page();
        }

        var email = AdminEmail.Trim().ToLowerInvariant();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            Errors.AddRange(result.Errors.Select(e => AdminErrors.Translate(e)));
            return Page();
        }

        db.Admins.Add(new AdminEntry
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "configurazione iniziale",
        });
        await db.SaveChangesAsync();
        setup.Complete();
        await audit.WriteAsync(new AuditEntry("setup", email, Details: "Primo amministratore creato"), HttpContext);
        Flash = "Amministratore creato. Accedi e configura la verifica in due passaggi.";
        return RedirectToPage("/Account/LocalLogin");
    }
}

public static class AdminErrors
{
    public static string Translate(IdentityError error) => error.Code switch
    {
        "PasswordTooShort" => "La password deve avere almeno 14 caratteri.",
        "PasswordRequiresUpper" => "La password deve contenere una lettera maiuscola.",
        "PasswordRequiresLower" => "La password deve contenere una lettera minuscola.",
        "PasswordRequiresDigit" => "La password deve contenere un numero.",
        "DuplicateEmail" or "DuplicateUserName" => "Esiste già un account locale con questa email.",
        "InvalidEmail" => "Email non valida.",
        _ => error.Description,
    };
}
