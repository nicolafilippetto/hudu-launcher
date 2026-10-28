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
        var breakGlass = await db.Admins.Select(a => a.Email.ToUpper()).ToListAsync(ct);
        var usableBreakGlass = await db.Users.CountAsync(u => u.NormalizedEmail != null && breakGlass.Contains(u.NormalizedEmail), ct);
        var entraAdmins = await db.PortalUsers.CountAsync(u => u.Status == PortalUserStatus.Approved && u.IsAdmin, ct);
        var pending = await db.PortalUsers.CountAsync(u => u.Status == PortalUserStatus.Pending, ct);

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
            settings.Entra.IsConfigured ? "Configurato" : "Tenant, client ID e credenziale (certificato o segreto) da impostare", "/Admin/Settings#entra"));
        if (settings.Entra.IsConfigured)
        {
            var certificate = EntraCredential.Describe(settings.Entra.Certificate);
            Checklist.Add(certificate is null
                ? new ChecklistItem(false, "Credenziale Microsoft 365", "Segreto client: consigliato passare al certificato", "/Admin/Settings#entra")
                : new ChecklistItem(certificate.NotAfter - DateTimeOffset.UtcNow > TimeSpan.FromDays(30), "Credenziale Microsoft 365",
                    $"Certificato, scade il {certificate.NotAfter:dd/MM/yyyy}", "/Admin/Settings#entra"));
        }

        Checklist.Add(new ChecklistItem(settings.Hudu.IsConfigured, "Hudu",
            settings.Hudu.IsConfigured ? settings.Hudu.BaseUrl! : "Indirizzo e chiave API da impostare", "/Admin/Settings#hudu"));
        Checklist.Add(new ChecklistItem(settings.RustDesk.IsApiConfigured, "Rubrica RustDesk",
            settings.RustDesk.IsApiConfigured ? settings.RustDesk.ApiUrl! : "Facoltativa ma consigliata: abbina i dispositivi agli asset", "/Admin/Settings#rustdesk"));
        Checklist.Add(new ChecklistItem(!string.IsNullOrEmpty(settings.RustDesk.ConnectionPassword), "Password di connessione RustDesk",
            string.IsNullOrEmpty(settings.RustDesk.ConnectionPassword) ? "Senza, RustDesk la chiederà a ogni connessione" : "Impostata", "/Admin/Settings#rustdesk"));
        Checklist.Add(new ChecklistItem(enabledLayouts > 0, "Layout collegabili",
            enabledLayouts > 0 ? $"{enabledLayouts} layout abilitati" : "Nessun layout abilitato: lancia una sincronizzazione e abilitali", "/Admin/Layouts"));
        Checklist.Add(new ChecklistItem(usableBreakGlass > 1, "Accessi di emergenza",
            usableBreakGlass > 1 ? $"{usableBreakGlass} accessi di emergenza" : "Uno solo: aggiungine un secondo, per non restare chiusi fuori", "/Admin/Admins"));
        Checklist.Add(new ChecklistItem(entraAdmins > 0, "Amministratori con Microsoft 365",
            entraAdmins > 0 ? (entraAdmins == 1 ? "1 amministratore" : $"{entraAdmins} amministratori") : "Nessuno: entra con Microsoft 365 e approva il tuo utente come amministratore", "/Admin/Users"));
        if (pending > 0)
        {
            Checklist.Add(new ChecklistItem(false, "Richieste di accesso", pending == 1 ? "1 utente in attesa di approvazione" : $"{pending} utenti in attesa di approvazione", "/Admin/Users"));
        }

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
