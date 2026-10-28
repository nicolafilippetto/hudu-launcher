using Launcher.Server.Data;
using Launcher.Server.Settings;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed class AuditModel(AppDbContext db, SettingsService settings) : LauncherPageModel
{
    public const int PageSize = 100;

    public IReadOnlyList<AuditEvent> Events { get; private set; } = [];
    public IReadOnlyList<string> Actions { get; private set; } = [];
    public string? FilterUser { get; private set; }
    public string? FilterAction { get; private set; }
    public string? FilterText { get; private set; }
    public bool OnlyFailures { get; private set; }
    public int PageNumber { get; private set; }
    public bool HasMore { get; private set; }
    public string TimeZone => settings.Portal.TimeZone;

    public async Task OnGetAsync(string? user, string? action, string? q, bool failures, int p, CancellationToken ct)
    {
        FilterUser = user?.Trim();
        FilterAction = action;
        FilterText = q?.Trim();
        OnlyFailures = failures;
        PageNumber = Math.Max(p, 1);

        IQueryable<AuditEvent> query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrEmpty(FilterUser))
        {
            query = query.Where(e => e.UserEmail != null && e.UserEmail.Contains(FilterUser.ToLower()));
        }

        if (!string.IsNullOrEmpty(FilterAction))
        {
            query = query.Where(e => e.Action == FilterAction);
        }

        if (!string.IsNullOrEmpty(FilterText))
        {
            query = query.Where(e =>
                (e.AssetName != null && e.AssetName.Contains(FilterText)) ||
                (e.CompanyName != null && e.CompanyName.Contains(FilterText)) ||
                (e.Target != null && e.Target.Contains(FilterText)) ||
                (e.Details != null && e.Details.Contains(FilterText)) ||
                (e.Ip != null && e.Ip.Contains(FilterText)));
        }

        if (OnlyFailures)
        {
            query = query.Where(e => !e.Success);
        }

        var rows = await query.OrderByDescending(e => e.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = rows.Count > PageSize;
        Events = rows.Take(PageSize).ToList();
        Actions = await db.AuditEvents.Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }
}
