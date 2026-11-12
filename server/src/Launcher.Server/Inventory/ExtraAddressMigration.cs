using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Inventory;

/// <summary>
/// Aggiornamento 1.5.7, una volta sola: fino alla 1.5.6 l'indirizzo completo di una connessione aggiuntiva
/// (o della console) era anche la destinazione del tunnel. Ora il tunnel va all'IP e l'indirizzo serve solo
/// al browser: le connessioni salvate con un indirizzo e senza IP ricevono l'IP che l'IPAM associa a quel
/// nome, o il nome stesso se non c'è (il tunnel va dove andava prima).
/// </summary>
public static class ExtraAddressMigration
{
    public const string DoneKey = "migrations.extraBrowserAddress";

    public static async Task<int> RunOnceAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Settings.AnyAsync(s => s.Key == DoneKey, ct))
        {
            return 0;
        }

        var rows = await db.ExtraConnections.Where(e => e.Url != null && e.Host == null).ToListAsync(ct);
        var assetIds = rows.Select(r => r.AssetId).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var count = 0;
        foreach (var row in rows)
        {
            if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var url))
            {
                continue;
            }

            var name = url.Host.Trim('[', ']');
            row.Host = assets.TryGetValue(row.AssetId, out var asset) ? ConnectionResolver.TunnelHost(asset, name) : name;
            row.UpdatedAt = now;
            row.UpdatedBy = "aggiornamento 1.5.7";
            count++;
        }

        db.Settings.Add(new Setting { Key = DoneKey, Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedAt = now, UpdatedBy = "aggiornamento 1.5.7" });
        await db.SaveChangesAsync(ct);
        return count;
    }
}
