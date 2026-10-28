using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed class LayoutForm
{
    public int LayoutId { get; set; }
    public bool Enabled { get; set; }
    public string? IpFieldLabel { get; set; }
    public string? OsFieldLabel { get; set; }
    public string? HostnameFieldLabel { get; set; }
    public string? ConsoleFieldLabel { get; set; }
    public string? RustDeskIdFieldLabel { get; set; }
    public string? DefaultProtocol { get; set; }
}

public sealed record LayoutRow(LayoutConfig Config, IReadOnlyList<string> Fields, string? Icon);

public sealed class LayoutsModel(AppDbContext db, AuditService audit, SyncService sync) : LauncherPageModel
{
    public IReadOnlyList<LayoutRow> Rows { get; private set; } = [];

    [BindProperty]
    public List<LayoutForm> Layouts { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var configs = await db.LayoutConfigs.AsNoTracking().ToListAsync(ct);
        var cached = await db.Layouts.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
        Rows = configs
            .OrderByDescending(c => c.Enabled)
            .ThenBy(c => c.LayoutName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new LayoutRow(
                c,
                cached.TryGetValue(c.LayoutId, out var l) ? l.FieldLabels.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [],
                cached.TryGetValue(c.LayoutId, out var li) ? li.Icon : null))
            .ToList();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var configs = await db.LayoutConfigs.ToDictionaryAsync(c => c.LayoutId, ct);
        var cached = await db.Layouts.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.FieldLabels.Split('\n').ToHashSet(StringComparer.Ordinal), ct);
        string? Field(int layoutId, string? value) =>
            !string.IsNullOrWhiteSpace(value) && cached.TryGetValue(layoutId, out var fields) && fields.Contains(value) ? value : null;

        foreach (var form in Layouts)
        {
            if (!configs.TryGetValue(form.LayoutId, out var config))
            {
                continue;
            }

            config.Enabled = form.Enabled;
            config.IpFieldLabel = Field(form.LayoutId, form.IpFieldLabel);
            config.OsFieldLabel = Field(form.LayoutId, form.OsFieldLabel);
            config.HostnameFieldLabel = Field(form.LayoutId, form.HostnameFieldLabel);
            config.ConsoleFieldLabel = Field(form.LayoutId, form.ConsoleFieldLabel);
            config.RustDeskIdFieldLabel = Field(form.LayoutId, form.RustDeskIdFieldLabel);
            config.DefaultProtocol = Enum.TryParse<ConnectionProtocol>(form.DefaultProtocol, out var p) && Enum.IsDefined(p) ? p : null;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("layouts.update", Email, Details: $"{configs.Values.Count(c => c.Enabled)} layout abilitati"), HttpContext, ct);
        sync.RequestSync();
        Flash = "Layout salvati. La cache verrà aggiornata con la prossima sincronizzazione, avviata ora.";
        return RedirectToPage();
    }
}
