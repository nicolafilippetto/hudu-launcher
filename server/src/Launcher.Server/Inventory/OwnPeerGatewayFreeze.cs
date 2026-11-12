using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Inventory;

/// <summary>
/// Aggiornamento 1.5.3, una volta sola: fino alla 1.5.2 il portale sceglieva da solo come gateway il
/// dispositivo RustDesk con lo stesso nome dell'asset (tunnel verso localhost). Dalla 1.5.3 il gateway
/// non si deduce più dal nome: per non interrompere le connessioni che funzionavano così, l'abbinamento
/// di oggi diventa una scelta scritta sull'asset (gateway e indirizzo localhost), visibile e modificabile.
/// </summary>
public static class OwnPeerGatewayFreeze
{
    public const string DoneKey = "migrations.ownPeerGatewayFrozen";

    /// <returns>Asset aggiornati (0 se già fatto o se non c'era nulla da congelare).</returns>
    public static async Task<int> RunOnceAsync(AppDbContext db, bool rustDeskEnabled, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Settings.AnyAsync(s => s.Key == DoneKey, ct))
        {
            return 0;
        }

        var count = 0;
        // Con RustDesk spento l'abbinamento non si applicava: niente da congelare (localhost romperebbe la diretta).
        var peers = rustDeskEnabled ? await db.Peers.AsNoTracking().ToListAsync(ct) : [];
        if (peers.Count > 0)
        {
            var byName = ConnectionResolver.IndexPeersByName(peers);
            var settings = await db.AssetSettings.ToDictionaryAsync(s => s.AssetId, ct);
            var assets = await db.Assets.AsNoTracking().Where(a => a.IsConnectable).ToListAsync(ct);
            foreach (var asset in assets)
            {
                settings.TryGetValue(asset.Id, out var setting);
                // Come prima: il gateway scritto a mano vinceva sull'abbinamento per nome.
                if (setting?.GatewayPeerId is not null || ConnectionResolver.FindOwnPeer(asset, byName) is not { } peer)
                {
                    continue;
                }

                if (setting is null)
                {
                    setting = new AssetSetting { AssetId = asset.Id };
                    db.AssetSettings.Add(setting);
                }

                setting.GatewayPeerId = peer.Id;
                setting.Host ??= ConnectionResolver.SelfHost;
                setting.UpdatedAt = now;
                setting.UpdatedBy = "aggiornamento 1.5.3";
                count++;
            }
        }

        db.Settings.Add(new Setting { Key = DoneKey, Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedAt = now, UpdatedBy = "aggiornamento 1.5.3" });
        await db.SaveChangesAsync(ct);
        return count;
    }
}
