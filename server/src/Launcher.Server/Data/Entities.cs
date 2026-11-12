namespace Launcher.Server.Data;

/// <summary>Protocolli supportati dal connettore.</summary>
public enum ConnectionProtocol
{
    Rdp = 1,
    Ssh = 2,
    Telnet = 3,
    Http = 4,
    Https = 5,
    /// <summary>MikroTik WinBox: il Launcher apre winbox.exe verso il dispositivo (o il tunnel).</summary>
    Winbox = 6,
    /// <summary>Database Oracle: il Launcher apre il tunnel e scrive la voce nel tnsnames.ora del tecnico.</summary>
    Oracle = 7,
    /// <summary>Altro servizio TCP: il Launcher apre il tunnel e indica indirizzo e porta da usare.</summary>
    Tcp = 8,
}

/// <summary>Come si indica il database Oracle nella voce del tnsnames.ora.</summary>
public static class DatabaseKinds
{
    public const string ServiceName = "service";
    public const string Sid = "sid";

    public static bool IsValid(string? kind) => kind is null or ServiceName or Sid;
}

/// <summary>Accesso di emergenza (break-glass): utente locale con password e MFA, sempre amministratore.</summary>
public sealed class AdminEntry
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public enum PortalUserStatus
{
    /// <summary>Primo accesso con Microsoft 365: nessun accesso finché un amministratore non approva.</summary>
    Pending = 0,
    Approved = 1,
    /// <summary>Bloccato: resta tale anche ai prossimi accessi.</summary>
    Blocked = 2,
}

/// <summary>
/// Utente Microsoft 365 riconosciuto dal suo identificativo immutabile (tenant + object ID), non
/// dall'email. Al primo accesso finisce in attesa; può entrare solo dopo l'approvazione di un
/// amministratore. Email e nome si aggiornano a ogni accesso e servono solo per la visualizzazione.
/// </summary>
public sealed class PortalUser
{
    public int Id { get; set; }
    public required string TenantId { get; set; }
    public required string ObjectId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public PortalUserStatus Status { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? LastIp { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>Impostazione del portale. I valori segreti sono cifrati.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Logo e favicon caricati dagli admin.</summary>
public sealed class BrandingAsset
{
    public required string Kind { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Configurazione di un layout Hudu: se è collegabile e dove trovare i dati.</summary>
public sealed class LayoutConfig
{
    public int LayoutId { get; set; }
    public required string LayoutName { get; set; }
    public bool Enabled { get; set; }
    public string? IpFieldLabel { get; set; }
    public string? OsFieldLabel { get; set; }
    public string? HostnameFieldLabel { get; set; }
    public string? ConsoleFieldLabel { get; set; }
    public string? RustDeskIdFieldLabel { get; set; }
    /// <summary>null = automatico in base al sistema operativo.</summary>
    public ConnectionProtocol? DefaultProtocol { get; set; }
}

// ---------------------------------------------------------------------------
// Cache dell'inventario (ricostruita a ogni sincronizzazione, nessuna password)
// ---------------------------------------------------------------------------

public sealed class CachedCompany
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Nickname { get; set; }
    public string? HuduUrl { get; set; }
}

public sealed class CachedLayout
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Icon { get; set; }
    /// <summary>Etichette dei campi, separate da a capo.</summary>
    public string FieldLabels { get; set; } = "";
}

public sealed class CachedAsset
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int LayoutId { get; set; }
    public required string LayoutName { get; set; }
    public required string Name { get; set; }
    public string? HuduUrl { get; set; }
    public string? Hostname { get; set; }
    /// <summary>IP dal campo del layout (usato solo se l'asset non ha IP nell'IPAM).</summary>
    public string? PrimaryIp { get; set; }
    /// <summary>IP dell'IPAM di Hudu collegati all'asset, uno per riga, dal preferito.</summary>
    public string? IpamAddresses { get; set; }
    /// <summary>FQDN dell'IPAM per gli IP che lo hanno: una riga per IP, "ip fqdn".</summary>
    public string? IpamFqdns { get; set; }
    public string? OperatingSystem { get; set; }
    public string? ConsoleAddress { get; set; }
    public string? RustDeskId { get; set; }
    /// <summary>
    /// false per gli asset letti solo per trovare il gateway del cliente
    /// (es. "VPN &amp; Remote Access" con il campo ID RustDesk).
    /// </summary>
    public bool IsConnectable { get; set; } = true;
    /// <summary>Testo normalizzato per la ricerca (nome, IP, hostname, cliente).</summary>
    public string SearchText { get; set; } = "";

    public IReadOnlyList<string> IpamList() =>
        string.IsNullOrEmpty(IpamAddresses) ? [] : IpamAddresses.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>FQDN che l'IPAM di Hudu associa a questo IP, se c'è.</summary>
    public string? FqdnFor(string? ip) =>
        ip is null || string.IsNullOrEmpty(IpamFqdns)
            ? null
            : IpamFqdns.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && string.Equals(parts[0], ip, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1])
                .FirstOrDefault();
}

/// <summary>Metadati di una password Hudu. Il valore non viene mai salvato.</summary>
public sealed class CachedPassword
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public required string Name { get; set; }
    public string? Username { get; set; }
    public string? Url { get; set; }
    /// <summary>Asset a cui la password è assegnata in Hudu (creata dentro l'asset).</summary>
    public int? AssetId { get; set; }
    /// <summary>Asset collegati come "related" in Hudu, separati da virgola.</summary>
    public string? RelatedAssets { get; set; }

    public IReadOnlyList<int> RelatedAssetIds() =>
        string.IsNullOrEmpty(RelatedAssets)
            ? []
            : [.. RelatedAssets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)];

    /// <summary>
    /// Password collegata all'asset in Hudu: assegnata o "related", e dello stesso cliente (una
    /// relazione verso la password di un altro cliente non vale).
    /// </summary>
    public bool IsLinkedTo(CachedAsset asset) =>
        CompanyId == asset.CompanyId && (AssetId == asset.Id || RelatedAssetIds().Contains(asset.Id));
}

/// <summary>Dispositivo della rubrica condivisa di RustDesk.</summary>
public sealed class CachedPeer
{
    public required string Id { get; set; }
    public string? Alias { get; set; }
    public string? Hostname { get; set; }
    public string? Platform { get; set; }
    public string? Tags { get; set; }
    public string? AddressBook { get; set; }
}

public sealed class SyncState
{
    public int Id { get; set; } = 1;
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int Companies { get; set; }
    public int Assets { get; set; }
    public int Passwords { get; set; }
    public int Peers { get; set; }
}

// ---------------------------------------------------------------------------
// Mappature dei tecnici (persistenti)
// ---------------------------------------------------------------------------

/// <summary>Correzioni manuali alla connessione principale di un asset.</summary>
public sealed class AssetSetting
{
    public int AssetId { get; set; }
    public string? Host { get; set; }
    public ConnectionProtocol? Protocol { get; set; }
    public int? Port { get; set; }
    public int? PasswordId { get; set; }
    public string? GatewayPeerId { get; set; }
    /// <summary>Scelta esplicita: nessun jump host, la connessione è solo diretta (niente avviso sul gateway).</summary>
    public bool NoGateway { get; set; }
    /// <summary>Scelta esplicita: nessuna credenziale, si inserisce a mano (niente avviso sulla credenziale).</summary>
    public bool NoPassword { get; set; }
    public bool Hidden { get; set; }
    public string? Note { get; set; }
    /// <summary>Opzioni RDP che sovrascrivono i predefiniti (JSON chiave → valore).</summary>
    public string? RdpOptions { get; set; }
    /// <summary>
    /// URL della pagina web scritto a mano nelle versioni precedenti: non si usa più (l'indirizzo viene
    /// dall'FQDN dell'IPAM di Hudu). Resta solo per segnalarlo nell'editor; si cancella al salvataggio.
    /// </summary>
    public string? WebUrl { get; set; }
    /// <summary>Indirizzo delle pagine web: null = FQDN dell'IPAM (se c'è), "ip" = sempre l'IP.</summary>
    public string? WebAddress { get; set; }
    /// <summary>Opzioni delle pagine web che sovrascrivono i predefiniti (JSON chiave → valore).</summary>
    public string? WebOptions { get; set; }
    /// <summary>Database Oracle: service name o SID (vedi <see cref="DatabaseKind"/>).</summary>
    public string? DatabaseName { get; set; }
    /// <summary><see cref="DatabaseKinds"/>: null = service name.</summary>
    public string? DatabaseKind { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Connessioni aggiuntive di un asset (es. interfaccia web, porta SSH alternativa). Con
/// <see cref="IsConsole"/> la riga modifica (o nasconde) la console suggerita da Hudu: i campi
/// vuoti continuano a seguire Hudu.
/// </summary>
public sealed class ExtraConnection
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public required string Label { get; set; }
    /// <summary>null solo per la console: segue lo schema dell'indirizzo in Hudu.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public ConnectionProtocol? Protocol
    {
        get => StoredProtocol == 0 ? null : (ConnectionProtocol)StoredProtocol;
        set => StoredProtocol = value is { } protocol ? (int)protocol : 0;
    }

    /// <summary>Colonna "Protocol": 0 = come in Hudu (la colonna resta non nulla, senza ricostruire la tabella).</summary>
    public int StoredProtocol { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    /// <summary>Indirizzo completo per le connessioni web; host e porta si ricavano da qui.</summary>
    public string? Url { get; set; }
    public int? PasswordId { get; set; }
    public string? GatewayPeerId { get; set; }
    /// <summary>Opzioni RDP di questa connessione (JSON chiave → valore).</summary>
    public string? RdpOptions { get; set; }
    /// <summary>Opzioni delle pagine web di questa connessione (JSON chiave → valore).</summary>
    public string? WebOptions { get; set; }
    public string? DatabaseName { get; set; }
    public string? DatabaseKind { get; set; }
    /// <summary>Scelta esplicita: nessun jump host per questa connessione (solo diretta).</summary>
    public bool NoGateway { get; set; }
    /// <summary>Modifica della console suggerita da Hudu (una sola per asset).</summary>
    public bool IsConsole { get; set; }
    /// <summary>Solo per la console: nascosta dal tecnico (es. iDRAC dismesso ma ancora in Hudu).</summary>
    public bool Hidden { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Launcher usato da un utente: versione dichiarata, PC e ultimo uso.</summary>
public sealed class LauncherClient
{
    /// <summary>Email dell'utente, in minuscolo.</summary>
    public required string Email { get; set; }
    public string? Version { get; set; }
    public string? Machine { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Impostazioni per cliente: il jump host RustDesk predefinito, o nessun RustDesk.</summary>
public sealed class CompanySetting
{
    public int CompanyId { get; set; }
    public string? GatewayPeerId { get; set; }
    /// <summary>Il cliente non usa RustDesk: connessioni solo dirette, salvo un gateway scritto a mano sull'asset.</summary>
    public bool NoRustDesk { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class Favorite
{
    public required string UserEmail { get; set; }
    public int AssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Biglietti e registro
// ---------------------------------------------------------------------------

/// <summary>Biglietto monouso per il connettore. Si salva solo l'hash.</summary>
public sealed class Ticket
{
    public required string Id { get; set; }
    public required string UserEmail { get; set; }
    public int AssetId { get; set; }
    public required string ConnectionKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    /// <summary>Prenotazione del Launcher appena ricevuto il link: la scadenza è stata estesa (una volta sola).</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClientIp { get; set; }

    /// <summary>Accesso con cui è stato emesso: "entra" (serve il token del connettore) o "local" (admin locale).</summary>
    public string AuthMethod { get; set; } = "entra";

    /// <summary>Connessione diretta, senza jump host RustDesk.</summary>
    public bool Direct { get; set; }

    /// <summary>Pacchetto offline: ID degli asset separati da virgola (null per i biglietti normali).</summary>
    public string? OfflineAssets { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Action { get; set; }
    public string? UserEmail { get; set; }
    public bool Success { get; set; } = true;
    public string? CompanyName { get; set; }
    public int? AssetId { get; set; }
    public string? AssetName { get; set; }
    public string? Target { get; set; }
    public string? Ip { get; set; }
    public string? Details { get; set; }
}
