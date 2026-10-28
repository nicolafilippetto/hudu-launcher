using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed record ChecklistItem(bool Ok, string Title, string Detail, string? Link);

public sealed class IndexModel(AppDbContext db, SettingsService settings, SyncService sync, Security.LauncherOptions options) : LauncherPageModel
{
    public SyncState? State { get; private set; }
    public bool SyncRunning => sync.IsRunning;
    public List<ChecklistItem> Checklist { get; } = [];
    public int CompaniesWithoutGateway { get; private set; }
    public int AssetsWithOverrides { get; private set; }
    public int ConnectionsLast7Days { get; private set; }
    public string TimeZone => settings.Portal.TimeZone;

    public async Task OnGetAsync(CancellationToken ct)
    {
        State = await db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var enabledLayouts = await db.LayoutConfigs.CountAsync(l => l.Enabled, ct);
        var admins = await db.Admins.CountAsync(ct);

        if (NetworkAllowList.HasUntrustedProxy(HttpContext))
        {
            Checklist.Add(new ChecklistItem(false, "Proxy non riconosciuto",
                $"Le richieste arrivano da un proxy ({HttpContext.Connection.RemoteIpAddress}) che non è in Launcher__TrustedProxies: " +
                "il portale non vede l'IP reale dei client, quindi limiti per IP e accesso locale non funzionano correttamente.", null));
        }

        if (options.AllowHttp)
        {
            Checklist.Add(new ChecklistItem(false, "Modalità test HTTP attiva",
                "Il portale accetta HTTP. Prima della pubblicazione togliere Launcher__AllowHttp dal compose.", null));
        }

        Checklist.Add(new ChecklistItem(settings.Entra.IsConfigured, "Microsoft 365",
            settings.Entra.IsConfigured ? "Configurato" : "Tenant, client ID e segreto da impostare", "/Admin/Settings#entra"));
        Checklist.Add(new ChecklistItem(settings.Hudu.IsConfigured, "Hudu",
            settings.Hudu.IsConfigured ? settings.Hudu.BaseUrl! : "Indirizzo e chiave API da impostare", "/Admin/Settings#hudu"));
        Checklist.Add(new ChecklistItem(settings.RustDesk.IsApiConfigured, "Rubrica RustDesk",
            settings.RustDesk.IsApiConfigured ? settings.RustDesk.ApiUrl! : "Facoltativa ma consigliata: abbina i dispositivi agli asset", "/Admin/Settings#rustdesk"));
        Checklist.Add(new ChecklistItem(!string.IsNullOrEmpty(settings.RustDesk.ConnectionPassword), "Password di connessione RustDesk",
            string.IsNullOrEmpty(settings.RustDesk.ConnectionPassword) ? "Senza, RustDesk la chiederà a ogni connessione" : "Impostata", "/Admin/Settings#rustdesk"));
        Checklist.Add(new ChecklistItem(enabledLayouts > 0, "Layout collegabili",
            enabledLayouts > 0 ? $"{enabledLayouts} layout abilitati" : "Nessun layout abilitato: lancia una sincronizzazione e abilitali", "/Admin/Layouts"));
        Checklist.Add(new ChecklistItem(admins > 1, "Amministratori",
            admins > 1 ? $"{admins} amministratori" : "Un solo amministratore: aggiungine un secondo", "/Admin/Admins"));

        var companiesWithAssets = await db.Assets.Where(a => a.IsConnectable).Select(a => a.CompanyId).Distinct().ToListAsync(ct);
        var withGateway = (await db.CompanySettings.Where(c => c.GatewayPeerId != null).Select(c => c.CompanyId).ToListAsync(ct))
            .Concat(await db.Assets.Where(a => !a.IsConnectable && a.RustDeskId != null).Select(a => a.CompanyId).ToListAsync(ct))
            .ToHashSet();
        CompaniesWithoutGateway = companiesWithAssets.Count(c => !withGateway.Contains(c));
        AssetsWithOverrides = await db.AssetSettings.CountAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        ConnectionsLast7Days = await db.AuditEvents.CountAsync(e => e.Action == "ticket.redeem" && e.At > since, ct);
    }

    public IActionResult OnPostSync()
    {
        if (!settings.Hudu.IsConfigured)
        {
            FlashError = "Configura prima Hudu.";
            return RedirectToPage();
        }

        _ = sync.RunAsync(Email, CancellationToken.None);
        Flash = "Sincronizzazione avviata: ricarica la pagina tra qualche istante.";
        return RedirectToPage();
    }
}
