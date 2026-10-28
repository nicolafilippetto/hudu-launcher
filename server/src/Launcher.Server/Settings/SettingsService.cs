using System.Collections.Frozen;
using Launcher.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Settings;

/// <summary>Chiavi delle impostazioni modificabili dagli admin.</summary>
public static class SettingKeys
{
    public const string PortalName = "portal.name";
    public const string AllowLocalAdminConnections = "portal.allowLocalAdminConnections";
    public const string OfflineMaxAssets = "portal.offlineMaxAssets";
    public const string OfflineMinutes = "portal.offlineMinutes";
    public const string LocalLoginNetworks = "portal.localLoginNetworks";
    public const string SessionHours = "portal.sessionHours";
    public const string TicketSeconds = "portal.ticketSeconds";
    public const string TimeZone = "portal.timeZone";
    public const string AuditRetentionDays = "portal.auditRetentionDays";

    public const string HuduBaseUrl = "hudu.baseUrl";
    public const string HuduApiKey = "hudu.apiKey";
    public const string HuduSyncMinutes = "hudu.syncMinutes";
    public const string HuduPageSize = "hudu.pageSize";

    public const string RdpDefaults = "rdp.defaults";
    public const string TerminalHighlight = "terminal.highlight";
    public const string TerminalHighlightRules = "terminal.highlightRules";

    public const string RustDeskApiUrl = "rustdesk.apiUrl";
    public const string RustDeskApiToken = "rustdesk.apiToken";
    public const string RustDeskAddressBook = "rustdesk.addressBook";
    public const string RustDeskConnectionPassword = "rustdesk.connectionPassword";

    public const string EntraTenantId = "entra.tenantId";
    public const string EntraClientId = "entra.clientId";
    public const string EntraClientSecret = "entra.clientSecret";
    /// <summary>Certificato attivo (pubblico, Base64) e la sua chiave privata (segreta).</summary>
    public const string EntraCertificate = "entra.certificate";
    public const string EntraCertificateKey = "entra.certificateKey";
    /// <summary>Nuovo certificato generato, da caricare in Entra e provare prima di attivarlo.</summary>
    public const string EntraCertificateNext = "entra.certificateNext";
    public const string EntraCertificateNextKey = "entra.certificateNextKey";

    public static readonly FrozenSet<string> Secrets = new[]
    {
        HuduApiKey, RustDeskApiToken, RustDeskConnectionPassword, EntraClientSecret, EntraCertificateKey, EntraCertificateNextKey,
    }.ToFrozenSet();
}

public sealed record PortalSettings(
    string Name,
    bool AllowLocalAdminConnections,
    int OfflineMaxAssets,
    int OfflineMinutes,
    string LocalLoginNetworks,
    int SessionHours,
    int TicketSeconds,
    string TimeZone,
    int AuditRetentionDays);

public sealed record HuduSettings(string? BaseUrl, string? ApiKey, int SyncMinutes, int PageSize)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record RustDeskSettings(string? ApiUrl, string? ApiToken, string? AddressBook, string? ConnectionPassword)
{
    public bool IsApiConfigured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiToken);
}

public sealed record EntraSettings(
    string? TenantId,
    string? ClientId,
    string? ClientSecret,
    string? Certificate = null,
    string? CertificateKey = null,
    string? NextCertificate = null,
    string? NextCertificateKey = null)
{
    /// <summary>Il portale si presenta a Entra con il certificato (consigliato) invece del segreto.</summary>
    public bool UsesCertificate => !string.IsNullOrEmpty(Certificate) && !string.IsNullOrEmpty(CertificateKey);

    public bool HasNextCertificate => !string.IsNullOrEmpty(NextCertificate) && !string.IsNullOrEmpty(NextCertificateKey);

    public bool IsConfigured =>
        Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _) && (UsesCertificate || !string.IsNullOrWhiteSpace(ClientSecret));

    /// <summary>Scope che il connettore Windows chiede a Entra per chiamare l'API.</summary>
    public string ConnectorScope => $"api://{ClientId}/{ConnectorScopeName}";

    public const string ConnectorScopeName = "Launcher.Connect";
}

/// <summary>
/// Impostazioni del portale, salvate nel database. I segreti sono cifrati con Data Protection
/// (le cui chiavi sono a loro volta cifrate con la chiave master). I valori sono tenuti in
/// memoria e ricaricati a ogni salvataggio.
/// </summary>
public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection, ILogger<SettingsService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Launcher.Settings.v1");
    private volatile FrozenDictionary<string, string> _values = FrozenDictionary<string, string>.Empty;

    /// <summary>Scatta dopo ogni salvataggio, con le chiavi modificate.</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.AsNoTracking().ToListAsync(cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Value is null)
            {
                continue;
            }

            if (row.IsSecret)
            {
                try
                {
                    values[row.Key] = _protector.Unprotect(row.Value);
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    logger.LogError(ex, "Impossibile decifrare l'impostazione {Key}: la chiave master è cambiata?", row.Key);
                }
            }
            else
            {
                values[row.Key] = row.Value;
            }
        }

        _values = values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool HasValue(string key) => _values.ContainsKey(key);

    /// <summary>
    /// Salva le impostazioni. Un valore null cancella la chiave; per i segreti una stringa vuota
    /// significa "lascia invariato" (i form non rimandano mai il valore attuale al browser).
    /// </summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, string?> updates, string? user, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var changed = new List<string>();
        foreach (var (key, rawValue) in updates)
        {
            var isSecret = SettingKeys.Secrets.Contains(key);
            var value = rawValue?.Trim();
            if (isSecret && value == "")
            {
                continue;
            }

            var row = await db.Settings.FindAsync([key], cancellationToken);
            if (value is null or "")
            {
                if (row is not null)
                {
                    db.Settings.Remove(row);
                    changed.Add(key);
                }

                continue;
            }

            if (!isSecret && row?.Value == value)
            {
                continue;
            }

            if (row is null)
            {
                row = new Setting { Key = key };
                db.Settings.Add(row);
            }

            row.IsSecret = isSecret;
            row.Value = isSecret ? _protector.Protect(value) : value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = user;
            changed.Add(key);
        }

        await db.SaveChangesAsync(cancellationToken);
        await LoadAsync(cancellationToken);
        if (changed.Count > 0)
        {
            Changed?.Invoke(changed);
        }
    }

    public PortalSettings Portal => new(
        Get(SettingKeys.PortalName) ?? "Hudu Launcher",
        GetBool(SettingKeys.AllowLocalAdminConnections, false),
        GetInt(SettingKeys.OfflineMaxAssets, 10, 0, 25),
        GetInt(SettingKeys.OfflineMinutes, 60, 15, 480),
        Get(SettingKeys.LocalLoginNetworks) ?? "127.0.0.1/32, ::1/128, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16",
        GetInt(SettingKeys.SessionHours, 10, 1, 24),
        GetInt(SettingKeys.TicketSeconds, 30, 10, 120),
        Get(SettingKeys.TimeZone) ?? "Europe/Rome",
        GetInt(SettingKeys.AuditRetentionDays, 365, 30, 3650));

    /// <summary>Predefiniti RDP scelti dagli admin (solo le opzioni impostate).</summary>
    public Dictionary<string, string> RdpDefaults => Connections.RdpOptionCatalog.Parse(Get(SettingKeys.RdpDefaults));

    /// <summary>Testo delle regole di colore del terminale (quelle predefinite se mai modificate).</summary>
    public string TerminalRulesText => Get(SettingKeys.TerminalHighlightRules) ?? Connections.HighlightRules.Default;

    public Connections.TerminalOptions Terminal => new(
        GetBool(SettingKeys.TerminalHighlight, true),
        Connections.HighlightRules.Parse(TerminalRulesText).Rules);

    public HuduSettings Hudu => new(
        Get(SettingKeys.HuduBaseUrl),
        Get(SettingKeys.HuduApiKey),
        GetInt(SettingKeys.HuduSyncMinutes, 15, 5, 1440),
        GetInt(SettingKeys.HuduPageSize, 500, 25, 1000));

    public RustDeskSettings RustDesk => new(
        Get(SettingKeys.RustDeskApiUrl),
        Get(SettingKeys.RustDeskApiToken),
        Get(SettingKeys.RustDeskAddressBook),
        Get(SettingKeys.RustDeskConnectionPassword));

    public EntraSettings Entra => new(
        Get(SettingKeys.EntraTenantId),
        Get(SettingKeys.EntraClientId),
        Get(SettingKeys.EntraClientSecret),
        Get(SettingKeys.EntraCertificate),
        Get(SettingKeys.EntraCertificateKey),
        Get(SettingKeys.EntraCertificateNext),
        Get(SettingKeys.EntraCertificateNextKey));

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    private int GetInt(string key, int fallback, int min, int max) =>
        int.TryParse(Get(key), out var value) ? Math.Clamp(value, min, max) : fallback;
}
