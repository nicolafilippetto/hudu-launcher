using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Connector.Core;

public sealed record ConnectorConfig(string? TenantId, string? ClientId, string? Scope, string PortalName);

/// <summary>Jump host RustDesk; ClientPath = client indicato dall'admin del portale (facoltativo).</summary>
public sealed record GatewayInfo(string Id, string? Password, string? ClientPath = null);

public sealed record TargetInfo(string Host, int Port);

public sealed record CredentialInfo(string? Username, string? Password);

/// <summary>
/// Opzioni di Desktop remoto decise nel portale (predefiniti dell'admin + eventuali override dell'asset).
/// I valori predefiniti valgono se il portale non le invia: solo appunti, tasti al computer remoto.
/// </summary>
public sealed record RdpOptions(
    bool AdminSession = false,
    bool Clipboard = true,
    bool Drives = false,
    bool Printers = false,
    bool SmartCards = false,
    bool Ports = false,
    bool Devices = false,
    bool WebAuthn = false,
    bool Microphone = false,
    string Audio = "none",
    string Keyboard = "remote",
    int ColorDepth = 32,
    string Display = "resize",
    bool Nla = true)
{
    public static RdpOptions Default { get; } = new();

    /// <summary>AudioRedirectionMode del controllo: 0 = su questo PC, 1 = sul remoto, 2 = nessun audio.</summary>
    public int AudioMode => Audio switch { "local" => 0, "remote" => 1, _ => 2 };

    /// <summary>KeyboardHookMode del controllo: 0 = questo PC, 1 = remoto, 2 = remoto solo a schermo intero.</summary>
    public int KeyboardMode => Keyboard switch { "local" => 0, "fullscreen" => 2, _ => 1 };

    public int SafeColorDepth => ColorDepth is 15 or 16 or 24 or 32 ? ColorDepth : 32;
}

public sealed record HighlightRule(string Pattern, string Color);

/// <summary>Opzioni del terminale: regole di colore scritte dagli admin nel portale.</summary>
public sealed record TerminalOptions(bool Highlight = false, IReadOnlyList<HighlightRule>? Rules = null);

public enum ConnectionProtocol
{
    Rdp,
    Ssh,
    Telnet,
    Http,
    Https,
}

/// <summary>
/// Risposta del portale al riscatto del biglietto: tutto ciò che serve per collegarsi.
/// <see cref="Gateway"/> è null per le connessioni dirette (tecnico dal cliente o in VPN).
/// </summary>
public sealed record ConnectionInfo(
    ConnectionProtocol Protocol,
    string Name,
    string Company,
    string Label,
    GatewayInfo? Gateway,
    TargetInfo Target,
    CredentialInfo? Credential,
    string HostKeyAlias,
    string? Warning,
    RdpOptions? Rdp = null,
    TerminalOptions? Terminal = null,
    string? HuduUrl = null)
{
    public bool IsDirect => Gateway is null;

    /// <summary>Descrizione del percorso, per i testi della scheda.</summary>
    public string Route => Gateway is null ? "connessione diretta" : $"tramite RustDesk {Gateway.Id}";
}

/// <summary>Pacchetto della cache offline: connessioni dirette valide per <see cref="ValidSeconds"/>.</summary>
public sealed record OfflineBundle(int ValidSeconds, IReadOnlyList<ConnectionInfo> Connections);

/// <summary>Dove si collega la sessione: la porta locale del tunnel RustDesk oppure direttamente il dispositivo.</summary>
public sealed record SessionEndpoint(string Host, int Port, bool Direct)
{
    public static SessionEndpoint ForTunnel(int localPort) => new("127.0.0.1", localPort, false);

    public static SessionEndpoint ForTarget(TargetInfo target) => new(target.Host, target.Port, true);

    /// <summary>Host per un URL (gli IPv6 vanno tra parentesi quadre).</summary>
    public string UrlHost => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
}

public sealed class PortalException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Chiamate al portale. L'indirizzo è quello delle impostazioni, confermato dall'utente.</summary>
public sealed class PortalClient(HttpClient http, Uri portal)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Uri Portal { get; } = portal;

    /// <summary>true se il portale è in https (http è ammesso solo per prove su indirizzi interni).</summary>
    public bool IsSecure => Portal.Scheme == Uri.UriSchemeHttps;

    public static Uri ValidatePortalUrl(string? value) =>
        TryNormalizePortalUrl(value)
        ?? throw new PortalException("Indirizzo del portale non configurato o non valido: serve https (http solo su indirizzi IP interni, per prova).");

    /// <summary>
    /// Riduce l'indirizzo a schema://host:porta/. Ammessi https, oppure http verso localhost o un
    /// indirizzo IP privato (portale di prova non ancora pubblicato); mai credenziali nell'indirizzo.
    /// </summary>
    public static Uri? TryNormalizePortalUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsInternalHost(uri))))
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsInternalHost(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(uri.DnsSafeHost, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public async Task<ConnectorConfig> GetConfigAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(Portal, "api/connector/config")), ct);
        var config = await response.Content.ReadFromJsonAsync<ConnectorConfig>(Json, ct)
            ?? throw new PortalException("Risposta del portale non valida.");
        if (string.IsNullOrEmpty(config.TenantId) || string.IsNullOrEmpty(config.ClientId) || string.IsNullOrEmpty(config.Scope))
        {
            throw new PortalException("Il portale non ha ancora Microsoft 365 configurato.");
        }

        return config;
    }

    /// <summary>Riscatta il biglietto della cache offline: connessioni dirette con le credenziali.</summary>
    public async Task<OfflineBundle> RedeemOfflineAsync(string ticket, string? accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Portal, "api/connector/redeem-offline"))
        {
            Content = JsonContent.Create(new { ticket }),
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await SendAsync(request, ct);
        return await response.Content.ReadFromJsonAsync<OfflineBundle>(Json, ct)
            ?? throw new PortalException("Risposta del portale non valida.");
    }

    /// <summary>Riscatta il biglietto; senza token solo per i biglietti degli admin locali.</summary>
    public async Task<ConnectionInfo> RedeemAsync(string ticket, string? accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Portal, "api/connector/redeem"))
        {
            Content = JsonContent.Create(new { ticket }),
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await SendAsync(request, ct);
        return await response.Content.ReadFromJsonAsync<ConnectionInfo>(Json, ct)
            ?? throw new PortalException("Risposta del portale non valida.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using (request)
            {
                response = await http.SendAsync(request, ct);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new PortalException($"Portale non raggiungibile ({Portal.Host}): {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PortalException($"Il portale ({Portal.Host}) non risponde.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var detail = await ReadProblemAsync(response, ct);
            throw new PortalException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Il portale non ha accettato l'accesso Microsoft 365. " + detail,
                HttpStatusCode.TooManyRequests => "Troppe richieste: attendi qualche minuto.",
                _ => detail ?? $"Errore del portale ({(int)response.StatusCode}).",
            });
        }
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var name in new[] { "detail", "title" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // corpo non JSON
        }

        return null;
    }
}
