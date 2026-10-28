namespace Launcher.Server.Data;

/// <summary>Protocolli supportati dal connettore.</summary>
public enum ConnectionProtocol
{
    Rdp = 1,
    Ssh = 2,
    Telnet = 3,
    Http = 4,
    Https = 5,
}

/// <summary>Amministratore del portale. Chi accede con 365 e ha questa email è admin.</summary>
public sealed class AdminEntry
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
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
}

/// <summary>Metadati di una password Hudu. Il valore non viene mai salvato.</summary>
public sealed class CachedPassword
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public required string Name { get; set; }
    public string? Username { get; set; }
    public string? Url { get; set; }
    public int? AssetId { get; set; }
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
    public bool Hidden { get; set; }
    public string? Note { get; set; }
    /// <summary>Opzioni RDP che sovrascrivono i predefiniti (JSON chiave → valore).</summary>
    public string? RdpOptions { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Connessioni aggiuntive di un asset (es. interfaccia web, porta SSH alternativa).</summary>
public sealed class ExtraConnection
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public required string Label { get; set; }
    public ConnectionProtocol Protocol { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public int? PasswordId { get; set; }
    public string? GatewayPeerId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Impostazioni per cliente: il jump host RustDesk predefinito.</summary>
public sealed class CompanySetting
{
    public int CompanyId { get; set; }
    public string? GatewayPeerId { get; set; }
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
