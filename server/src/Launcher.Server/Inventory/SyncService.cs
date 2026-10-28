using System.Globalization;
using System.Text;
using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Integrations;
using Launcher.Server.Settings;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Inventory;

public sealed record SyncResult(bool Success, string Message);

/// <summary>
/// Copia periodicamente in cache l'inventario di Hudu (clienti, asset, IP dell'IPAM, metadati delle
/// password) e la rubrica di RustDesk. Le password non vengono mai salvate: si leggono al momento della
/// connessione. Se Hudu non risponde resta valida l'ultima cache.
/// </summary>
public sealed class SyncService(
    IServiceScopeFactory scopes,
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    ILogger<SyncService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset> _companyRefreshes = new();

    /// <summary>Intervallo minimo tra due aggiornamenti dello stesso cliente.</summary>
    public static readonly TimeSpan CompanyRefreshInterval = TimeSpan.FromMinutes(1);

    public bool IsRunning => _running.CurrentCount == 0;

    /// <summary>Chiede una sincronizzazione appena possibile (es. dopo aver salvato le impostazioni).</summary>
    public void RequestSync()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // già richiesta
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Hudu.IsConfigured)
            {
                await RunAsync(null, stoppingToken);
            }

            var interval = TimeSpan.FromMinutes(settings.Hudu.SyncMinutes);
            try
            {
                await _wake.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<SyncResult> RunAsync(string? requestedBy, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct))
        {
            return new SyncResult(false, "Sincronizzazione già in corso.");
        }

        try
        {
            return await RunCoreAsync(requestedBy, ct);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<SyncResult> RunCoreAsync(string? requestedBy, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var hudu = scope.ServiceProvider.GetRequiredService<HuduClient>();
        var rustDesk = scope.ServiceProvider.GetRequiredService<RustDeskClient>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var started = DateTimeOffset.UtcNow;
        await UpdateStateAsync(s => s.LastStartedAt = started, ct);

        try
        {
            var layouts = await hudu.GetLayoutsAsync(ct);
            var layoutConfigs = await EnsureLayoutConfigsAsync(layouts, ct);
            var companies = await hudu.GetCompaniesAsync(ct);
            var companyNames = companies.ToDictionary(c => c.Id, c => c.Name);
            var (ipam, ipamError) = await ReadIpamAsync(hudu, null, ct);

            var assets = new List<CachedAsset>();
            foreach (var config in layoutConfigs.Values.Where(c => c.Enabled || !string.IsNullOrWhiteSpace(c.RustDeskIdFieldLabel)))
            {
                foreach (var asset in await hudu.GetAssetsAsync(config.LayoutId, ct))
                {
                    if (!asset.Archived)
                    {
                        assets.Add(ToCached(asset, config, companyNames.GetValueOrDefault(asset.CompanyId), ipam.GetValueOrDefault(asset.Id)));
                    }
                }
            }

            var passwords = (await hudu.GetPasswordInfosAsync(ct)).Select(ToCached).ToList();

            List<CachedPeer>? peers = null;
            string? peerError = null;
            if (settings.RustDesk.IsApiConfigured)
            {
                try
                {
                    peers = (await rustDesk.GetPeersAsync(ct))
                        .Select(p => new CachedPeer
                        {
                            Id = p.Id,
                            Alias = p.Alias,
                            Hostname = p.Hostname,
                            Platform = p.Platform,
                            Tags = p.Tags.Count == 0 ? null : string.Join(", ", p.Tags),
                            AddressBook = p.AddressBook,
                        })
                        .ToList();
                }
                catch (RustDeskException ex)
                {
                    peerError = ex.Message;
                    logger.LogWarning(ex, "Rubrica RustDesk non letta, resta quella precedente");
                }
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Layouts.ExecuteDeleteAsync(ct);
            await db.Companies.ExecuteDeleteAsync(ct);
            await db.Assets.ExecuteDeleteAsync(ct);
            await db.Passwords.ExecuteDeleteAsync(ct);
            db.Layouts.AddRange(layouts.Select(l => new CachedLayout { Id = l.Id, Name = l.Name, Icon = l.Icon, FieldLabels = string.Join('\n', l.FieldLabels) }));
            db.Companies.AddRange(companies.Select(c => new CachedCompany { Id = c.Id, Name = c.Name, Nickname = c.Nickname, HuduUrl = c.Url }));
            db.Assets.AddRange(assets);
            db.Passwords.AddRange(passwords);
            if (peers is not null)
            {
                await db.Peers.ExecuteDeleteAsync(ct);
                db.Peers.AddRange(peers);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var peerCount = peers?.Count ?? await db.Peers.CountAsync(ct);
            var message = $"{companies.Count} clienti, {assets.Count(a => a.IsConnectable)} asset, {passwords.Count} credenziali, "
                          + $"{ipam.Values.Sum(v => v.Count)} IP dall'IPAM, {peerCount} dispositivi RustDesk";
            var warning = string.Join(" · ", new[] { peerError is null ? null : "RustDesk: " + peerError, ipamError }.OfType<string>());
            await UpdateStateAsync(s =>
            {
                s.LastSuccessAt = DateTimeOffset.UtcNow;
                s.LastError = warning.Length == 0 ? null : warning;
                s.Companies = companies.Count;
                s.Assets = assets.Count(a => a.IsConnectable);
                s.Passwords = passwords.Count;
                s.Peers = peerCount;
            }, ct);
            logger.LogInformation("Sincronizzazione completata in {Seconds:0.0}s: {Message}", (DateTimeOffset.UtcNow - started).TotalSeconds, message);
            if (requestedBy is not null)
            {
                await audit.WriteAsync(new AuditEntry("sync", requestedBy, Details: message), cancellationToken: ct);
            }

            return new SyncResult(warning.Length == 0, warning.Length == 0 ? message : message + " — " + warning);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sincronizzazione non riuscita");
            await UpdateStateAsync(s => s.LastError = ex.Message, CancellationToken.None);
            await audit.WriteAsync(new AuditEntry("sync", requestedBy, Success: false, Details: ex.Message), cancellationToken: CancellationToken.None);
            return new SyncResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Aggiorna da Hudu solo un cliente (asset, IP dell'IPAM, credenziali): serve subito dopo una
    /// modifica in Hudu, senza attendere la sincronizzazione completa.
    /// </summary>
    public async Task<SyncResult> RefreshCompanyAsync(int companyId, string requestedBy, CancellationToken ct)
    {
        // Una volta al minuto per cliente, chiunque lo chieda: protegge i limiti dell'API di Hudu.
        var now = DateTimeOffset.UtcNow;
        var last = _companyRefreshes.GetOrAdd(companyId, DateTimeOffset.MinValue);
        var wait = last + CompanyRefreshInterval - now;
        if (wait > TimeSpan.Zero || !_companyRefreshes.TryUpdate(companyId, now, last))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return new SyncResult(false, $"Cliente aggiornato da poco: riprova tra {seconds} secondi.");
        }

        if (!await _running.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            return new SyncResult(false, "È in corso una sincronizzazione completa: riprova tra poco.");
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var hudu = scope.ServiceProvider.GetRequiredService<HuduClient>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
            if (company is null)
            {
                return new SyncResult(false, "Cliente non trovato.");
            }

            try
            {
                var configs = await db.LayoutConfigs.AsNoTracking().ToDictionaryAsync(c => c.LayoutId, ct);
                var (ipam, ipamError) = await ReadIpamAsync(hudu, companyId, ct);
                var assets = (await hudu.GetCompanyAssetsAsync(companyId, ct))
                    .Where(a => !a.Archived && a.CompanyId == companyId && configs.TryGetValue(a.LayoutId, out var c)
                                && (c.Enabled || !string.IsNullOrWhiteSpace(c.RustDeskIdFieldLabel)))
                    .Select(a => ToCached(a, configs[a.LayoutId], company.Name, ipam.GetValueOrDefault(a.Id)))
                    .ToList();
                var passwords = (await hudu.GetPasswordInfosAsync(ct, companyId))
                    .Where(p => p.CompanyId == companyId)
                    .Select(ToCached)
                    .ToList();

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Assets.Where(a => a.CompanyId == companyId).ExecuteDeleteAsync(ct);
                await db.Passwords.Where(p => p.CompanyId == companyId).ExecuteDeleteAsync(ct);
                db.Assets.AddRange(assets);
                db.Passwords.AddRange(passwords);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var message = $"{company.Name}: {assets.Count(a => a.IsConnectable)} asset, {passwords.Count} credenziali, {ipam.Values.Sum(v => v.Count)} IP dall'IPAM"
                              + (ipamError is null ? "" : " — " + ipamError);
                await audit.WriteAsync(new AuditEntry("sync.company", requestedBy, CompanyName: company.Name, Details: message), cancellationToken: ct);
                return new SyncResult(true, message);
            }
            catch (Exception ex) when (ex is HuduException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Aggiornamento del cliente {CompanyId} non riuscito", companyId);
                await audit.WriteAsync(new AuditEntry("sync.company", requestedBy, Success: false, CompanyName: company.Name, Details: ex.Message),
                    cancellationToken: CancellationToken.None);
                return new SyncResult(false, ex.Message);
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// IPAM di Hudu: gli indirizzi di ogni asset. Se l'endpoint non risponde si tengono quelli già in
    /// cache, così gli asset non risultano di colpo senza IP.
    /// </summary>
    private async Task<(Dictionary<int, List<HuduIpAddress>> ByAsset, string? Error)> ReadIpamAsync(HuduClient hudu, int? companyId, CancellationToken ct)
    {
        try
        {
            return (GroupAddresses(await hudu.GetIpAddressesAsync(ct, companyId)), null);
        }
        catch (HuduException ex)
        {
            logger.LogWarning(ex, "IPAM di Hudu non letto, restano gli IP in cache");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cached = await db.Assets.AsNoTracking()
                .Where(a => a.IpamAddresses != null && (companyId == null || a.CompanyId == companyId))
                .ToListAsync(ct);
            var previous = cached.ToDictionary(a => a.Id, a => a.IpamList().Select(ip => new HuduIpAddress(ip, null, null, a.Id, a.CompanyId)).ToList());
            return (previous, "IPAM non letto: " + ex.Message);
        }
    }

    /// <summary>Indirizzi di ogni asset, dal preferito: IPv4 prima di IPv6, poi quelli "assigned"/"active".</summary>
    internal static Dictionary<int, List<HuduIpAddress>> GroupAddresses(IEnumerable<HuduIpAddress> addresses) =>
        addresses
            .Where(a => a.AssetId is > 0 && HostParser.ExtractHost(a.Address) is not null)
            .GroupBy(a => a.AssetId!.Value)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(a => System.Net.IPAddress.TryParse(HostParser.ExtractHost(a.Address), out var ip)
                              && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(a => a.Status is not null && (a.Status.Equals("assigned", StringComparison.OrdinalIgnoreCase)
                                                      || a.Status.Equals("active", StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .DistinctBy(a => HostParser.ExtractHost(a.Address), StringComparer.OrdinalIgnoreCase)
                .ToList());

    private static CachedPassword ToCached(HuduPasswordInfo p) => new()
    {
        Id = p.Id,
        CompanyId = p.CompanyId,
        Name = p.Name,
        Username = p.Username,
        Url = p.Url,
        AssetId = string.Equals(p.PasswordableType, "Asset", StringComparison.OrdinalIgnoreCase) ? p.PasswordableId : null,
    };

    private async Task UpdateStateAsync(Action<SyncState> update, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.SyncStates.FindAsync([1], ct);
        if (state is null)
        {
            state = new SyncState();
            db.SyncStates.Add(state);
        }

        update(state);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Crea la configurazione dei layout nuovi, indovinando i campi dai nomi.</summary>
    private async Task<Dictionary<int, LayoutConfig>> EnsureLayoutConfigsAsync(IReadOnlyList<HuduLayout> layouts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.LayoutConfigs.ToDictionaryAsync(c => c.LayoutId, ct);
        foreach (var layout in layouts)
        {
            if (existing.TryGetValue(layout.Id, out var config))
            {
                config.LayoutName = layout.Name;
                continue;
            }

            config = LayoutDefaults.Guess(layout);
            db.LayoutConfigs.Add(config);
            existing[layout.Id] = config;
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    internal static CachedAsset ToCached(HuduAsset asset, LayoutConfig config, string? companyName, IReadOnlyList<HuduIpAddress>? ipam = null)
    {
        string? Field(string? label) =>
            label is not null && asset.Fields.TryGetValue(label, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        // L'IPAM di Hudu è la fonte degli IP; il campo del layout resta come riserva e per scegliere
        // tra più IP dell'IPAM.
        var ip = HostParser.ExtractHost(Field(config.IpFieldLabel));
        var ipamIps = (ipam ?? []).Select(a => HostParser.ExtractHost(a.Address)!).ToList();
        var fqdn = (ipam ?? []).Select(a => a.Fqdn).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim();
        var hostname = Field(config.HostnameFieldLabel) ?? fqdn;
        var os = Field(config.OsFieldLabel);
        var console = Field(config.ConsoleFieldLabel);
        var rustDeskId = Field(config.RustDeskIdFieldLabel) is { } rd ? new string(rd.Where(char.IsLetterOrDigit).ToArray()) : null;

        return new CachedAsset
        {
            Id = asset.Id,
            CompanyId = asset.CompanyId,
            LayoutId = asset.LayoutId,
            LayoutName = config.LayoutName,
            Name = asset.Name,
            HuduUrl = asset.Url,
            Hostname = hostname,
            PrimaryIp = ip,
            IpamAddresses = ipamIps.Count == 0 ? null : string.Join('\n', ipamIps),
            OperatingSystem = os,
            ConsoleAddress = console,
            RustDeskId = string.IsNullOrEmpty(rustDeskId) ? null : rustDeskId,
            IsConnectable = config.Enabled,
            SearchText = Normalize(string.Join(' ', new[] { asset.Name, hostname, ip, os, companyName }.Concat(ipamIps).Where(s => s is not null).Distinct())),
        };
    }

    /// <summary>Minuscolo e senza accenti, per una ricerca tollerante.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>Valori iniziali dei layout, dedotti dai nomi dei layout e dei campi di Hudu.</summary>
public static class LayoutDefaults
{
    private static readonly string[] ConnectableLayouts =
    [
        "virtual machine", "physical server", "server", "firewall", "switch", "nas", "san", "ups", "wireless", "access point",
        "router", "printer", "storage", "hypervisor",
    ];

    public static LayoutConfig Guess(HuduLayout layout)
    {
        var name = layout.Name.ToLowerInvariant();
        string? Find(params string[] fragments) =>
            fragments.Select(f => layout.FieldLabels.FirstOrDefault(l => l.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(l => l is not null);

        var ipField = Find("management ip", "ip address", "indirizzo ip", "ip");
        return new LayoutConfig
        {
            LayoutId = layout.Id,
            LayoutName = layout.Name,
            // L'IP può arrivare anche dall'IPAM: basta che il layout sia di un tipo collegabile.
            Enabled = ConnectableLayouts.Any(name.Contains),
            IpFieldLabel = ipField,
            OsFieldLabel = Find("operating system", "sistema operativo"),
            HostnameFieldLabel = Find("hostname", "fqdn"),
            ConsoleFieldLabel = Find("idrac", "ilo", "ipmi", "management url", "web ui", "console"),
            RustDeskIdFieldLabel = Find("rustdesk"),
            DefaultProtocol = name.Contains("firewall") || name.Contains("nas") || name.Contains("san") || name.Contains("ups")
                || name.Contains("wireless") || name.Contains("access point") || name.Contains("printer") || name.Contains("switch")
                ? ConnectionProtocol.Https
                : null,
        };
    }
}

public static class HostParser
{
    /// <summary>
    /// Estrae host (e porta) da valori come "192.168.1.1", "https://10.0.0.1:8443/",
    /// "10.0.0.5/24" o "srv01.dominio.local". Restituisce null se non riconosce un host.
    /// </summary>
    public static (string Host, int? Port, string? Scheme)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Split([' ', ',', ';', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return (uri.Host.Trim('[', ']'), uri.IsDefaultPort ? null : uri.Port, uri.Scheme.ToLowerInvariant());
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            text = text[..slash];
        }

        int? port = null;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.Count(c => c == ':') == 1 && int.TryParse(text[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            port = p;
            text = text[..colon];
        }

        return IsValidHost(text) ? (text, port, null) : null;
    }

    public static string? ExtractHost(string? value) => Parse(value)?.Host;

    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && host.Length <= 253
        && (System.Net.IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
}
