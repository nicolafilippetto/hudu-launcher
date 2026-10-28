using Launcher.Server.Audit;
using Launcher.Server.Connections;
using Launcher.Server.Integrations;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Launcher.Server.Pages.Admin;

public sealed class SettingsModel(SettingsService settings, AuditService audit, IServiceProvider services, IHttpClientFactory httpFactory, IHostEnvironment environment, TimeProvider time) : LauncherPageModel
{
    public EntraCertificateInfo? Certificate => EntraCredential.Describe(settings.Entra.Certificate);

    public EntraCertificateInfo? NextCertificate => settings.Entra.HasNextCertificate ? EntraCredential.Describe(settings.Entra.NextCertificate) : null;

    public bool CertificateExpiresSoon => Certificate?.ExpiresSoon(time) == true;

    public PortalSettings Portal => settings.Portal;
    public HuduSettings Hudu => settings.Hudu;
    public RustDeskSettings RustDesk => settings.RustDesk;
    public EntraSettings Entra => settings.Entra;
    public Dictionary<string, string> RdpDefaults => settings.RdpDefaults;
    public TerminalOptions Terminal => settings.Terminal;
    public string TerminalRulesText => settings.TerminalRulesText;

    public bool HasSecret(string key) => settings.HasValue(key);

    public string RedirectUri => $"{Request.Scheme}://{Request.Host}/signin-oidc";

    public string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostPortalAsync(string? name, bool allowLocalAdminConnections, string? localLoginNetworks,
        int sessionHours, int ticketSeconds, string? timeZone, int auditRetentionDays, int offlineMaxAssets, int offlineMinutes)
    {
        var networks = (localLoginNetworks ?? "").Trim();
        if (networks.Length > 0 && !NetworkAllowList.IsValid(networks))
        {
            return Error("Reti per l'accesso locale non valide: usare indirizzi o CIDR separati da virgola.", "portal");
        }

        var zone = string.IsNullOrWhiteSpace(timeZone) ? "Europe/Rome" : timeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            return Error($"Fuso orario sconosciuto: {zone}", "portal");
        }

        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.PortalName] = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 60)],
            [SettingKeys.AllowLocalAdminConnections] = allowLocalAdminConnections.ToString(),
            [SettingKeys.OfflineMaxAssets] = Math.Clamp(offlineMaxAssets, 0, 25).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.OfflineMinutes] = Math.Clamp(offlineMinutes, 15, 480).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.LocalLoginNetworks] = networks.Length == 0 ? "127.0.0.1/32" : networks,
            [SettingKeys.SessionHours] = Math.Clamp(sessionHours, 1, 24).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.TicketSeconds] = Math.Clamp(ticketSeconds, 10, 120).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.TimeZone] = zone,
            [SettingKeys.AuditRetentionDays] = Math.Clamp(auditRetentionDays, 30, 3650).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, "Portale");
        return RedirectToPage(null, null, "portal");
    }

    public async Task<IActionResult> OnPostHuduAsync(string? baseUrl, string? apiKey, int syncMinutes, int pageSize)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            try
            {
                HuduClient.BuildApiBase(baseUrl, environment.IsDevelopment());
            }
            catch (HuduException ex)
            {
                return Error(ex.Message, "hudu");
            }
        }

        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.HuduBaseUrl] = baseUrl?.Trim().TrimEnd('/'),
            [SettingKeys.HuduApiKey] = apiKey ?? "",
            [SettingKeys.HuduSyncMinutes] = Math.Clamp(syncMinutes, 5, 1440).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.HuduPageSize] = Math.Clamp(pageSize, 25, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, "Hudu");
        return RedirectToPage(null, null, "hudu");
    }

    public async Task<IActionResult> OnPostRustDeskAsync(string? apiUrl, string? apiToken, string? addressBook, string? connectionPassword, bool clearPassword,
        bool enabled, string? clientPath)
    {
        var path = clientPath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(path) && !RustDeskSettings.IsValidClientPath(path))
        {
            return Error("Percorso del client RustDesk non valido: serve un percorso completo di un .exe, es. C:\\Program Files\\RustDesk\\rustdesk.exe.", "rustdesk");
        }

        if (!string.IsNullOrWhiteSpace(apiUrl) && (!Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
        {
            return Error("Indirizzo API di RustDesk non valido (es. https://rustdesk.azienda.it).", "rustdesk");
        }

        var values = new Dictionary<string, string?>
        {
            [SettingKeys.RustDeskApiUrl] = apiUrl?.Trim().TrimEnd('/'),
            [SettingKeys.RustDeskApiToken] = apiToken ?? "",
            [SettingKeys.RustDeskAddressBook] = string.IsNullOrWhiteSpace(addressBook) ? null : addressBook.Trim(),
            [SettingKeys.RustDeskConnectionPassword] = clearPassword ? null : connectionPassword ?? "",
            [SettingKeys.RustDeskEnabled] = enabled.ToString(),
            [SettingKeys.RustDeskClientPath] = string.IsNullOrEmpty(path) ? null : path,
        };
        await SaveAsync(values, "RustDesk");
        return RedirectToPage(null, null, "rustdesk");
    }

    public async Task<IActionResult> OnPostRdpAsync([FromForm] Dictionary<string, string?> rdp)
    {
        // Si salvano solo le scelte diverse dai valori consigliati, così i consigli futuri valgono da soli.
        var values = RdpOptionCatalog.Clean(rdp)
            .Where(v => RdpOptionCatalog.All.First(d => d.Key == v.Key).Default != v.Value)
            .ToDictionary();
        await SaveAsync(new Dictionary<string, string?> { [SettingKeys.RdpDefaults] = RdpOptionCatalog.Serialize(values) }, "Desktop remoto");
        return RedirectToPage(null, null, "rdp");
    }

    public async Task<IActionResult> OnPostRdpResetAsync()
    {
        await SaveAsync(new Dictionary<string, string?> { [SettingKeys.RdpDefaults] = null }, "Desktop remoto");
        return RedirectToPage(null, null, "rdp");
    }

    public async Task<IActionResult> OnPostTerminalAsync(bool highlight, string? rules)
    {
        var text = rules?.Trim();
        var normalizedDefault = HighlightRules.Default.Replace("\r", "", StringComparison.Ordinal).Trim();
        if (!string.IsNullOrEmpty(text))
        {
            text = text.Replace("\r", "", StringComparison.Ordinal);
            if (text.Length > 20_000)
            {
                return Error("Regole troppo lunghe.", "terminal");
            }

            var (_, errors) = HighlightRules.Parse(text);
            if (errors.Count > 0)
            {
                return Error("Regole non salvate. " + string.Join(" ", errors.Take(5)), "terminal");
            }
        }

        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.TerminalHighlight] = highlight.ToString(),
            [SettingKeys.TerminalHighlightRules] = string.IsNullOrEmpty(text) || text == normalizedDefault ? null : text,
        }, "Terminale");
        return RedirectToPage(null, null, "terminal");
    }

    public async Task<IActionResult> OnPostEntraAsync(string? tenantId, string? clientId, string? clientSecret)
    {
        if (!string.IsNullOrWhiteSpace(tenantId) && !Guid.TryParse(tenantId, out _))
        {
            return Error("Il tenant ID deve essere un GUID (Entra → Panoramica → ID tenant).", "entra");
        }

        if (!string.IsNullOrWhiteSpace(clientId) && !Guid.TryParse(clientId, out _))
        {
            return Error("Il client ID deve essere un GUID (Registrazione app → ID applicazione).", "entra");
        }

        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EntraTenantId] = tenantId?.Trim(),
            [SettingKeys.EntraClientId] = clientId?.Trim(),
            [SettingKeys.EntraClientSecret] = clientSecret ?? "",
        }, "Microsoft 365");
        return RedirectToPage(null, null, "entra");
    }

    public async Task<IActionResult> OnPostTestHuduAsync(CancellationToken ct)
    {
        try
        {
            var layouts = await services.GetRequiredService<HuduClient>().TestAsync(ct);
            Flash = $"Hudu risponde: {layouts} layout letti nella prima pagina.";
        }
        catch (HuduException ex)
        {
            FlashError = ex.Message;
        }

        return RedirectToPage(null, null, "hudu");
    }

    public async Task<IActionResult> OnPostTestRustDeskAsync(CancellationToken ct)
    {
        try
        {
            var books = await services.GetRequiredService<RustDeskClient>().GetAddressBooksAsync(ct);
            Flash = books.Count == 0
                ? "RustDesk risponde, ma il token non vede nessuna rubrica condivisa."
                : "RustDesk risponde. Rubriche condivise: " + string.Join(", ", books.Select(b => b.Name));
        }
        catch (RustDeskException ex)
        {
            FlashError = ex.Message;
        }

        return RedirectToPage(null, null, "rustdesk");
    }

    /// <summary>Verifica tenant e credenziale attiva (certificato o segreto) con un token "client credentials".</summary>
    public async Task<IActionResult> OnPostTestEntraAsync(CancellationToken ct)
    {
        var entra = settings.Entra;
        if (!entra.IsConfigured)
        {
            FlashError = "Completa tenant ID, client ID e la credenziale (certificato o segreto).";
            return RedirectToPage(null, null, "entra");
        }

        var error = entra.UsesCertificate
            ? await TryCredentialAsync(entra, entra.Certificate!, entra.CertificateKey!, null, ct)
            : await TryCredentialAsync(entra, null, null, entra.ClientSecret, ct);
        if (error is null)
        {
            Flash = $"Microsoft 365 risponde: tenant, client ID e {(entra.UsesCertificate ? "certificato" : "segreto")} sono corretti.";
        }
        else
        {
            FlashError = error;
        }

        return RedirectToPage(null, null, "entra");
    }

    /// <summary>
    /// Nuovo certificato: si genera qui (la chiave privata non lascia il server), si carica il
    /// certificato pubblico in Entra e poi si prova e attiva. Fino ad allora resta valida la
    /// credenziale attuale, così il login non si interrompe.
    /// </summary>
    public async Task<IActionResult> OnPostGenerateCertificateAsync()
    {
        var (certificate, key) = EntraCredential.Generate($"Hudu Launcher ({Request.Host.Host})", time);
        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EntraCertificateNext] = certificate,
            [SettingKeys.EntraCertificateNextKey] = key,
        }, "Microsoft 365: nuovo certificato generato");
        Flash = "Certificato generato. Scaricalo, caricalo in Entra (Registrazione app → Certificati e segreti → Certificati) e poi premi \"Prova e attiva\".";
        return RedirectToPage(null, null, "entra");
    }

    /// <summary>Certificato pubblico (.cer) da caricare in Entra; la chiave privata non si scarica.</summary>
    public IActionResult OnGetCertificate(bool next)
    {
        var info = next ? NextCertificate : Certificate;
        return info is null ? NotFound() : File(info.RawData, "application/pkix-cert", next ? "hudu-launcher-entra-nuovo.cer" : "hudu-launcher-entra.cer");
    }

    public async Task<IActionResult> OnPostActivateCertificateAsync(CancellationToken ct)
    {
        var entra = settings.Entra;
        if (!entra.HasNextCertificate || !Guid.TryParse(entra.TenantId, out _) || !Guid.TryParse(entra.ClientId, out _))
        {
            FlashError = "Imposta tenant e client ID e genera prima un certificato.";
            return RedirectToPage(null, null, "entra");
        }

        var error = await TryCredentialAsync(entra, entra.NextCertificate!, entra.NextCertificateKey!, null, ct);
        if (error is not null)
        {
            FlashError = error + " Il certificato è stato caricato in Entra per questa app?";
            return RedirectToPage(null, null, "entra");
        }

        // Attivo: il segreto non serve più e si elimina anche dal portale.
        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EntraCertificate] = entra.NextCertificate,
            [SettingKeys.EntraCertificateKey] = entra.NextCertificateKey,
            [SettingKeys.EntraCertificateNext] = null,
            [SettingKeys.EntraCertificateNextKey] = null,
            [SettingKeys.EntraClientSecret] = null,
        }, "Microsoft 365: certificato attivato");
        Flash = "Certificato attivo: il portale accede a Entra senza segreto. Elimina il segreto client (e l'eventuale certificato precedente) dalla registrazione app in Entra.";
        return RedirectToPage(null, null, "entra");
    }

    public async Task<IActionResult> OnPostDiscardCertificateAsync()
    {
        await SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EntraCertificateNext] = null,
            [SettingKeys.EntraCertificateNextKey] = null,
        }, "Microsoft 365: nuovo certificato scartato");
        Flash = "Nuovo certificato scartato.";
        return RedirectToPage(null, null, "entra");
    }

    /// <summary>Chiede a Entra un token "client credentials": null se la credenziale funziona, altrimenti l'errore.</summary>
    private async Task<string?> TryCredentialAsync(EntraSettings entra, string? certificate, string? key, string? secret, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = entra.ClientId!,
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://graph.microsoft.com/.default",
        };
        if (certificate is not null && key is not null)
        {
            form["client_assertion_type"] = EntraCredential.AssertionType;
            form["client_assertion"] = EntraCredential.CreateAssertion(entra.TenantId!, entra.ClientId!, certificate, key, time);
        }
        else
        {
            form["client_secret"] = secret ?? "";
        }

        using var client = httpFactory.CreateClient();
        using var content = new FormUrlEncodedContent(form);
        try
        {
            using var response = await client.PostAsync(new Uri($"https://login.microsoftonline.com/{entra.TenantId}/oauth2/v2.0/token"), content, ct);
            return response.IsSuccessStatusCode ? null : "Entra ha rifiutato i dati: " + ExtractAadError(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (HuduClient.IsNetworkError(ex, ct))
        {
            return "Entra non raggiungibile: " + HuduClient.NetworkErrorText(ex);
        }
    }

    private static string ExtractAadError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return description?.Split('\n')[0].Split(" Trace ID")[0] ?? "errore sconosciuto";
        }
        catch (System.Text.Json.JsonException)
        {
            return "risposta non valida";
        }
    }

    private async Task SaveAsync(Dictionary<string, string?> values, string section)
    {
        await settings.SaveAsync(values, Email);
        await audit.WriteAsync(new AuditEntry("settings.update", Email, Details: section), HttpContext);
        Flash = $"Impostazioni \"{section}\" salvate.";
    }

    private RedirectToPageResult Error(string message, string fragment)
    {
        FlashError = message;
        return RedirectToPage(null, null, fragment);
    }
}
