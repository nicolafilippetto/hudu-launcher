using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Launcher.Server.Settings;

namespace Launcher.Server.Integrations;

public sealed record HuduCompany(int Id, string Name, string? Nickname, string? Url);

public sealed record HuduLayout(int Id, string Name, string? Icon, IReadOnlyList<string> FieldLabels);

public sealed record HuduAsset(
    int Id,
    int CompanyId,
    int LayoutId,
    string Name,
    string? Url,
    bool Archived,
    IReadOnlyDictionary<string, string?> Fields);

/// <summary>Metadati di una password: il valore non viene letto.</summary>
public sealed record HuduPasswordInfo(int Id, int CompanyId, string Name, string? Username, string? Url, string? PasswordableType, int? PasswordableId);

/// <summary>Indirizzo IP dell'IPAM di Hudu, collegato a un asset.</summary>
public sealed record HuduIpAddress(string Address, string? Status, string? Fqdn, int? AssetId, int CompanyId);

public sealed record HuduCredential(int Id, int CompanyId, string Name, string? Username, string? Password, string? Url);

public sealed class HuduException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client minimo per l'API REST di Hudu (<c>/api/v1</c>, intestazione <c>x-api-key</c>).
/// Rispetta il limite di richieste di Hudu (circa 300 al minuto) e riprova sui 429.
/// </summary>
public sealed class HuduClient(IHttpClientFactory httpFactory, SettingsService settings, IHostEnvironment environment, ILogger<HuduClient> logger)
{
    public const string HttpClientName = "hudu";

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(220);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <param name="allowHttp">Solo in sviluppo, per i test con un Hudu finto.</param>
    public static Uri BuildApiBase(string baseUrl, bool allowHttp = false)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new HuduException("L'indirizzo di Hudu deve essere un URL https, es. https://azienda.huducloud.com");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/api/v1";
        }

        return new UriBuilder(uri) { Path = path + "/", Query = "" }.Uri;
    }

    public async Task<IReadOnlyList<HuduCompany>> GetCompaniesAsync(CancellationToken ct)
    {
        var result = new List<HuduCompany>();
        await foreach (var item in GetPagedAsync("companies", "companies", null, ct))
        {
            result.Add(new HuduCompany(
                GetInt(item, "id") ?? 0,
                GetString(item, "name") ?? "(senza nome)",
                GetString(item, "nickname"),
                AbsoluteUrl(GetString(item, "full_url") ?? GetString(item, "url"))));
        }

        return result;
    }

    public async Task<IReadOnlyList<HuduLayout>> GetLayoutsAsync(CancellationToken ct)
    {
        var result = new List<HuduLayout>();
        await foreach (var item in GetPagedAsync("asset_layouts", "asset_layouts", null, ct))
        {
            var labels = new List<string>();
            if (item.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    var label = GetString(field, "label");
                    var type = GetString(field, "field_type");
                    if (!string.IsNullOrWhiteSpace(label) && type is not ("Divider" or "Heading" or "Password"))
                    {
                        labels.Add(label);
                    }
                }
            }

            result.Add(new HuduLayout(GetInt(item, "id") ?? 0, GetString(item, "name") ?? "?", GetString(item, "icon"), labels));
        }

        return result;
    }

    public Task<IReadOnlyList<HuduAsset>> GetAssetsAsync(int layoutId, CancellationToken ct) =>
        GetAssetsCoreAsync($"asset_layout_id={layoutId}&archived=false", layoutId, ct);

    /// <summary>Tutti gli asset di un cliente (per l'aggiornamento del singolo cliente).</summary>
    public Task<IReadOnlyList<HuduAsset>> GetCompanyAssetsAsync(int companyId, CancellationToken ct) =>
        GetAssetsCoreAsync($"company_id={companyId}&archived=false", 0, ct);

    private async Task<IReadOnlyList<HuduAsset>> GetAssetsCoreAsync(string query, int layoutId, CancellationToken ct)
    {
        var result = new List<HuduAsset>();
        await foreach (var item in GetPagedAsync("assets", "assets", query, ct))
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("fields", out var fieldArray) && fieldArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldArray.EnumerateArray())
                {
                    var label = GetString(field, "label");
                    if (!string.IsNullOrWhiteSpace(label) && field.TryGetProperty("value", out var value))
                    {
                        fields[label] = ValueToText(value);
                    }
                }
            }

            var archived = item.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
            result.Add(new HuduAsset(
                GetInt(item, "id") ?? 0,
                GetInt(item, "company_id") ?? 0,
                GetInt(item, "asset_layout_id") ?? layoutId,
                GetString(item, "name") ?? "(senza nome)",
                AbsoluteUrl(GetString(item, "url")),
                archived,
                fields));
        }

        return result;
    }

    /// <summary>Elenco delle password senza i valori, che vengono scartati appena letti.</summary>
    public async Task<IReadOnlyList<HuduPasswordInfo>> GetPasswordInfosAsync(CancellationToken ct, int? companyId = null)
    {
        var result = new List<HuduPasswordInfo>();
        await foreach (var item in GetPagedAsync("asset_passwords", "asset_passwords", companyId is null ? null : $"company_id={companyId}", ct))
        {
            if (item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            result.Add(new HuduPasswordInfo(
                GetInt(item, "id") ?? 0,
                GetInt(item, "company_id") ?? 0,
                GetString(item, "name") ?? "(senza nome)",
                GetString(item, "username"),
                GetString(item, "url"),
                GetString(item, "passwordable_type"),
                GetInt(item, "passwordable_id")));
        }

        return result;
    }

    /// <summary>Indirizzi dell'IPAM (Hudu → IPAM) collegati a un asset; tutti o di un solo cliente.</summary>
    public async Task<IReadOnlyList<HuduIpAddress>> GetIpAddressesAsync(CancellationToken ct, int? companyId = null)
    {
        // Questo endpoint non accetta page/page_size (risponde 400) e restituisce tutto in un array.
        var result = new List<HuduIpAddress>();
        using var doc = await SendAsync("ip_addresses" + (companyId is null ? "" : $"?company_id={companyId}"), ct);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("ip_addresses", out var inner) ? inner : default;
        if (items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            var address = GetString(item, "address");
            var assetId = GetInt(item, "asset_id");
            var discarded = item.TryGetProperty("discarded_at", out var d) && d.ValueKind == JsonValueKind.String;
            if (string.IsNullOrWhiteSpace(address) || assetId is null or 0 || discarded)
            {
                continue;
            }

            result.Add(new HuduIpAddress(address.Trim(), GetString(item, "status"), GetString(item, "fqdn"), assetId, GetInt(item, "company_id") ?? 0));
        }

        return result;
    }

    /// <summary>Legge una singola credenziale, al momento della connessione.</summary>
    public async Task<HuduCredential> GetCredentialAsync(int passwordId, CancellationToken ct)
    {
        using var doc = await SendAsync($"asset_passwords/{passwordId}", ct);
        var item = doc.RootElement.TryGetProperty("asset_password", out var inner) ? inner : doc.RootElement;
        return new HuduCredential(
            GetInt(item, "id") ?? passwordId,
            GetInt(item, "company_id") ?? 0,
            GetString(item, "name") ?? "",
            GetString(item, "username"),
            GetString(item, "password"),
            GetString(item, "url"));
    }

    /// <summary>Prova di connessione per la pagina admin: restituisce il numero di layout.</summary>
    public async Task<int> TestAsync(CancellationToken ct)
    {
        using var doc = await SendAsync("asset_layouts?page=1", ct);
        return doc.RootElement.TryGetProperty("asset_layouts", out var layouts) ? layouts.GetArrayLength() : 0;
    }

    private async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string path, string rootProperty, string? query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pageSize = settings.Hudu.PageSize;
        string? firstIdOfPreviousPage = null;
        for (var page = 1; page <= 10_000; page++)
        {
            var uri = $"{path}?page={page}&page_size={pageSize}" + (query is null ? "" : "&" + query);
            using var doc = await SendAsync(uri, ct);
            // Alcuni endpoint (es. ip_addresses) restituiscono direttamente l'array.
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty(rootProperty, out var inner) ? inner : default;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                yield break;
            }

            // Protezione: se l'API ignorasse la paginazione si ripeterebbe sempre la stessa pagina.
            var firstId = GetInt(items[0], "id")?.ToString(CultureInfo.InvariantCulture) ?? items[0].GetRawText();
            if (firstId == firstIdOfPreviousPage)
            {
                logger.LogWarning("Hudu ha restituito due volte la stessa pagina per {Path}: interrompo", path);
                yield break;
            }

            firstIdOfPreviousPage = firstId;
            foreach (var item in items.EnumerateArray())
            {
                yield return item.Clone();
            }
        }
    }

    private async Task<JsonDocument> SendAsync(string relative, CancellationToken ct)
    {
        var hudu = settings.Hudu;
        if (!hudu.IsConfigured)
        {
            throw new HuduException("Hudu non è configurato (indirizzo e chiave API nella sezione admin).");
        }

        var uri = new Uri(BuildApiBase(hudu.BaseUrl!, environment.IsDevelopment()), relative);
        var client = httpFactory.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("x-api-key", hudu.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (IsNetworkError(ex, ct) && attempt < 3)
            {
                logger.LogWarning(ex, "Errore di rete verso Hudu, tentativo {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            catch (Exception ex) when (IsNetworkError(ex, ct))
            {
                throw new HuduException($"Hudu non raggiungibile ({uri.Host}): {NetworkErrorText(ex)}", ex);
            }

            using (response)
            {
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    logger.LogWarning("Hudu ha risposto {Status}, attendo {Wait}", (int)response.StatusCode, wait);
                    await Task.Delay(wait, ct);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new HuduException("Hudu ha rifiutato la chiave API (verificare chiave e permessi, incluso l'accesso alle password).");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HuduException($"Hudu ha risposto {(int)response.StatusCode} per {relative.Split('?')[0]}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
        }
    }

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            Gate.Release();
        }
    }

    private string? AbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || environment.IsDevelopment()))
        {
            return absolute.ToString();
        }

        if (url.StartsWith('/') && settings.Hudu.BaseUrl is { } baseUrl && Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
        {
            return new Uri(root, url).ToString();
        }

        return null;
    }

    /// <summary>Errore di rete o timeout (non un annullamento richiesto dall'utente).</summary>
    internal static bool IsNetworkError(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    internal static string NetworkErrorText(Exception ex) =>
        ex is TaskCanceledException
            ? "nessuna risposta entro il tempo limite. Controllare indirizzo, porta, DNS e firewall dal server Docker."
            : ex.Message;

    internal static string? ValueToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(ValueToText).Where(v => !string.IsNullOrWhiteSpace(v))),
        _ => null,
    };

    internal static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? ValueToText(value) : null;

    internal static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }
}

/// <summary>
/// Sola lettura garantita: qualsiasi richiesta verso Hudu che non sia GET viene rifiutata prima di
/// partire, anche se un futuro errore nel codice provasse a modificare o cancellare qualcosa.
/// </summary>
public sealed class ReadOnlyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Method == HttpMethod.Get
            ? base.SendAsync(request, cancellationToken)
            : throw new HuduException($"Bloccata una richiesta {request.Method} verso Hudu: il Launcher accede solo in lettura.");
}
