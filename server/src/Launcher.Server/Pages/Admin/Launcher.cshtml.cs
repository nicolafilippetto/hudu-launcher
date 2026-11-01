using Launcher.Server.Audit;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed record LauncherUserRow(string Email, string? Name, LauncherStatus Status);

/// <summary>
/// Launcher incluso nel portale, versione minima obbligatoria e chi usa quale versione. La versione e
/// il PC li comunica il Launcher a ogni connessione; gli utenti approvati che non l'hanno mai usato
/// compaiono come "mai usato".
/// </summary>
public sealed class LauncherModel(AppDbContext db, LauncherReleases releases, SettingsService settings, AuditService audit) : LauncherPageModel
{
    public LauncherBundle? Bundle { get; private set; }

    public string? Minimum { get; private set; }

    public IReadOnlyList<LauncherUserRow> Users { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Bundle = releases.Bundle;
        Minimum = settings.LauncherMinVersion;
        var clients = await db.LauncherClients.AsNoTracking().ToDictionaryAsync(c => c.Email, ct);
        var approved = await db.PortalUsers.AsNoTracking().Where(u => u.Status == PortalUserStatus.Approved)
            .Select(u => new { u.Email, u.DisplayName }).ToListAsync(ct);
        var names = approved.GroupBy(u => u.Email.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().DisplayName);
        Users = names.Keys.Union(clients.Keys)
            .Select(email => new LauncherUserRow(email, names.GetValueOrDefault(email),
                LauncherStatus.Of(clients.GetValueOrDefault(email), Bundle?.Version, Minimum)))
            .OrderBy(r => r.Status.State)
            .ThenByDescending(r => r.Status.LastSeen)
            .ThenBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostMinimumAsync(string? version, CancellationToken ct)
    {
        var value = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (value is not null && !LauncherVersion.IsValid(value))
        {
            FlashError = "Versione non valida: usa il formato 1.4.0.";
            return RedirectToPage();
        }

        // Un minimo più alto del Launcher incluso bloccherebbe tutti senza un aggiornamento da installare.
        if (value is not null && (releases.Bundle is not { } bundle || LauncherVersion.Compare(value, bundle.Version) > 0))
        {
            FlashError = releases.Bundle is null
                ? "Il portale non include il Launcher: senza un aggiornamento da distribuire non si può imporre una versione minima."
                : $"La versione minima non può superare quella inclusa nel portale ({releases.Bundle.Version}).";
            return RedirectToPage();
        }

        await settings.SaveAsync(new Dictionary<string, string?> { [SettingKeys.LauncherMinVersion] = value }, Email, ct);
        await audit.WriteAsync(new AuditEntry("settings.update", Email, Details: $"Launcher: versione minima {value ?? "nessuna"}"), HttpContext, ct);
        Flash = value is null
            ? "Nessuna versione minima: il portale accetta qualsiasi Launcher."
            : $"Da ora il portale accetta solo Launcher dalla versione {value}: gli altri ricevono la richiesta di aggiornamento.";
        return RedirectToPage();
    }
}
