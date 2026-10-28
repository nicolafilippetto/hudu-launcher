using System.Security.Claims;
using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

/// <summary>
/// Utenti Microsoft 365: chi entra per la prima volta resta in attesa finché un amministratore non
/// lo approva. Bloccare o rimuovere toglie l'accesso entro un minuto, anche alle sessioni aperte.
/// </summary>
public sealed class UsersModel(AppDbContext db, AuditService audit) : LauncherPageModel
{
    public IReadOnlyList<PortalUser> Pending { get; private set; } = [];
    public IReadOnlyList<PortalUser> Approved { get; private set; } = [];
    public IReadOnlyList<PortalUser> Blocked { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var users = await db.PortalUsers.AsNoTracking().ToListAsync(ct);
        Pending = users.Where(u => u.Status == PortalUserStatus.Pending).OrderBy(u => u.FirstSeenAt).ToList();
        Approved = users.Where(u => u.Status == PortalUserStatus.Approved)
            .OrderByDescending(u => u.IsAdmin).ThenBy(u => u.DisplayName ?? u.Email, StringComparer.OrdinalIgnoreCase).ToList();
        Blocked = users.Where(u => u.Status == PortalUserStatus.Blocked).OrderByDescending(u => u.DecidedAt).ToList();
    }

    /// <summary>L'utente collegato (solo per gli accessi con Microsoft 365).</summary>
    public bool IsSelf(PortalUser user) =>
        user.ObjectId == User.FindFirstValue(LauncherClaims.ObjectId) && user.TenantId == User.FindFirstValue(LauncherClaims.TenantId);

    public Task<IActionResult> OnPostApproveAsync(int id, bool admin, CancellationToken ct) =>
        ChangeAsync(id, "user.approve", admin ? "approvato come amministratore" : "approvato", user =>
        {
            user.Status = PortalUserStatus.Approved;
            user.IsAdmin = admin;
            return $"{Label(user)} può accedere{(admin ? " come amministratore" : "")}.";
        }, protectSelf: false, ct);

    public Task<IActionResult> OnPostSetAdminAsync(int id, bool admin, CancellationToken ct) =>
        ChangeAsync(id, "user.role", admin ? "reso amministratore" : "non più amministratore", user =>
        {
            user.IsAdmin = admin;
            return admin ? $"{Label(user)} è ora amministratore." : $"{Label(user)} non è più amministratore.";
        }, protectSelf: true, ct);

    public Task<IActionResult> OnPostBlockAsync(int id, CancellationToken ct) =>
        ChangeAsync(id, "user.block", "bloccato", user =>
        {
            user.Status = PortalUserStatus.Blocked;
            user.IsAdmin = false;
            return $"{Label(user)} è bloccato: perde l'accesso entro un minuto e non potrà più entrare finché non viene riattivato.";
        }, protectSelf: true, ct);

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var user = await db.PortalUsers.FindAsync([id], ct);
        if (user is null)
        {
            return RedirectToPage();
        }

        if (IsSelf(user))
        {
            FlashError = "Non puoi rimuovere te stesso.";
            return RedirectToPage();
        }

        db.PortalUsers.Remove(user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("user.remove", Email, Target: user.Email, Details: "oid " + user.ObjectId), HttpContext, ct);
        Flash = $"{Label(user)} è stato rimosso: perde l'accesso entro un minuto e, se rientra, dovrà essere approvato di nuovo.";
        return RedirectToPage();
    }

    private async Task<IActionResult> ChangeAsync(int id, string action, string detail, Func<PortalUser, string> change, bool protectSelf, CancellationToken ct)
    {
        var user = await db.PortalUsers.FindAsync([id], ct);
        if (user is null)
        {
            return RedirectToPage();
        }

        if (protectSelf && IsSelf(user))
        {
            FlashError = "Non puoi cambiare il tuo stesso accesso: chiedi a un altro amministratore.";
            return RedirectToPage();
        }

        Flash = change(user);
        user.DecidedAt = DateTimeOffset.UtcNow;
        user.DecidedBy = Email;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry(action, Email, Target: user.Email, Details: $"{detail} (oid {user.ObjectId})"), HttpContext, ct);
        return RedirectToPage();
    }

    private static string Label(PortalUser user) => user.DisplayName is null ? user.Email : $"{user.DisplayName} ({user.Email})";
}
