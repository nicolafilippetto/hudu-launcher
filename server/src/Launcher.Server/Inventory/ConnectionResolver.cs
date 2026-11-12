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
    IReadOnlyList<string> Issues,
    string? Url = null,
    SavedOptions? Options = null,
    bool DirectOnly = false)
{
    public bool IsWeb => Protocol is ConnectionProtocol.Http or ConnectionProtocol.Https;

    /// <summary>Opzioni della connessione: RDP e pagine web (JSON salvati), database Oracle.</summary>
    public SavedOptions Settings => Options ?? SavedOptions.None;

    /// <summary>Pagina da aprire: l'URL indicato o, in mancanza, schema, host e porta di destinazione.</summary>
    public string? WebUrl => !IsWeb || Host is null ? null : Url ?? Connections.WebUrls.Default(Protocol == ConnectionProtocol.Https, Host, Port);

    /// <summary>Collegabile tramite il jump host RustDesk.</summary>
    public bool CanConnect => CanConnectDirect && GatewayPeerId is not null;

    // DirectOnly: scelta esplicita (connessione, cliente o RustDesk spento) di collegarsi solo in diretta.

    /// <summary>Collegabile direttamente (tecnico dal cliente o in VPN): bastano indirizzo e porta.</summary>
    public bool CanConnectDirect => Host is not null && Port > 0;
}

/// <summary>Opzioni salvate per una singola connessione.</summary>
public sealed record SavedOptions(string? Rdp, string? Web, string? DatabaseName, string? DatabaseKind)
{
    public static readonly SavedOptions None = new(null, null, null, null);
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
    /// <summary>Password collegate in Hudu a ogni asset (assegnate o related): vedi <see cref="ConnectionResolver.IndexPasswordsByAsset"/>.</summary>
    public required ILookup<int, CachedPassword> PasswordsByAsset { get; init; }
    public required IReadOnlyDictionary<string, CachedPeer> Peers { get; init; }
    public required IReadOnlyDictionary<string, CachedPeer> PeersByName { get; init; }
    /// <summary>Gateway letto da Hudu a livello di cliente (asset con campo ID RustDesk).</summary>
    public required IReadOnlyDictionary<int, string> HuduCompanyGateways { get; init; }
    public required IReadOnlySet<int> Favorites { get; init; }
    /// <summary>false = RustDesk spento: niente gateway, solo connessioni dirette.</summary>
    public bool RustDeskEnabled { get; init; } = true;
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
    /// <summary>Valore di <see cref="AssetSetting.WebAddress"/> per usare l'IP invece dell'FQDN.</summary>
    public const string WebAddressIp = "ip";
    /// <summary>Indirizzo per un dispositivo che è anche il proprio jump host (RustDesk installato sopra).</summary>
    public const string SelfHost = "localhost";

    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => 3389,
        ConnectionProtocol.Ssh => 22,
        ConnectionProtocol.Telnet => 23,
        ConnectionProtocol.Http => 80,
        ConnectionProtocol.Https => 443,
        ConnectionProtocol.Winbox => 8291,
        ConnectionProtocol.Oracle => 1521,
        _ => 0,
    };

    public static string Label(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "RDP",
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Telnet => "Telnet",
        ConnectionProtocol.Http => "Web",
        ConnectionProtocol.Https => "Web",
        ConnectionProtocol.Winbox => "WinBox",
        ConnectionProtocol.Oracle => "Oracle",
        ConnectionProtocol.Tcp => "TCP",
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
        // Solo per dedurre il protocollo dalla piattaforma: il gateway non si ricava più dal nome.
        var ownPeer = ctx.RustDeskEnabled ? FindOwnPeer(asset, ctx.PeersByName) : null;
        // Cliente senza RustDesk: nessun gateway del cliente né da Hudu, solo connessioni dirette.
        var companyNoRustDesk = !ctx.RustDeskEnabled || companySetting?.NoRustDesk == true;
        var companyGateway = companyNoRustDesk ? null : companySetting?.GatewayPeerId
            ?? (ctx.HuduCompanyGateways.TryGetValue(asset.CompanyId, out var huduGateway) ? huduGateway : null);
        var companyGatewaySource = companySetting?.GatewayPeerId is not null ? "predefinito del cliente" : "Hudu, cliente";

        var connections = new List<ResolvedConnection>();

        // --- Connessione principale ---
        var protocol = setting?.Protocol ?? GuessProtocol(asset.OperatingSystem, layout, ownPeer);
        Uri? webUrl = null;

        // Gateway: scelta dell'asset nel portale, poi quella del cliente, poi Hudu (cliente e asset);
        // altrimenti avviso. "Nessun gateway" (asset o cliente) è una scelta: solo diretta, senza avvisi.
        string? gateway;
        string? gatewaySource;
        var directOnly = false;
        var (host, hostIssue) = ResolveHost(setting?.Host, asset);
        if (!ctx.RustDeskEnabled || setting?.NoGateway == true)
        {
            (gateway, gatewaySource, directOnly) = (null, null, true);
        }
        else if (setting?.GatewayPeerId is { } manualGateway)
        {
            (gateway, gatewaySource) = (manualGateway, "manuale");
        }
        else if (companyNoRustDesk)
        {
            (gateway, gatewaySource, directOnly) = (null, null, true);
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
        // Le connessioni aggiuntive senza indirizzo usano l'IP della principale.
        var ipHost = host;
        var useFqdn = !string.Equals(setting?.WebAddress, WebAddressIp, StringComparison.Ordinal);
        if (IsWebProtocol(protocol) && useFqdn && FqdnUrl(asset, host) is { } fqdn)
        {
            // Pagine web: vale l'FQDN che l'IPAM di Hudu associa all'IP (risolto dal jump host), salvo
            // la scelta "IP" nell'editor. Se in Hudu c'è un indirizzo completo, decide anche schema e porta,
            // ma solo se il tecnico non ha scelto porta o protocollo diversi (come per le aggiuntive).
            if (fqdn.Url is { } fqdnUrl
                && (setting?.Port is null || setting.Port == fqdnUrl.Port)
                && (setting?.Protocol is null || setting.Protocol == WebProtocol(fqdnUrl)))
            {
                webUrl = fqdnUrl;
                (protocol, host, port) = (WebProtocol(fqdnUrl), fqdnUrl.Host.Trim('[', ']'), fqdnUrl.Port);
            }
            else
            {
                host = fqdn.Url?.Host.Trim('[', ']') ?? fqdn.Host;
            }
        }

        // "Nessuna credenziale" è una scelta: niente credenziale e niente avviso.
        var noPassword = setting?.NoPassword == true;
        var (passwordId, passwordIssue) = noPassword ? (null, null) : ResolvePassword(setting?.PasswordId, asset, ctx);
        connections.Add(Build(PrimaryKey, Label(protocol), protocol, host, port, gateway, gatewaySource, passwordId, passwordIssue, ctx, hostIssue,
            webUrl?.AbsoluteUri, new SavedOptions(setting?.RdpOptions, setting?.WebOptions, setting?.DatabaseName, setting?.DatabaseKind),
            directOnly, credentialOptional: noPassword));

        // --- Console fuori banda (iDRAC/iLO/IPMI): suggerita dal campo di Hudu, modificabile dai tecnici ---
        var extrasOfAsset = ctx.Extras[asset.Id].ToList();
        var consoleRow = extrasOfAsset.FirstOrDefault(e => e.IsConsole);
        if (consoleRow?.Hidden != true && ResolveConsole(asset, consoleRow) is { } console)
        {
            var (consoleGateway, consoleSource, consoleDirect) = OwnAddressGateway(consoleRow, ctx, companyNoRustDesk, companyGateway,
                companyGatewaySource, gateway, gatewaySource, directOnly);
            var (consolePasswordId, consolePasswordIssue) = consoleRow?.PasswordId is not null
                ? ResolvePassword(consoleRow.PasswordId, asset, ctx)
                : (null, (string?)null);
            connections.Add(Build(ConsoleKey, string.IsNullOrWhiteSpace(consoleRow?.Label) ? "Console" : consoleRow.Label, console.Protocol, console.Host,
                console.Port, consoleGateway, consoleSource, consolePasswordId, consolePasswordIssue, ctx, url: console.Url,
                options: Options(consoleRow), directOnly: consoleDirect));
        }

        // --- Connessioni aggiuntive definite dai tecnici ---
        foreach (var extra in extrasOfAsset.Where(e => !e.IsConsole).OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase))
        {
            var extraUrl = Connections.WebUrls.Parse(extra.Url).Url;
            var extraHost = extraUrl?.Host.Trim('[', ']') ?? extra.Host ?? ipHost;
            var protocolOfExtra = extra.Protocol ?? ConnectionProtocol.Https;
            if (extraUrl is null && extra.Host is null && useFqdn && IsWebProtocol(protocolOfExtra) && FqdnUrl(asset, ipHost) is { } extraFqdn)
            {
                // Pagina web sullo stesso indirizzo dell'asset: anche qui l'FQDN dell'IPAM.
                if (extraFqdn.Url is { } extraFqdnUrl && (extra.Port is null || extra.Port == extraFqdnUrl.Port))
                {
                    extraUrl = extraFqdnUrl;
                }

                extraHost = extraFqdn.Url?.Host.Trim('[', ']') ?? extraFqdn.Host;
            }

            // Stesso indirizzo della principale: stesso gateway; indirizzo proprio: gateway del cliente.
            var (extraGateway, extraSource, extraDirect) = extra.Host is null && extra.Url is null && !extra.NoGateway && extra.GatewayPeerId is null
                ? (gateway, gatewaySource, directOnly)
                : OwnAddressGateway(extra, ctx, companyNoRustDesk, companyGateway, companyGatewaySource, gateway, gatewaySource, directOnly);
            var (extraPasswordId, extraPasswordIssue) = extra.PasswordId is not null
                ? ResolvePassword(extra.PasswordId, asset, ctx)
                : (null, (string?)null);
            var (extraProtocol, extraPort) = extraUrl is null
                ? (protocolOfExtra, extra.Port ?? DefaultPort(protocolOfExtra))
                : (WebProtocol(extraUrl), extraUrl.Port);
            connections.Add(Build(ExtraPrefix + extra.Id, extra.Label, extraProtocol, extraHost, extraPort,
                extraGateway, extraSource, extraPasswordId, extraPasswordIssue, ctx, url: extraUrl?.AbsoluteUri, options: Options(extra),
                directOnly: extraDirect));
        }

        return new ResolvedAsset(asset, companyName, setting, connections, ctx.Favorites.Contains(asset.Id));
    }

    private static bool IsWebProtocol(ConnectionProtocol protocol) => protocol is ConnectionProtocol.Http or ConnectionProtocol.Https;

    /// <summary>
    /// Gateway di una connessione con indirizzo proprio (console o aggiuntiva): scelta della connessione,
    /// poi cliente, poi quello della principale.
    /// </summary>
    private static (string? Gateway, string? Source, bool DirectOnly) OwnAddressGateway(
        ExtraConnection? row, ResolutionContext ctx, bool companyNoRustDesk, string? companyGateway, string companyGatewaySource,
        string? primaryGateway, string? primarySource, bool primaryDirectOnly)
    {
        if (!ctx.RustDeskEnabled || row?.NoGateway == true)
        {
            return (null, null, true);
        }

        if (row?.GatewayPeerId is { } manual)
        {
            return (manual, "manuale", false);
        }

        if (companyNoRustDesk)
        {
            return (null, null, true);
        }

        return companyGateway is not null
            ? (companyGateway, companyGatewaySource, false)
            : (primaryGateway, primarySource, primaryDirectOnly);
    }

    private static SavedOptions Options(ExtraConnection? row) =>
        row is null ? SavedOptions.None : new SavedOptions(row.RdpOptions, row.WebOptions, row.DatabaseName, row.DatabaseKind);

    /// <summary>Console suggerita dal campo di Hudu (null se il campo è vuoto o non valido).</summary>
    public static (ConnectionProtocol Protocol, string Host, int Port, string? Url)? HuduConsole(CachedAsset asset)
    {
        if (HostParser.Parse(asset.ConsoleAddress) is not { } console)
        {
            return null;
        }

        var protocol = console.Scheme == "http" ? ConnectionProtocol.Http : ConnectionProtocol.Https;
        // Se in Hudu c'è un indirizzo completo (con percorso), la pagina si apre proprio lì.
        var url = console.Scheme is null ? null : Connections.WebUrls.Parse(asset.ConsoleAddress!.Trim().Split([' ', ',', ';', '\n', '\t'])[0]).Url;
        return (protocol, console.Host, console.Port ?? DefaultPort(protocol), url?.AbsoluteUri);
    }

    /// <summary>
    /// Console effettiva: i campi scritti dal tecnico prevalgono, quelli vuoti seguono Hudu. Senza
    /// indirizzo né in Hudu né nella modifica non c'è console.
    /// </summary>
    public static (ConnectionProtocol Protocol, string Host, int Port, string? Url)? ResolveConsole(CachedAsset asset, ExtraConnection? row)
    {
        var hudu = HuduConsole(asset);
        if (row is null)
        {
            return hudu;
        }

        if (Connections.WebUrls.Parse(row.Url).Url is { } rowUrl)
        {
            return (WebProtocol(rowUrl), rowUrl.Host.Trim('[', ']'), rowUrl.Port, rowUrl.AbsoluteUri);
        }

        if (row.Host is { } rowHost)
        {
            var protocol = row.Protocol ?? hudu?.Protocol ?? ConnectionProtocol.Https;
            return (protocol, rowHost, row.Port ?? DefaultPort(protocol), null);
        }

        if (hudu is not { } suggested)
        {
            return null;
        }

        // Stesso indirizzo di Hudu: protocollo e porta si possono cambiare; l'URL completo di Hudu vale
        // solo se non sono stati toccati.
        var effective = row.Protocol ?? suggested.Protocol;
        var unchanged = effective == suggested.Protocol && (row.Port is null || row.Port == suggested.Port);
        return (effective, suggested.Host, row.Port ?? (effective == suggested.Protocol ? suggested.Port : DefaultPort(effective)),
            unchanged ? suggested.Url : null);
    }

    /// <summary>FQDN dell'IPAM per l'IP della connessione: un indirizzo completo (Url) o solo il nome host.</summary>
    private static (Uri? Url, string Host)? FqdnUrl(CachedAsset asset, string? ip)
    {
        if (asset.FqdnFor(ip) is not { } fqdn)
        {
            return null;
        }

        if (fqdn.Contains("://", StringComparison.Ordinal))
        {
            return Connections.WebUrls.Parse(fqdn).Url is { } url ? (url, url.Host.Trim('[', ']')) : null;
        }

        return HostParser.IsValidHost(fqdn) ? (null, fqdn) : null;
    }

    private static ConnectionProtocol WebProtocol(Uri url) => url.Scheme == Uri.UriSchemeHttp ? ConnectionProtocol.Http : ConnectionProtocol.Https;

    private static ResolvedConnection Build(
        string key, string label, ConnectionProtocol protocol, string? host, int port,
        string? gateway, string? gatewaySource, int? passwordId, string? passwordIssue, ResolutionContext ctx, string? hostIssue = null,
        string? url = null, SavedOptions? options = null, bool directOnly = false, bool credentialOptional = false)
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

        if (port <= 0)
        {
            issues.Add("Porta mancante");
        }

        if (protocol == ConnectionProtocol.Oracle && string.IsNullOrWhiteSpace(options?.DatabaseName))
        {
            issues.Add("Nome del database Oracle mancante");
        }

        if (!ctx.RustDeskEnabled || directOnly)
        {
            // Solo connessioni dirette (RustDesk spento o scelta esplicita): il gateway non serve.
        }
        else if (gateway is null)
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
        else if (password is null && !credentialOptional && protocol is ConnectionProtocol.Rdp or ConnectionProtocol.Ssh or ConnectionProtocol.Telnet or ConnectionProtocol.Winbox && key == PrimaryKey)
        {
            issues.Add("Nessuna credenziale associata");
        }

        return new ResolvedConnection(key, label, protocol, host, port, gateway, gatewaySource, password?.Id, password?.Name, password?.Username, issues, url,
            options, directOnly || !ctx.RustDeskEnabled);
    }

    /// <summary>
    /// Indirizzo della connessione principale. La fonte è l'IPAM di Hudu: con un solo IP si usa quello,
    /// con più IP vale quello scelto dal tecnico (o quello del campo del layout, se è tra questi).
    /// Senza IP nell'IPAM si usa il campo del layout, ma va segnalato: l'IP va caricato in Hudu.
    /// </summary>
    public static (string? Host, string? Issue) ResolveHost(string? chosen, CachedAsset asset)
    {
        var ipam = asset.IpamList();
        if (string.Equals(chosen, SelfHost, StringComparison.OrdinalIgnoreCase))
        {
            // Scelta esplicita: il dispositivo è il proprio jump host, il tunnel punta a sé stesso.
            return (SelfHost, null);
        }

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

    /// <summary>Indice asset → password collegate in Hudu (assegnate o related).</summary>
    public static ILookup<int, CachedPassword> IndexPasswordsByAsset(IEnumerable<CachedPassword> passwords) =>
        passwords
            .SelectMany(p => (p.AssetId is { } assigned ? [assigned] : Array.Empty<int>()).Concat(p.RelatedAssetIds()).Distinct()
                .Select(assetId => (AssetId: assetId, Password: p)))
            .ToLookup(x => x.AssetId, x => x.Password);

    /// <summary>Password collegate all'asset in Hudu, prima quelle assegnate, poi le related, per nome.</summary>
    public static IReadOnlyList<CachedPassword> LinkedPasswords(CachedAsset asset, ILookup<int, CachedPassword> passwordsByAsset) =>
        passwordsByAsset[asset.Id]
            .Where(p => p.IsLinkedTo(asset))
            .OrderBy(p => p.AssetId == asset.Id ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Credenziale della connessione: solo tra quelle collegate all'asset in Hudu (assegnate o related),
    /// così i collegamenti si sistemano in Hudu e non nel portale. Senza scelta: l'unica collegata,
    /// oppure l'unica assegnata se ci sono anche delle related.
    /// </summary>
    private static (int? Id, string? Issue) ResolvePassword(int? chosen, CachedAsset asset, ResolutionContext ctx)
    {
        var linked = LinkedPasswords(asset, ctx.PasswordsByAsset);
        if (chosen is { } id)
        {
            return linked.Any(p => p.Id == id) ? (id, null)
                : ctx.Passwords.ContainsKey(id) ? (null, "Credenziale scelta non collegata all'asset in Hudu: va collegata in Hudu")
                : (null, "Credenziale scelta non più presente in Hudu");
        }

        var assigned = linked.Where(p => p.AssetId == asset.Id).ToList();
        return linked.Count switch
        {
            0 => (null, null),
            1 => (linked[0].Id, null),
            _ when assigned.Count == 1 => (assigned[0].Id, null),
            _ => (null, $"{linked.Count} credenziali collegate in Hudu: sceglierne una"),
        };
    }

    /// <summary>
    /// Dispositivo della rubrica RustDesk con lo stesso nome dell'asset. Serve solo a dedurre il protocollo
    /// (e alla migrazione 1.5.3): il gateway non si sceglie più per nome.
    /// </summary>
    public static CachedPeer? FindOwnPeer(CachedAsset asset, IReadOnlyDictionary<string, CachedPeer> peersByName)
    {
        foreach (var name in new[] { asset.Hostname, asset.Name })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var shortName = name.Split('.')[0];
            if (peersByName.TryGetValue(shortName, out var peer))
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
