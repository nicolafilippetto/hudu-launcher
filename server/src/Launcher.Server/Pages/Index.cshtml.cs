using Launcher.Server.Audit;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages;

public sealed class ExtraForm
{
    public int? Id { get; set; }
    public string? Label { get; set; }
    public string? Protocol { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? PasswordId { get; set; }
    public string? Gateway { get; set; }
    public bool Remove { get; set; }
}

public sealed class AssetForm
{
    public int AssetId { get; set; }
    public string? Protocol { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? PasswordId { get; set; }
    public string? Gateway { get; set; }
    public bool Hidden { get; set; }
    public string? Note { get; set; }
    public List<ExtraForm> Extras { get; set; } = [];
    /// <summary>Opzioni RDP: chiave → valore; vuoto = predefinito.</summary>
    public Dictionary<string, string?> Rdp { get; set; } = [];
    public string? WebUrl { get; set; }
    public Dictionary<string, string?> Web { get; set; } = [];
}

/// <summary>Modifica in blocco: campo vuoto = invariato.</summary>
public sealed class BulkForm
{
    public string? AssetIds { get; set; }
    /// <summary>"" invariato, "auto" toglie il gateway impostato a mano, "set" usa <see cref="Gateway"/>.</summary>
    public string? GatewayMode { get; set; }
    public string? Gateway { get; set; }
    /// <summary>"" invariata, "auto" credenziale da Hudu, oppure l'ID della credenziale.</summary>
    public string? PasswordId { get; set; }
    /// <summary>"" invariato, "auto" dedotto, oppure il protocollo.</summary>
    public string? Protocol { get; set; }
    /// <summary>"" invariata, "hide" o "show".</summary>
    public string? Visibility { get; set; }
}

public sealed record BulkEditorViewModel(
    IReadOnlyList<ResolvedAsset> Assets,
    IReadOnlyList<CachedPassword>? Passwords,
    BulkForm Form,
    IReadOnlyList<string> Errors);

public sealed record EditorViewModel(
    ResolvedAsset Asset,
    IReadOnlyList<CachedPassword> Passwords,
    IReadOnlyList<ExtraConnection> Extras,
    ConnectionProtocol GuessedProtocol,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> RdpDefaults,
    IReadOnlyDictionary<string, string> RdpOverrides,
    IReadOnlyDictionary<string, string> WebDefaults,
    IReadOnlyDictionary<string, string> WebOverrides);

public sealed record HomeViewModel(IReadOnlyList<ResolvedAsset> Favorites, IReadOnlyList<ResolvedAsset> Recent, SyncState? Sync, int CompanyCount);

/// <summary>Risultati di ricerca; ScopeCompany valorizzato se la ricerca è limitata a un cliente.</summary>
public sealed record SearchViewModel(string Query, IReadOnlyList<ResolvedAsset> Results, CachedCompany? ScopeCompany = null);

public sealed class IndexModel(InventoryService inventory, AppDbContext db, AuditService audit, SettingsService settings, SyncService sync) : LauncherPageModel
{
    public IReadOnlyList<CompanySummary> Companies { get; private set; } = [];
    public CompanyView? Company { get; private set; }
    public SearchViewModel? Search { get; private set; }
    public HomeViewModel? Home { get; private set; }
    public int? CompanyId { get; private set; }
    public bool ShowHidden { get; private set; }

    public async Task OnGetAsync(int? c, string? q, bool hidden, CancellationToken ct)
    {
        Companies = await inventory.GetCompaniesAsync(ct);
        ShowHidden = hidden;
        CompanyId = c;
        var scope = c is { } id ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (!string.IsNullOrWhiteSpace(q))
        {
            Search = await BuildSearchAsync(q, scope, ct);
        }
        else if (c is { } companyId)
        {
            Company = await inventory.GetCompanyAsync(companyId, Email, hidden, ct);
        }
        else
        {
            Home = await BuildHomeAsync(ct);
        }

        ViewData["Query"] = q;
        if (scope is not null)
        {
            // Dentro un cliente la ricerca in alto resta nel cliente, salvo "Cerca ovunque".
            ViewData["ScopeCompanyId"] = scope.Id;
            ViewData["ScopeCompanyName"] = scope.Name;
        }
    }

    public async Task<IActionResult> OnGetSearchAsync(string? q, int? c, CancellationToken ct)
    {
        var scope = c is { } id ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (string.IsNullOrWhiteSpace(q))
        {
            if (scope is not null)
            {
                var company = await inventory.GetCompanyAsync(scope.Id, Email, false, ct);
                return Partial("_Company", company!);
            }

            return Partial("_Home", await BuildHomeAsync(ct));
        }

        return Partial("_Search", await BuildSearchAsync(q, scope, ct));
    }

    private async Task<SearchViewModel> BuildSearchAsync(string q, CachedCompany? scope, CancellationToken ct) =>
        new(q.Trim(), await inventory.SearchAsync(q, Email, scope?.Id, ct), scope);

    public async Task<IActionResult> OnGetEditorAsync(int assetId, CancellationToken ct)
    {
        var model = await BuildEditorAsync(assetId, [], ct);
        return model is null ? NotFound() : Partial("_AssetEditor", model);
    }

    /// <summary>Elenco dei dispositivi RustDesk per il campo "gateway" (caricato una volta per pagina).</summary>
    public async Task<IActionResult> OnGetPeersAsync(CancellationToken ct)
    {
        var peers = await inventory.GetPeersAsync(ct);
        return new JsonResult(peers.Select(p => new { id = p.Id, label = Format.PeerLabel(p), book = p.AddressBook }));
    }

    public async Task<IActionResult> OnPostSaveAssetAsync([FromForm] AssetForm form, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == form.AssetId, ct);
        if (asset is null)
        {
            return NotFound();
        }

        var companyPasswords = await db.Passwords.Where(p => p.CompanyId == asset.CompanyId).Select(p => p.Id).ToListAsync(ct);
        var errors = new List<string>();
        var protocol = ParseProtocol(form.Protocol, "Protocollo", errors);
        var host = ParseHost(form.Host, "Indirizzo", errors);
        var previousHost = await db.AssetSettings.AsNoTracking().Where(s => s.AssetId == asset.Id).Select(s => s.Host).FirstOrDefaultAsync(ct);
        if (host is not null && !asset.IpamList().Contains(host, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(host, previousHost, StringComparison.OrdinalIgnoreCase))
        {
            // Solo IP dell'IPAM: così gli indirizzi si correggono in Hudu, non nel portale.
            errors.Add($"L'indirizzo {host} non è nell'IPAM di Hudu per questo asset: caricalo in Hudu e premi \"Aggiorna da Hudu\".");
            host = null;
        }
        var port = ParsePort(form.Port, "Porta", errors);
        var passwordId = ParsePassword(form.PasswordId, companyPasswords, errors);
        var gateway = ParseGateway(form.Gateway, errors);
        var note = string.IsNullOrWhiteSpace(form.Note) ? null : form.Note.Trim();
        var rdp = RdpOptionCatalog.Clean(form.Rdp);
        var web = WebOptionCatalog.Catalog.Clean(form.Web);
        if (note?.Length > 500)
        {
            errors.Add("La nota supera i 500 caratteri.");
        }

        var (webUrl, webUrlError) = WebUrls.Parse(form.WebUrl);
        if (webUrlError is not null)
        {
            errors.Add($"URL della pagina web: {webUrlError}.");
        }
        else if (webUrl is not null && protocol is not (null or ConnectionProtocol.Http or ConnectionProtocol.Https))
        {
            errors.Add("L'URL della pagina web vale solo con il protocollo Web: lascia il protocollo su Automatico o scegli HTTP/HTTPS.");
            webUrl = null;
        }

        var extras = new List<(ExtraForm Form, ConnectionProtocol Protocol, string? Host, int? Port, string? Url, int? PasswordId, string? Gateway)>();
        foreach (var extra in form.Extras.Where(e => !e.Remove))
        {
            var label = extra.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                errors.Add("Ogni connessione aggiuntiva deve avere un nome.");
                continue;
            }

            if (label.Length > 40)
            {
                errors.Add($"Nome \"{label[..20]}…\" troppo lungo (max 40 caratteri).");
            }

            var extraProtocol = ParseProtocol(extra.Protocol, label, errors);
            if (extraProtocol is null)
            {
                errors.Add($"Scegliere il protocollo per \"{label}\".");
                continue;
            }

            // Nel campo host si può scrivere anche un indirizzo completo: vale per le pagine web.
            if (extra.Host?.Contains("://", StringComparison.Ordinal) == true)
            {
                var (extraUrl, extraUrlError) = WebUrls.Parse(extra.Host);
                if (extraUrlError is not null)
                {
                    errors.Add($"{label}: {extraUrlError}.");
                    continue;
                }

                if (extraProtocol is not (ConnectionProtocol.Http or ConnectionProtocol.Https))
                {
                    errors.Add($"{label}: un indirizzo completo (URL) vale solo per le connessioni web.");
                    continue;
                }

                extras.Add((extra, extraUrl!.Scheme == Uri.UriSchemeHttp ? ConnectionProtocol.Http : ConnectionProtocol.Https, null, null,
                    extraUrl.AbsoluteUri, ParsePassword(extra.PasswordId, companyPasswords, errors), ParseGateway(extra.Gateway, errors)));
                continue;
            }

            extras.Add((extra, extraProtocol.Value, ParseHost(extra.Host, label, errors), ParsePort(extra.Port, label, errors), null,
                ParsePassword(extra.PasswordId, companyPasswords, errors), ParseGateway(extra.Gateway, errors)));
        }

        if (extras.Count > 10)
        {
            errors.Add("Massimo 10 connessioni aggiuntive per asset.");
        }

        if (errors.Count > 0)
        {
            var model = await BuildEditorAsync(form.AssetId, errors, ct);
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Partial("_AssetEditor", model!);
        }

        var now = DateTimeOffset.UtcNow;
        var setting = await db.AssetSettings.FindAsync([asset.Id], ct);
        var isEmpty = protocol is null && host is null && port is null && passwordId is null && gateway is null && !form.Hidden && note is null
            && rdp.Count == 0 && webUrl is null && web.Count == 0;
        if (isEmpty)
        {
            if (setting is not null)
            {
                db.AssetSettings.Remove(setting);
            }
        }
        else
        {
            if (setting is null)
            {
                setting = new AssetSetting { AssetId = asset.Id };
                db.AssetSettings.Add(setting);
            }

            setting.Protocol = protocol;
            setting.Host = host;
            setting.Port = port;
            setting.PasswordId = passwordId;
            setting.GatewayPeerId = gateway;
            setting.Hidden = form.Hidden;
            setting.Note = note;
            setting.RdpOptions = RdpOptionCatalog.Serialize(rdp);
            setting.WebUrl = webUrl?.AbsoluteUri;
            setting.WebOptions = OptionCatalog.Serialize(web);
            setting.UpdatedAt = now;
            setting.UpdatedBy = Email;
        }

        var existing = await db.ExtraConnections.Where(e => e.AssetId == asset.Id).ToListAsync(ct);
        var kept = new HashSet<int>();
        foreach (var (extraForm, extraProtocol, extraHost, extraPort, extraUrl, extraPassword, extraGateway) in extras)
        {
            var row = extraForm.Id is { } id ? existing.FirstOrDefault(e => e.Id == id) : null;
            if (row is null)
            {
                row = new ExtraConnection { AssetId = asset.Id, Label = "" };
                db.ExtraConnections.Add(row);
            }
            else
            {
                kept.Add(row.Id);
            }

            row.Label = extraForm.Label!.Trim();
            row.Protocol = extraProtocol;
            row.Host = extraHost;
            row.Port = extraPort;
            row.Url = extraUrl;
            row.PasswordId = extraPassword;
            row.GatewayPeerId = extraGateway;
            row.UpdatedAt = now;
            row.UpdatedBy = Email;
        }

        db.ExtraConnections.RemoveRange(existing.Where(e => !kept.Contains(e.Id)));
        await db.SaveChangesAsync(ct);

        var company = await db.Companies.AsNoTracking().Where(c => c.Id == asset.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        await audit.WriteAsync(new AuditEntry("asset.update", Email, CompanyName: company, AssetId: asset.Id, AssetName: asset.Name,
            Details: Summarize(protocol, host, port, passwordId, gateway, form.Hidden, extras.Count)
                     + (rdp.Count > 0 ? "; RDP: " + RdpOptionCatalog.Describe(rdp) : "")
                     + (webUrl is null ? "" : "; URL: " + webUrl.AbsoluteUri)
                     + (web.Count > 0 ? "; Web: " + WebOptionCatalog.Catalog.Describe(web) : "")), HttpContext, ct);

        var resolved = await inventory.GetAssetAsync(asset.Id, Email, ct);
        ViewData["ShowCompany"] = Request.Query["context"] == "list";
        return Partial("_AssetRow", resolved!);
    }

    /// <summary>Asset modificabili insieme in una volta.</summary>
    public const int MaxBulkAssets = 200;

    public async Task<IActionResult> OnGetBulkEditorAsync(string? ids, CancellationToken ct)
    {
        var model = await BuildBulkEditorAsync(new BulkForm { AssetIds = ids }, [], ct);
        return model is null ? NotFound() : Partial("_BulkEditor", model);
    }

    /// <summary>Stessa modifica su più asset: gateway, credenziale, protocollo, visibilità.</summary>
    public async Task<IActionResult> OnPostBulkAsync([FromForm] BulkForm form, CancellationToken ct)
    {
        var ids = ParseIds(form.AssetIds);
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (assets.Count == 0)
        {
            return NotFound();
        }

        var errors = new List<string>();
        var changes = new List<string>();
        var gatewayMode = form.GatewayMode ?? "";
        string? gateway = null;
        if (gatewayMode == "set")
        {
            gateway = ParseGateway(form.Gateway, errors);
            if (gateway is null && errors.Count == 0)
            {
                errors.Add("Indicare il gateway RustDesk.");
            }

            changes.Add("gateway " + gateway);
        }
        else if (gatewayMode == "auto")
        {
            changes.Add("gateway automatico");
        }
        else if (gatewayMode.Length > 0)
        {
            errors.Add("Scelta del gateway non valida.");
        }

        var passwordMode = form.PasswordId ?? "";
        int? passwordId = null;
        if (passwordMode == "auto")
        {
            changes.Add("credenziale automatica");
        }
        else if (passwordMode.Length > 0)
        {
            var companies = assets.Select(a => a.CompanyId).Distinct().ToList();
            if (companies.Count != 1)
            {
                errors.Add("La credenziale si imposta in blocco solo per asset dello stesso cliente.");
            }
            else
            {
                var companyPasswords = await db.Passwords.Where(p => p.CompanyId == companies[0]).Select(p => p.Id).ToListAsync(ct);
                passwordId = ParsePassword(passwordMode, companyPasswords, errors);
                changes.Add("credenziale #" + passwordId);
            }
        }

        var protocolMode = form.Protocol ?? "";
        ConnectionProtocol? protocol = null;
        if (protocolMode == "auto")
        {
            changes.Add("protocollo automatico");
        }
        else if (protocolMode.Length > 0)
        {
            protocol = ParseProtocol(protocolMode, "Protocollo", errors);
            changes.Add("protocollo " + protocol);
        }

        var visibility = form.Visibility ?? "";
        if (visibility is "hide" or "show")
        {
            changes.Add(visibility == "hide" ? "nascosti" : "visibili");
        }
        else if (visibility.Length > 0)
        {
            errors.Add("Scelta di visibilità non valida.");
        }

        if (errors.Count == 0 && changes.Count == 0)
        {
            errors.Add("Nessuna modifica scelta.");
        }

        if (errors.Count > 0)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Partial("_BulkEditor", (await BuildBulkEditorAsync(form, errors, ct))!);
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.AssetSettings.Where(s => ids.Contains(s.AssetId)).ToDictionaryAsync(s => s.AssetId, ct);
        foreach (var asset in assets)
        {
            if (!existing.TryGetValue(asset.Id, out var setting))
            {
                setting = new AssetSetting { AssetId = asset.Id };
                db.AssetSettings.Add(setting);
            }

            if (gatewayMode.Length > 0)
            {
                setting.GatewayPeerId = gateway;
            }

            if (passwordMode.Length > 0)
            {
                setting.PasswordId = passwordId;
            }

            if (protocolMode.Length > 0)
            {
                setting.Protocol = protocol;
            }

            if (visibility.Length > 0)
            {
                setting.Hidden = visibility == "hide";
            }

            setting.UpdatedAt = now;
            setting.UpdatedBy = Email;
            if (setting is { Protocol: null, Host: null, Port: null, PasswordId: null, GatewayPeerId: null, Hidden: false, Note: null, RdpOptions: null, WebUrl: null, WebOptions: null })
            {
                db.AssetSettings.Remove(setting);
            }
        }

        await db.SaveChangesAsync(ct);

        var summary = string.Join(", ", changes);
        var companyNames = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        foreach (var asset in assets)
        {
            await audit.WriteAsync(new AuditEntry("asset.update", Email, CompanyName: companyNames.GetValueOrDefault(asset.CompanyId),
                AssetId: asset.Id, AssetName: asset.Name, Details: $"Modifica in blocco ({assets.Count} asset): {summary}"), HttpContext, ct);
        }

        return new JsonResult(new { count = assets.Count, message = $"{assets.Count} asset aggiornati: {summary}." });
    }

    private async Task<BulkEditorViewModel?> BuildBulkEditorAsync(BulkForm form, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var assets = await inventory.GetAssetsAsync(ParseIds(form.AssetIds), Email, ct);
        if (assets.Count == 0)
        {
            return null;
        }

        var companies = assets.Select(a => a.Asset.CompanyId).Distinct().ToList();
        var passwords = companies.Count == 1 ? await inventory.GetCompanyPasswordsAsync(companies[0], ct) : null;
        return new BulkEditorViewModel(assets, passwords, form, errors);
    }

    private static List<int> ParseIds(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(MaxBulkAssets)
            .ToList();

    /// <summary>Rilegge da Hudu solo questo cliente (al massimo una volta al minuto).</summary>
    public async Task<IActionResult> OnPostRefreshCompanyAsync(int companyId, CancellationToken ct)
    {
        var result = await sync.RefreshCompanyAsync(companyId, Email, ct);
        if (result.Success)
        {
            Flash = "Aggiornato da Hudu: " + result.Message;
        }
        else
        {
            FlashError = result.Message;
        }

        return RedirectToPage(new { c = companyId });
    }

    public async Task<IActionResult> OnPostCompanyGatewayAsync(int companyId, string? gateway, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null)
        {
            return NotFound();
        }

        var errors = new List<string>();
        var peerId = ParseGateway(gateway, errors);
        if (errors.Count > 0)
        {
            FlashError = errors[0];
            return RedirectToPage(new { c = companyId });
        }

        var setting = await db.CompanySettings.FindAsync([companyId], ct);
        if (peerId is null)
        {
            if (setting is not null)
            {
                db.CompanySettings.Remove(setting);
            }
        }
        else
        {
            if (setting is null)
            {
                setting = new CompanySetting { CompanyId = companyId };
                db.CompanySettings.Add(setting);
            }

            setting.GatewayPeerId = peerId;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            setting.UpdatedBy = Email;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("company.update", Email, CompanyName: company.Name, Details: "Gateway: " + (peerId ?? "nessuno")), HttpContext, ct);
        Flash = peerId is null ? "Gateway del cliente rimosso." : $"Gateway del cliente impostato su {peerId}.";
        return RedirectToPage(new { c = companyId });
    }

    private async Task<HomeViewModel> BuildHomeAsync(CancellationToken ct) => new(
        await inventory.GetFavoritesAsync(Email, ct),
        await inventory.GetRecentAsync(Email, 8, ct),
        await db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct),
        await db.Companies.CountAsync(ct));

    private async Task<EditorViewModel?> BuildEditorAsync(int assetId, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var resolved = await inventory.GetAssetAsync(assetId, Email, ct);
        if (resolved is null)
        {
            return null;
        }

        var passwords = await inventory.GetCompanyPasswordsAsync(resolved.Asset.CompanyId, ct);
        var extras = await db.ExtraConnections.AsNoTracking().Where(e => e.AssetId == assetId).OrderBy(e => e.Label).ToListAsync(ct);
        var layout = await db.LayoutConfigs.AsNoTracking().FirstOrDefaultAsync(l => l.LayoutId == resolved.Asset.LayoutId, ct);
        var guessed = ConnectionResolver.GuessProtocol(resolved.Asset.OperatingSystem, layout, null);
        return new EditorViewModel(resolved, passwords, extras, guessed, errors,
            settings.RdpDefaults, RdpOptionCatalog.Parse(resolved.Setting?.RdpOptions),
            settings.WebDefaults, WebOptionCatalog.Catalog.Parse(resolved.Setting?.WebOptions));
    }

    private static ConnectionProtocol? ParseProtocol(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<ConnectionProtocol>(value, ignoreCase: true, out var protocol) && Enum.IsDefined(protocol))
        {
            return protocol;
        }

        errors.Add($"{field}: protocollo non valido.");
        return null;
    }

    private static string? ParseHost(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var host = value.Trim();
        if (!HostParser.IsValidHost(host))
        {
            errors.Add($"{field}: \"{host}\" non è un IP o un nome host valido.");
            return null;
        }

        return host;
    }

    private static int? ParsePort(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value.Trim(), out var port) && port is > 0 and < 65536)
        {
            return port;
        }

        errors.Add($"{field}: porta non valida (1-65535).");
        return null;
    }

    private static int? ParsePassword(string? value, List<int> companyPasswords, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var id) && companyPasswords.Contains(id))
        {
            return id;
        }

        errors.Add("Credenziale non valida per questo cliente.");
        return null;
    }

    /// <summary>Accetta "123456789" o "123456789 — alias" (testo scelto dall'elenco).</summary>
    internal static string? ParseGateway(string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var id = value.Trim().Split([' ', '—', '-'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (id.Length is < 3 or > 32 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '.'))
        {
            errors.Add($"ID RustDesk non valido: \"{value.Trim()}\".");
            return null;
        }

        return id;
    }

    private static string Summarize(ConnectionProtocol? protocol, string? host, int? port, int? passwordId, string? gateway, bool hidden, int extras)
    {
        var parts = new List<string>();
        if (protocol is not null)
        {
            parts.Add("protocollo " + protocol);
        }

        if (host is not null)
        {
            parts.Add("host " + host);
        }

        if (port is not null)
        {
            parts.Add("porta " + port);
        }

        if (passwordId is not null)
        {
            parts.Add("credenziale #" + passwordId);
        }

        if (gateway is not null)
        {
            parts.Add("gateway " + gateway);
        }

        if (hidden)
        {
            parts.Add("nascosto");
        }

        parts.Add($"{extras} connessioni aggiuntive");
        return string.Join(", ", parts);
    }
}
