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
/// Registro degli accessi: chi ha fatto cosa, quando e da dove. Ogni evento va anche nei log
/// dell'applicazione, così può essere inoltrato a un SIEM.
/// </summary>
public sealed class AuditService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AuditService> logger)
{
    public async Task WriteAsync(AuditEntry entry, HttpContext? httpContext = null, CancellationToken cancellationToken = default)
    {
        var ip = httpContext?.Connection.RemoteIpAddress?.ToString();
        logger.LogInformation(
            "AUDIT {Action} utente={User} esito={Success} cliente={Company} asset={AssetId}:{AssetName} destinazione={Target} ip={Ip} {Details}",
            entry.Action, entry.UserEmail, entry.Success ? "ok" : "negato", entry.CompanyName, entry.AssetId, entry.AssetName, entry.Target, ip, entry.Details);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEvent
        {
            At = DateTimeOffset.UtcNow,
            Action = entry.Action,
            UserEmail = entry.UserEmail,
            Success = entry.Success,
            CompanyName = entry.CompanyName,
            AssetId = entry.AssetId,
            AssetName = entry.AssetName,
            Target = entry.Target,
            Ip = ip,
            Details = entry.Details,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string Describe(string action) => action switch
    {
        "login" => "Accesso",
        "login.local" => "Accesso locale",
        "logout" => "Uscita",
        "ticket.create" => "Connessione richiesta",
        "ticket.redeem" => "Credenziali consegnate al connettore",
        "ticket.reject" => "Biglietto rifiutato",
        "password.copy" => "Password copiata",
        "asset.update" => "Mappatura asset modificata",
        "company.update" => "Gateway cliente modificato",
        "settings.update" => "Impostazioni modificate",
        "layouts.update" => "Layout modificati",
        "admin.add" => "Admin aggiunto",
        "admin.remove" => "Admin rimosso",
        "admin.local" => "Accesso locale configurato",
        "branding.update" => "Logo/favicon modificati",
        "sync" => "Sincronizzazione",
        "ratelimit" => "Limite di richieste superato",
        "setup" => "Configurazione iniziale",
        _ => action,
    };
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
