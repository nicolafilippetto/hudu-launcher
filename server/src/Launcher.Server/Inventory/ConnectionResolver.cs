using Launcher.Server.Data;

namespace Launcher.Server.Inventory;

public sealed record ResolvedConnection(
    string Key,
    string Label,
    ConnectionProtocol Protocol,
    string? Host,
    int Port,
    string? GatewayPeerId,
    string? GatewaySource,
    int? PasswordId,
    string? PasswordName,
    string? Username,
    IReadOnlyList<string> Issues)
{
    /// <summary>Collegabile tramite il jump host RustDesk.</summary>
    public bool CanConnect => Host is not null && GatewayPeerId is not null;

    /// <summary>Collegabile direttamente (tecnico dal cliente o in VPN): basta l'indirizzo.</summary>
    public bool CanConnectDirect => Host is not null;

    public bool UsesCredential => Protocol is ConnectionProtocol.Rdp or ConnectionProtocol.Ssh or ConnectionProtocol.Telnet;
}

public sealed record ResolvedAsset(
    CachedAsset Asset,
    string CompanyName,
    AssetSetting? Setting,
    IReadOnlyList<ResolvedConnection> Connections,
    bool IsFavorite)
{
    public bool Hidden => Setting?.Hidden == true;

    public bool NeedsAttention => Connections.Count == 0 || Connections.Any(c => c.Issues.Count > 0);

    public ResolvedConnection? Primary => Connections.FirstOrDefault(c => c.Key == ConnectionResolver.PrimaryKey);
}

/// <summary>Tutto ciò che serve per risolvere gli asset di uno o più clienti.</summary>
public sealed class ResolutionContext
{
    public required IReadOnlyDictionary<int, CachedCompany> Companies { get; init; }
    public required IReadOnlyDictionary<int, LayoutConfig> Layouts { get; init; }
    public required IReadOnlyDictionary<int, AssetSetting> AssetSettings { get; init; }
    public required ILookup<int, ExtraConnection> Extras { get; init; }
    public required IReadOnlyDictionary<int, CompanySetting> CompanySettings { get; init; }
    public required IReadOnlyDictionary<int, CachedPassword> Passwords { get; init; }
    public required ILookup<int, CachedPassword> PasswordsByAsset { get; init; }
    public required IReadOnlyDictionary<string, CachedPeer> Peers { get; init; }
    public required IReadOnlyDictionary<string, CachedPeer> PeersByName { get; init; }
    /// <summary>Gateway letto da Hudu a livello di cliente (asset con campo ID RustDesk).</summary>
    public required IReadOnlyDictionary<int, string> HuduCompanyGateways { get; init; }
    public required IReadOnlySet<int> Favorites { get; init; }
}

/// <summary>
/// Decide come collegarsi a un asset combinando, in ordine di priorità, le correzioni dei tecnici,
/// i dati di Hudu e la rubrica di RustDesk.
/// </summary>
public static class ConnectionResolver
{
    public const string PrimaryKey = "primary";
    public const string ConsoleKey = "console";
    public const string ExtraPrefix = "extra:";

    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => 3389,
        ConnectionProtocol.Ssh => 22,
        ConnectionProtocol.Telnet => 23,
        ConnectionProtocol.Http => 80,
        ConnectionProtocol.Https => 443,
        _ => 0,
    };

    public static string Label(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "RDP",
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Telnet => "Telnet",
        ConnectionProtocol.Http => "Web",
        ConnectionProtocol.Https => "Web",
        _ => protocol.ToString(),
    };

    private static readonly string[] UnixHints =
    [
        "linux", "debian", "ubuntu", "centos", "red hat", "rhel", "rocky", "alma", "suse", "freebsd", "esxi",
        "proxmox", "vmware", "unix", "oracle linux", "photon",
    ];

    /// <summary>Protocollo dedotto dal sistema operativo, poi dal layout, poi dalla piattaforma RustDesk.</summary>
    public static ConnectionProtocol GuessProtocol(string? operatingSystem, LayoutConfig? layout, CachedPeer? ownPeer)
    {
        var os = (operatingSystem ?? "").ToLowerInvariant();
        if (os.Contains("windows", StringComparison.Ordinal))
        {
            return ConnectionProtocol.Rdp;
        }

        if (UnixHints.Any(h => os.Contains(h, StringComparison.Ordinal)))
        {
            return ConnectionProtocol.Ssh;
        }

        if (layout?.DefaultProtocol is { } layoutDefault)
        {
            return layoutDefault;
        }

        var platform = (ownPeer?.Platform ?? "").ToLowerInvariant();
        return platform.Contains("linux", StringComparison.Ordinal) || platform.Contains("mac", StringComparison.Ordinal)
            ? ConnectionProtocol.Ssh
            : ConnectionProtocol.Rdp;
    }

    public static ResolvedAsset Resolve(CachedAsset asset, ResolutionContext ctx)
    {
        ctx.AssetSettings.TryGetValue(asset.Id, out var setting);
        ctx.CompanySettings.TryGetValue(asset.CompanyId, out var companySetting);
        ctx.Layouts.TryGetValue(asset.LayoutId, out var layout);
        var companyName = ctx.Companies.TryGetValue(asset.CompanyId, out var company) ? company.Name : "";
        var ownPeer = FindOwnPeer(asset, ctx);
        var companyGateway = companySetting?.GatewayPeerId
            ?? (ctx.HuduCompanyGateways.TryGetValue(asset.CompanyId, out var huduGateway) ? huduGateway : null);
        var companyGatewaySource = companySetting?.GatewayPeerId is not null ? "predefinito del cliente" : "Hudu, cliente";

        var connections = new List<ResolvedConnection>();

        // --- Connessione principale ---
        var protocol = setting?.Protocol ?? GuessProtocol(asset.OperatingSystem, layout, ownPeer);
        string? gateway;
        string? gatewaySource;
        var (host, hostIssue) = ResolveHost(setting?.Host, asset);
        if (setting?.GatewayPeerId is { } manualGateway)
        {
            (gateway, gatewaySource) = (manualGateway, "manuale");
        }
        else if (ownPeer is not null)
        {
            // RustDesk è installato sull'asset stesso: il tunnel punta a sé stesso.
            (gateway, gatewaySource) = (ownPeer.Id, "RustDesk sull'asset");
            if (setting?.Host is null)
            {
                (host, hostIssue) = ("localhost", null);
            }
        }
        else if (companyGateway is not null)
        {
            (gateway, gatewaySource) = (companyGateway, companyGatewaySource);
        }
        else if (asset.RustDeskId is not null)
        {
            (gateway, gatewaySource) = (asset.RustDeskId, "Hudu, asset");
        }
        else
        {
            (gateway, gatewaySource) = (null, null);
        }

        var port = setting?.Port ?? DefaultPort(protocol);
        var (passwordId, passwordIssue) = ResolvePassword(setting?.PasswordId, asset, ctx);
        connections.Add(Build(PrimaryKey, Label(protocol), protocol, host, port, gateway, gatewaySource, passwordId, passwordIssue, ctx, hostIssue));

        // --- Console fuori banda (iDRAC/iLO/IPMI) dal campo di Hudu ---
        if (HostParser.Parse(asset.ConsoleAddress) is { } console)
        {
            var consoleProtocol = console.Scheme == "http" ? ConnectionProtocol.Http : ConnectionProtocol.Https;
            var consoleGateway = companyGateway ?? gateway;
            connections.Add(Build(ConsoleKey, "Console", consoleProtocol, console.Host, console.Port ?? DefaultPort(consoleProtocol),
                consoleGateway, companyGateway is not null ? companyGatewaySource : gatewaySource, null, null, ctx));
        }

        // --- Connessioni aggiuntive definite dai tecnici ---
        foreach (var extra in ctx.Extras[asset.Id].OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase))
        {
            var extraHost = extra.Host ?? host;
            var (extraGateway, extraSource) = extra.GatewayPeerId is not null
                ? (extra.GatewayPeerId, "manuale")
                : extra.Host is null ? (gateway, gatewaySource) : (companyGateway ?? gateway, companyGateway is not null ? companyGatewaySource : gatewaySource);
            var (extraPasswordId, extraPasswordIssue) = extra.PasswordId is not null
                ? ResolvePassword(extra.PasswordId, asset, ctx)
                : (null, (string?)null);
            connections.Add(Build(ExtraPrefix + extra.Id, extra.Label, extra.Protocol, extraHost, extra.Port ?? DefaultPort(extra.Protocol),
                extraGateway, extraSource, extraPasswordId, extraPasswordIssue, ctx));
        }

        return new ResolvedAsset(asset, companyName, setting, connections, ctx.Favorites.Contains(asset.Id));
    }

    private static ResolvedConnection Build(
        string key, string label, ConnectionProtocol protocol, string? host, int port,
        string? gateway, string? gatewaySource, int? passwordId, string? passwordIssue, ResolutionContext ctx, string? hostIssue = null)
    {
        var issues = new List<string>();
        if (host is null)
        {
            issues.Add(key == PrimaryKey ? "Indirizzo IP mancante: va caricato nell'IPAM di Hudu" : "Indirizzo IP mancante");
        }
        else if (hostIssue is not null)
        {
            issues.Add(hostIssue);
        }

        if (gateway is null)
        {
            issues.Add("Nessun gateway RustDesk");
        }
        else if (ctx.Peers.Count > 0 && !ctx.Peers.ContainsKey(gateway))
        {
            issues.Add("Gateway non presente nella rubrica RustDesk");
        }

        CachedPassword? password = null;
        if (passwordId is { } id)
        {
            ctx.Passwords.TryGetValue(id, out password);
        }

        if (passwordIssue is not null)
        {
            issues.Add(passwordIssue);
        }
        else if (password is null && protocol is ConnectionProtocol.Rdp or ConnectionProtocol.Ssh or ConnectionProtocol.Telnet && key == PrimaryKey)
        {
            issues.Add("Nessuna credenziale associata");
        }

        return new ResolvedConnection(key, label, protocol, host, port, gateway, gatewaySource, password?.Id, password?.Name, password?.Username, issues);
    }

    /// <summary>
    /// Indirizzo della connessione principale. La fonte è l'IPAM di Hudu: con un solo IP si usa quello,
    /// con più IP vale quello scelto dal tecnico (o quello del campo del layout, se è tra questi).
    /// Senza IP nell'IPAM si usa il campo del layout, ma va segnalato: l'IP va caricato in Hudu.
    /// </summary>
    public static (string? Host, string? Issue) ResolveHost(string? chosen, CachedAsset asset)
    {
        var ipam = asset.IpamList();
        if (chosen is not null)
        {
            return ipam.Count == 0
                ? (chosen, "IP non presente nell'IPAM di Hudu: va caricato")
                : ipam.Contains(chosen, StringComparer.OrdinalIgnoreCase) ? (chosen, null) : (chosen, "IP scelto non più presente nell'IPAM di Hudu");
        }

        return ipam.Count switch
        {
            0 => (asset.PrimaryIp, asset.PrimaryIp is null ? null : "IP non presente nell'IPAM di Hudu: va caricato"),
            1 => (ipam[0], null),
            _ when asset.PrimaryIp is { } layoutIp && ipam.Contains(layoutIp, StringComparer.OrdinalIgnoreCase) => (layoutIp, null),
            _ => (ipam[0], $"{ipam.Count} IP nell'IPAM: sceglierne uno"),
        };
    }

    private static (int? Id, string? Issue) ResolvePassword(int? chosen, CachedAsset asset, ResolutionContext ctx)
    {
        if (chosen is { } id)
        {
            return ctx.Passwords.TryGetValue(id, out var password) && password.CompanyId == asset.CompanyId
                ? (id, null)
                : (null, "Credenziale scelta non più presente in Hudu");
        }

        var linked = ctx.PasswordsByAsset[asset.Id].ToList();
        return linked.Count switch
        {
            0 => (null, null),
            1 => (linked[0].Id, null),
            _ => (null, $"{linked.Count} credenziali collegate: sceglierne una"),
        };
    }

    private static CachedPeer? FindOwnPeer(CachedAsset asset, ResolutionContext ctx)
    {
        foreach (var name in new[] { asset.Hostname, asset.Name })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var shortName = name.Split('.')[0];
            if (ctx.PeersByName.TryGetValue(shortName, out var peer))
            {
                return peer;
            }
        }

        return null;
    }

    /// <summary>Indice dei dispositivi RustDesk per hostname e alias (solo nomi univoci).</summary>
    public static Dictionary<string, CachedPeer> IndexPeersByName(IEnumerable<CachedPeer> peers)
    {
        var index = new Dictionary<string, CachedPeer>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in peers)
        {
            foreach (var name in new[] { peer.Hostname, peer.Alias }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Split('.')[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length < 3 || ambiguous.Contains(name))
                {
                    continue;
                }

                if (index.TryGetValue(name, out var existing) && existing.Id != peer.Id)
                {
                    index.Remove(name);
                    ambiguous.Add(name);
                    continue;
                }

                index[name] = peer;
            }
        }

        return index;
    }
}
