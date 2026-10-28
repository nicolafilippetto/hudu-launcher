using System.Security.Cryptography;
using System.Text;
using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Connections;

public enum RedeemError
{
    None,
    NotFound,
    Expired,
    AlreadyUsed,
    WrongUser,
    WrongClient,
}

public sealed record RedeemedTicket(Ticket Ticket, RedeemError Error);

/// <summary>
/// Biglietti monouso: 256 bit casuali, validi pochi secondi, legati all'utente e alla
/// connessione. Nel database finisce solo l'hash SHA-256, mai il biglietto.
/// I biglietti degli admin locali (senza 365) valgono solo dallo stesso indirizzo IP del browser.
/// </summary>
public sealed class TicketService(AppDbContext db, SettingsService settings, TimeProvider time)
{
    public async Task<string> CreateAsync(string userEmail, int assetId, string connectionKey, string? clientIp, string authMethod, bool direct,
        CancellationToken ct, IReadOnlyCollection<int>? offlineAssets = null)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        db.Tickets.Add(new Ticket
        {
            Id = Hash(token),
            UserEmail = userEmail.ToLowerInvariant(),
            AssetId = assetId,
            ConnectionKey = connectionKey,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(settings.Portal.TicketSeconds),
            ClientIp = clientIp,
            AuthMethod = authMethod,
            Direct = direct,
            OfflineAssets = offlineAssets is null ? null : string.Join(',', offlineAssets),
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    /// Riscatta il biglietto una sola volta, anche con richieste contemporanee.
    /// <paramref name="userEmail"/> è l'utente del token 365 del connettore (null se assente).
    /// </summary>
    public async Task<RedeemedTicket?> RedeemAsync(string token, string? userEmail, string? clientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var id = Hash(token.Trim());
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
        {
            return new RedeemedTicket(new Ticket { Id = id, UserEmail = "", ConnectionKey = "" }, RedeemError.NotFound);
        }

        var now = time.GetUtcNow();
        if (ticket.RedeemedAt is not null)
        {
            return new RedeemedTicket(ticket, RedeemError.AlreadyUsed);
        }

        if (ticket.ExpiresAt < now)
        {
            return new RedeemedTicket(ticket, RedeemError.Expired);
        }

        // Il biglietto si brucia anche se l'utente è sbagliato: un biglietto rubato non si riprova.
        var updated = await db.Tickets
            .Where(t => t.Id == id && t.RedeemedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RedeemedAt, now), ct);
        if (updated == 0)
        {
            return new RedeemedTicket(ticket, RedeemError.AlreadyUsed);
        }

        if (ticket.AuthMethod == LauncherClaims.MethodLocal)
        {
            if (!string.Equals(ticket.ClientIp, clientIp, StringComparison.Ordinal))
            {
                return new RedeemedTicket(ticket, RedeemError.WrongClient);
            }
        }
        else if (userEmail is null || !string.Equals(ticket.UserEmail, userEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new RedeemedTicket(ticket, RedeemError.WrongUser);
        }

        return new RedeemedTicket(ticket, RedeemError.None);
    }

    /// <summary>Asset di un pacchetto offline.</summary>
    public static IReadOnlyList<int> OfflineAssetIds(Ticket ticket) =>
        (ticket.OfflineAssets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsValidConnectionKey(string key) =>
        key == ConnectionResolver.PrimaryKey
        || key == ConnectionResolver.ConsoleKey
        || (key.StartsWith(ConnectionResolver.ExtraPrefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(ConnectionResolver.ExtraPrefix.Length), out _));
}
