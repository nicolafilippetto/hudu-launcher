using System.ComponentModel.DataAnnotations;
using Launcher.Server.Audit;
using Launcher.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed record AdminRow(AdminEntry Entry, IdentityUser? Local, bool LockedOut);

public sealed class AdminsModel(AppDbContext db, UserManager<IdentityUser> users, AuditService audit) : LauncherPageModel
{
    public IReadOnlyList<AdminRow> Admins { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var entries = await db.Admins.AsNoTracking().OrderBy(a => a.Email).ToListAsync(ct);
        var locals = await db.Users.AsNoTracking().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        Admins = entries.Select(e =>
        {
            var local = locals.FirstOrDefault(u => string.Equals(u.Email, e.Email, StringComparison.OrdinalIgnoreCase));
            return new AdminRow(e, local, local?.LockoutEnd > now);
        }).ToList();
    }

    public async Task<IActionResult> OnPostAddAsync(string? email, string? displayName, string? password, CancellationToken ct)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || !new EmailAddressAttribute().IsValid(normalized))
        {
            FlashError = "Email non valida.";
            return RedirectToPage();
        }

        if (await db.Admins.AnyAsync(a => a.Email == normalized, ct))
        {
            FlashError = "Esiste già un accesso di emergenza con questa email.";
            return RedirectToPage();
        }

        // Prima l'utente locale: se la password non va bene, non si aggiunge nulla.
        var error = string.IsNullOrEmpty(password) ? "Indicare la password." : await SetLocalPasswordAsync(normalized, password);
        if (error is not null)
        {
            FlashError = error;
            return RedirectToPage();
        }

        db.Admins.Add(new AdminEntry
        {
            Email = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = Email,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("admin.add", Email, Target: normalized), HttpContext, ct);
        Flash = $"Accesso di emergenza creato per {normalized}: al primo accesso locale configurerà l'app di autenticazione.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var entry = await db.Admins.FindAsync([id], ct);
        if (entry is null)
        {
            return RedirectToPage();
        }

        if (string.Equals(entry.Email, Email, StringComparison.OrdinalIgnoreCase))
        {
            FlashError = "Non puoi rimuovere te stesso.";
            return RedirectToPage();
        }

        if (!await OtherUsableBreakGlassAsync(entry.Email, ct))
        {
            FlashError = "Deve restare almeno un altro accesso di emergenza utilizzabile (con password).";
            return RedirectToPage();
        }

        var local = await users.FindByEmailAsync(entry.Email);
        if (local is not null)
        {
            await users.DeleteAsync(local);
        }

        db.Admins.Remove(entry);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("admin.remove", Email, Target: entry.Email), HttpContext, ct);
        Flash = $"Accesso di emergenza di {entry.Email} eliminato.";
        return RedirectToPage();
    }

    /// <summary>Crea o reimposta l'accesso locale. Al primo accesso dovrà configurare la MFA.</summary>
    public async Task<IActionResult> OnPostSetLocalAsync(int id, string? password, CancellationToken ct)
    {
        var entry = await db.Admins.FindAsync([id], ct);
        if (entry is null || string.IsNullOrEmpty(password))
        {
            FlashError = "Indicare una password.";
            return RedirectToPage();
        }

        var error = await SetLocalPasswordAsync(entry.Email, password);
        if (error is not null)
        {
            FlashError = error;
            return RedirectToPage();
        }

        await audit.WriteAsync(new AuditEntry("admin.local", Email, Target: entry.Email, Details: "password impostata"), HttpContext, ct);
        Flash = $"Accesso locale impostato per {entry.Email}. Comunica la password in modo sicuro.";
        return RedirectToPage();
    }

    /// <summary>Crea l'utente locale o ne reimposta la password; restituisce l'errore da mostrare.</summary>
    private async Task<string?> SetLocalPasswordAsync(string email, string password)
    {
        var user = await users.FindByEmailAsync(email);
        IdentityResult result;
        if (user is null)
        {
            user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            result = await users.CreateAsync(user, password);
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            result = await users.ResetPasswordAsync(user, token, password);
            if (result.Succeeded)
            {
                await users.SetLockoutEndDateAsync(user, null);
                await users.ResetAccessFailedCountAsync(user);
            }
        }

        return result.Succeeded ? null : string.Join(" ", result.Errors.Select(AdminErrors.Translate));
    }

    public async Task<IActionResult> OnPostResetMfaAsync(int id, CancellationToken ct) =>
        await WithLocalUserAsync(id, async user =>
        {
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            return "MFA azzerata: al prossimo accesso locale andrà riconfigurata.";
        }, "MFA azzerata", ct);

    public async Task<IActionResult> OnPostRemoveLocalAsync(int id, CancellationToken ct)
    {
        var entry = await db.Admins.FindAsync([id], ct);
        if (entry is not null && !await OtherUsableBreakGlassAsync(entry.Email, ct))
        {
            FlashError = "Deve restare almeno un altro accesso di emergenza utilizzabile (con password).";
            return RedirectToPage();
        }

        return await WithLocalUserAsync(id, async user =>
        {
            await users.DeleteAsync(user);
            return "Password rimossa: questo accesso di emergenza non è più utilizzabile.";
        }, "accesso locale rimosso", ct);
    }

    /// <summary>Esiste un altro accesso di emergenza con password (per non restare chiusi fuori).</summary>
    private async Task<bool> OtherUsableBreakGlassAsync(string email, CancellationToken ct)
    {
        var others = await db.Admins.Where(a => a.Email != email).Select(a => a.Email.ToUpper()).ToListAsync(ct);
        return await db.Users.AnyAsync(u => u.NormalizedEmail != null && others.Contains(u.NormalizedEmail), ct);
    }

    public async Task<IActionResult> OnPostUnlockAsync(int id, CancellationToken ct) =>
        await WithLocalUserAsync(id, async user =>
        {
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            return "Account sbloccato.";
        }, "sbloccato", ct);

    private async Task<IActionResult> WithLocalUserAsync(int id, Func<IdentityUser, Task<string>> action, string auditDetail, CancellationToken ct)
    {
        var entry = await db.Admins.FindAsync([id], ct);
        var user = entry is null ? null : await users.FindByEmailAsync(entry.Email);
        if (user is null)
        {
            FlashError = "Nessun accesso locale per questo amministratore.";
            return RedirectToPage();
        }

        Flash = await action(user);
        await audit.WriteAsync(new AuditEntry("admin.local", Email, Target: entry!.Email, Details: auditDetail), HttpContext, ct);
        return RedirectToPage();
    }
}
