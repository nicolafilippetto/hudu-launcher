using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Launcher.Server.Settings;

namespace Launcher.Server.Integrations;

public sealed record RustDeskAddressBook(string Guid, string Name);

public sealed record RustDeskPeer(string Id, string? Alias, string? Hostname, string? Platform, IReadOnlyList<string> Tags, string AddressBook);

public sealed class RustDeskException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Legge la rubrica condivisa di RustDesk Server Pro con un token API (Console → Settings → Tokens,
/// permesso "Address Book" in lettura). Usa gli stessi endpoint del client RustDesk:
/// <c>/api/ab/shared/profiles</c> e <c>/api/ab/peers</c>.
/// </summary>
public sealed class RustDeskClient(IHttpClientFactory httpFactory, SettingsService settings, ILogger<RustDeskClient> logger)
{
    public const string HttpClientName = "rustdesk";
    private const int PageSize = 100;

    public async Task<IReadOnlyList<RustDeskAddressBook>> GetAddressBooksAsync(CancellationToken ct)
    {
        var books = new List<RustDeskAddressBook>();
        await foreach (var item in GetPagedAsync("api/ab/shared/profiles", null, ct))
        {
            var guid = HuduClient.GetString(item, "guid");
            var name = HuduClient.GetString(item, "name");
            if (!string.IsNullOrWhiteSpace(guid))
            {
                books.Add(new RustDeskAddressBook(guid, name ?? guid));
            }
        }

        return books;
    }

    /// <summary>Dispositivi delle rubriche condivise (tutte, o solo quella indicata nelle impostazioni).</summary>
    public async Task<IReadOnlyList<RustDeskPeer>> GetPeersAsync(CancellationToken ct)
    {
        var wanted = settings.RustDesk.AddressBook?.Trim();
        var books = await GetAddressBooksAsync(ct);
        if (!string.IsNullOrEmpty(wanted))
        {
            books = books.Where(b => string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (books.Count == 0)
            {
                throw new RustDeskException($"Rubrica condivisa \"{wanted}\" non trovata.");
            }
        }

        var peers = new Dictionary<string, RustDeskPeer>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            await foreach (var item in GetPagedAsync("api/ab/peers", "ab=" + Uri.EscapeDataString(book.Guid), ct))
            {
                var id = HuduClient.GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || peers.ContainsKey(id))
                {
                    continue;
                }

                var tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : [];
                peers[id] = new RustDeskPeer(
                    id,
                    HuduClient.GetString(item, "alias"),
                    HuduClient.GetString(item, "hostname"),
                    HuduClient.GetString(item, "platform"),
                    tags,
                    book.Name);
            }
        }

        return [.. peers.Values];
    }

    private async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string path, string? query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var total = int.MaxValue;
        for (var current = 1; (current - 1) * PageSize < total && current <= 1000; current++)
        {
            var relative = $"{path}?current={current}&pageSize={PageSize}" + (query is null ? "" : "&" + query);
            using var doc = await SendAsync(relative, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new RustDeskException("RustDesk: " + HuduClient.ValueToText(error));
            }

            total = HuduClient.GetInt(root, "total") ?? 0;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                yield break;
            }

            foreach (var item in data.EnumerateArray())
            {
                yield return item.Clone();
            }
        }
    }

    private async Task<JsonDocument> SendAsync(string relative, CancellationToken ct)
    {
        var rd = settings.RustDesk;
        if (!rd.IsApiConfigured)
        {
            throw new RustDeskException("API di RustDesk non configurata (indirizzo e token nella sezione admin).");
        }

        if (!Uri.TryCreate(rd.ApiUrl!.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new RustDeskException("Indirizzo API di RustDesk non valido.");
        }

        var client = httpFactory.CreateClient(HttpClientName);
        var uri = new Uri(baseUri, relative);

        // I token API della console (Settings → Tokens) funzionano con GET, come negli script ufficiali
        // (ab.py). Il client RustDesk usa POST con il token dell'utente: si prova come ripiego.
        string? firstRejection = null;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rd.ApiToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (Exception ex) when (HuduClient.IsNetworkError(ex, ct))
            {
                throw new RustDeskException($"Server RustDesk non raggiungibile ({uri.Host}:{uri.Port}): {HuduClient.NetworkErrorText(ex)}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await ReadErrorAsync(response, ct);
                    var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? $"RustDesk ha rifiutato il token ({(int)response.StatusCode}{detail}). Serve un token della console (Settings → Tokens) con permesso Address Book."
                        : $"RustDesk ha risposto {(int)response.StatusCode}{detail} per {relative.Split('?')[0]}.";
                    if (method == HttpMethod.Get)
                    {
                        logger.LogInformation("RustDesk {Path} con GET: {Message}; provo con POST", relative.Split('?')[0], message);
                        firstRejection = message;
                        continue;
                    }

                    throw new RustDeskException(firstRejection ?? message);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                try
                {
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Risposta RustDesk non JSON per {Path}", relative);
                    throw new RustDeskException("Risposta di RustDesk non valida: l'indirizzo è quello dell'API (porta 21114 o dominio della console)?", ex);
                }
            }
        }

        throw new RustDeskException(firstRejection ?? "RustDesk non accetta la richiesta su " + relative.Split('?')[0]);
    }

    /// <summary>Testo d'errore restituito da RustDesk (es. {"error":"..."}), accorciato.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (body.Length == 0)
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                body = HuduClient.ValueToText(error) ?? body;
            }
        }
        catch (JsonException)
        {
            // non JSON: si mostra il testo
        }

        return ": " + (body.Length > 160 ? body[..160] + "…" : body);
    }
}
