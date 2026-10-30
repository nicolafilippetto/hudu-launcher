using Launcher.Server.Data;
using Launcher.Server.Settings;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Audit;

public sealed record AuditEntry(
    string Action,
    string? UserEmail,
    bool Success = true,
    string? CompanyName = null,
    int? AssetId = null,
    string? AssetName = null,
    string? Target = null,
    string? Details = null);

/// <summary>
/// Registro degli accessi: chi ha fatto cosa, quando e da dove, nel database (Amministrazione →
/// Registro accessi). Nei log dell'applicazione va solo una riga senza dati personali né testo
/// inserito dagli utenti (numero dell'evento, azione, esito), utile a un SIEM per gli allarmi:
/// i dettagli restano nel portale, accessibile solo agli amministratori.
/// </summary>
public sealed class AuditService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AuditService> logger)
{
    public async Task WriteAsync(AuditEntry entry, HttpContext? httpContext = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var audit = new AuditEvent
        {
            At = DateTimeOffset.UtcNow,
            Action = entry.Action,
            UserEmail = entry.UserEmail,
            Success = entry.Success,
            CompanyName = entry.CompanyName,
            AssetId = entry.AssetId,
            AssetName = entry.AssetName,
            Target = entry.Target,
            Ip = httpContext?.Connection.RemoteIpAddress?.ToString(),
            Details = entry.Details,
        };
        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);

        // Le azioni sono costanti del codice ("login", "ticket.create"…): nessun dato dell'utente.
        var action = Descriptions.ContainsKey(entry.Action) ? entry.Action : "altro";
        logger.LogInformation("AUDIT #{Id} {Action} {Outcome}", audit.Id, action, entry.Success ? "ok" : "negato");
    }

    /// <summary>Azioni del registro e descrizione per il portale.</summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["login"] = "Accesso",
        ["login.local"] = "Accesso locale",
        ["logout"] = "Uscita",
        ["ticket.create"] = "Connessione richiesta",
        ["ticket.redeem"] = "Credenziali consegnate al connettore",
        ["ticket.reject"] = "Biglietto rifiutato",
        ["offline.prepare"] = "Cache offline preparata",
        ["offline.redeem"] = "Cache offline consegnata",
        ["web.login"] = "Accesso web compilato dal Launcher",
        ["web.login.reject"] = "Accesso web rifiutato",
        ["password.copy"] = "Password copiata",
        ["asset.update"] = "Mappatura asset modificata",
        ["company.update"] = "Gateway cliente modificato",
        ["settings.update"] = "Impostazioni modificate",
        ["layouts.update"] = "Layout modificati",
        ["user.request"] = "Richiesta di accesso",
        ["user.approve"] = "Utente approvato",
        ["user.block"] = "Utente bloccato",
        ["user.remove"] = "Utente rimosso",
        ["user.role"] = "Ruolo utente modificato",
        ["admin.add"] = "Accesso di emergenza aggiunto",
        ["admin.remove"] = "Accesso di emergenza rimosso",
        ["admin.local"] = "Accesso locale configurato",
        ["branding.update"] = "Logo/favicon modificati",
        ["sync"] = "Sincronizzazione",
        ["sync.company"] = "Aggiornamento di un cliente",
        ["ratelimit"] = "Limite di richieste superato",
        ["setup"] = "Configurazione iniziale",
    };

    public static string Describe(string action) => Descriptions.GetValueOrDefault(action, action);
}

/// <summary>Pulizia periodica: biglietti scaduti e registro oltre la conservazione impostata.</summary>
public sealed class HousekeepingService(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings, ILogger<HousekeepingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                // Le date sono salvate come ticks UTC: il confronto in SQL resta corretto.
                var ticketCutoff = DateTimeOffset.UtcNow.AddHours(-1);
                var auditCutoff = DateTimeOffset.UtcNow.AddDays(-settings.Portal.AuditRetentionDays);
                var tickets = await db.Tickets
                    .Where(t => t.ExpiresAt < ticketCutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                var events = await db.AuditEvents
                    .Where(a => a.At < auditCutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                if (tickets + events > 0)
                {
                    logger.LogInformation("Pulizia: {Tickets} biglietti e {Events} eventi rimossi", tickets, events);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Pulizia periodica non riuscita");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
