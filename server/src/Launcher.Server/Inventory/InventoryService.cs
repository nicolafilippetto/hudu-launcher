using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Inventory;

public sealed record CompanySummary(int Id, string Name, string? Nickname, int AssetCount, bool HasGateway);

public sealed record CompanyView(CachedCompany Company, string? GatewayPeerId, string? GatewaySource, IReadOnlyList<IGrouping<string, ResolvedAsset>> Groups);

/// <summary>Letture dell'inventario in cache per il portale e per il connettore.</summary>
public sealed class InventoryService(AppDbContext db, Settings.SettingsService settings)
{
    public const int SearchLimit = 60;

    public async Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(CancellationToken ct)
    {
        var counts = await db.Assets.Where(a => a.IsConnectable)
            .GroupBy(a => a.CompanyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var gateways = (await db.CompanySettings.Where(c => c.GatewayPeerId != null).Select(c => c.CompanyId).ToListAsync(ct))
            .Concat(await db.Assets.Where(a => a.RustDeskId != null && !a.IsConnectable).Select(a => a.CompanyId).ToListAsync(ct))
            .ToHashSet();
        var companies = await db.Companies.AsNoTracking().ToListAsync(ct);
        return companies
            .Where(c => counts.ContainsKey(c.Id))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new CompanySummary(c.Id, c.Name, c.Nickname, counts[c.Id], gateways.Contains(c.Id)))
            .ToList();
    }

    public async Task<CompanyView?> GetCompanyAsync(int companyId, string userEmail, bool includeHidden, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null)
        {
            return null;
        }

        var assets = await db.Assets.AsNoTracking().Where(a => a.CompanyId == companyId && a.IsConnectable).ToListAsync(ct);
        var ctx = await BuildContextAsync([companyId], assets.Select(a => a.Id).ToList(), userEmail, ct);
        var resolved = assets.Select(a => ConnectionResolver.Resolve(a, ctx))
            .Where(a => includeHidden || !a.Hidden)
            .OrderBy(a => a.Asset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var groups = resolved.GroupBy(a => a.Asset.LayoutName).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();

        ctx.CompanySettings.TryGetValue(companyId, out var setting);
        var gateway = setting?.GatewayPeerId ?? ctx.HuduCompanyGateways.GetValueOrDefault(companyId);
        var source = setting?.GatewayPeerId is not null ? "impostato nel portale" : gateway is not null ? "da Hudu" : null;
        return new CompanyView(company, gateway, source, groups);
    }

    /// <summary>Ricerca per parole; con <paramref name="companyId"/> solo dentro quel cliente.</summary>
    public async Task<IReadOnlyList<ResolvedAsset>> SearchAsync(string query, string userEmail, int? companyId, CancellationToken ct)
    {
        var terms = SyncService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        IQueryable<CachedAsset> q = db.Assets.AsNoTracking().Where(a => a.IsConnectable);
        if (companyId is { } id)
        {
            q = q.Where(a => a.CompanyId == id);
        }

        foreach (var term in terms)
        {
            q = q.Where(a => a.SearchText.Contains(term));
        }

        var assets = await q.OrderBy(a => a.Name).Take(SearchLimit).ToListAsync(ct);
        return await ResolveManyAsync(assets, userEmail, ct);
    }

    public async Task<IReadOnlyList<ResolvedAsset>> GetFavoritesAsync(string userEmail, CancellationToken ct)
    {
        var ids = await db.Favorites.Where(f => f.UserEmail == userEmail).Select(f => f.AssetId).ToListAsync(ct);
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderBy(a => a.Name).ToListAsync(ct);
        return await ResolveManyAsync(assets, userEmail, ct);
    }

    public async Task<IReadOnlyList<ResolvedAsset>> GetRecentAsync(string userEmail, int count, CancellationToken ct)
    {
        var ids = await db.AuditEvents
            .Where(e => e.UserEmail == userEmail && e.Action == "ticket.create" && e.Success && e.AssetId != null)
            .OrderByDescending(e => e.Id)
            .Select(e => e.AssetId!.Value)
            .Take(100)
            .ToListAsync(ct);
        var ordered = ids.Distinct().Take(count).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => ordered.Contains(a.Id)).ToListAsync(ct);
        var resolved = await ResolveManyAsync(assets, userEmail, ct);
        return resolved.OrderBy(a => ordered.IndexOf(a.Asset.Id)).ToList();
    }

    public async Task<ResolvedAsset?> GetAssetAsync(int assetId, string userEmail, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return null;
        }

        var resolved = await ResolveManyAsync([asset], userEmail, ct);
        return resolved.Count > 0 ? resolved[0] : null;
    }

    public async Task<IReadOnlyList<ResolvedAsset>> GetAssetsAsync(IReadOnlyCollection<int> assetIds, string userEmail, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).OrderBy(a => a.Name).ToListAsync(ct);
        return await ResolveManyAsync(assets, userEmail, ct);
    }

    public async Task<IReadOnlyList<CachedPassword>> GetCompanyPasswordsAsync(int companyId, CancellationToken ct) =>
        await db.Passwords.AsNoTracking().Where(p => p.CompanyId == companyId).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<CachedPeer>> GetPeersAsync(CancellationToken ct) =>
        await db.Peers.AsNoTracking().OrderBy(p => p.Alias ?? p.Hostname ?? p.Id).ToListAsync(ct);

    private async Task<IReadOnlyList<ResolvedAsset>> ResolveManyAsync(List<CachedAsset> assets, string userEmail, CancellationToken ct)
    {
        if (assets.Count == 0)
        {
            return [];
        }

        var companyIds = assets.Select(a => a.CompanyId).Distinct().ToList();
        var ctx = await BuildContextAsync(companyIds, assets.Select(a => a.Id).ToList(), userEmail, ct);
        return assets.Select(a => ConnectionResolver.Resolve(a, ctx)).ToList();
    }

    private async Task<ResolutionContext> BuildContextAsync(List<int> companyIds, List<int> assetIds, string userEmail, CancellationToken ct)
    {
        var companies = await db.Companies.AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var layouts = await db.LayoutConfigs.AsNoTracking().ToDictionaryAsync(l => l.LayoutId, ct);
        var assetSettings = await db.AssetSettings.AsNoTracking().Where(s => assetIds.Contains(s.AssetId)).ToDictionaryAsync(s => s.AssetId, ct);
        var extras = (await db.ExtraConnections.AsNoTracking().Where(e => assetIds.Contains(e.AssetId)).ToListAsync(ct)).ToLookup(e => e.AssetId);
        var companySettings = await db.CompanySettings.AsNoTracking().Where(c => companyIds.Contains(c.CompanyId)).ToDictionaryAsync(c => c.CompanyId, ct);
        var passwords = await db.Passwords.AsNoTracking().Where(p => companyIds.Contains(p.CompanyId)).ToListAsync(ct);
        var peers = await db.Peers.AsNoTracking().ToListAsync(ct);
        var huduGateways = (await db.Assets.AsNoTracking()
                .Where(a => companyIds.Contains(a.CompanyId) && a.RustDeskId != null && !a.IsConnectable)
                .OrderBy(a => a.Id)
                .Select(a => new { a.CompanyId, a.RustDeskId })
                .ToListAsync(ct))
            .GroupBy(a => a.CompanyId)
            .ToDictionary(g => g.Key, g => g.First().RustDeskId!);
        var favorites = (await db.Favorites.Where(f => f.UserEmail == userEmail && assetIds.Contains(f.AssetId)).Select(f => f.AssetId).ToListAsync(ct)).ToHashSet();

        return new ResolutionContext
        {
            Companies = companies,
            Layouts = layouts,
            AssetSettings = assetSettings,
            Extras = extras,
            CompanySettings = companySettings,
            Passwords = passwords.ToDictionary(p => p.Id),
            PasswordsByAsset = passwords.Where(p => p.AssetId is not null).ToLookup(p => p.AssetId!.Value),
            Peers = peers.ToDictionary(p => p.Id, StringComparer.Ordinal),
            PeersByName = ConnectionResolver.IndexPeersByName(peers),
            HuduCompanyGateways = huduGateways,
            Favorites = favorites,
            RustDeskEnabled = settings.RustDesk.Enabled,
        };
    }
}
