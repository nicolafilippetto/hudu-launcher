using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Server.Connections;

/// <summary>Un'opzione di connessione (Desktop remoto o pagine web): valori ammessi ed etichette per il portale.</summary>
public sealed record OptionDefinition(string Key, string Label, string Default, IReadOnlyList<(string Value, string Label)> Choices, string? Hint = null)
{
    public bool IsSwitch => Choices.Count == 2 && Choices[0].Value == "true";

    public string LabelOf(string value) => Choices.FirstOrDefault(c => c.Value == value).Label ?? value;
}

/// <summary>
/// Elenco di opzioni con predefiniti per tutti (sezione admin) e valori per singolo asset.
/// Si salvano solo le scelte, come JSON chiave → valore.
/// </summary>
public sealed class OptionCatalog(IReadOnlyList<OptionDefinition> all)
{
    private readonly Dictionary<string, OptionDefinition> _byKey = all.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public IReadOnlyList<OptionDefinition> All => all;

    /// <summary>Tiene solo chiavi e valori ammessi.</summary>
    public Dictionary<string, string> Clean(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (value is not null && _byKey.TryGetValue(key, out var definition) && definition.Choices.Any(c => c.Value == value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    public Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];
            return Clean(raw);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static string? Serialize(IReadOnlyDictionary<string, string> values) =>
        values.Count == 0 ? null : JsonSerializer.Serialize(values.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary());

    /// <summary>Scelte diverse dal valore consigliato (quelle da salvare come predefinite).</summary>
    public Dictionary<string, string> WithoutDefaults(IReadOnlyDictionary<string, string> values) =>
        values.Where(v => _byKey[v.Key].Default != v.Value).ToDictionary(StringComparer.Ordinal);

    /// <summary>Valore effettivo: override dell'asset, poi predefinito admin, poi catalogo.</summary>
    public string Effective(string key, IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides = null) =>
        overrides?.GetValueOrDefault(key) ?? defaults.GetValueOrDefault(key) ?? _byKey[key].Default;

    /// <summary>Riassunto delle opzioni scelte, per il registro.</summary>
    public string Describe(IReadOnlyDictionary<string, string> values) =>
        string.Join(", ", values.Select(v => $"{_byKey[v.Key].Label}: {_byKey[v.Key].LabelOf(v.Value)}"));
}

/// <summary>Opzioni di Desktop remoto inviate al Launcher (stessi nomi del JSON del Launcher).</summary>
public sealed record RdpOptions(
    bool AdminSession,
    bool Clipboard,
    bool Drives,
    bool Printers,
    bool SmartCards,
    bool Ports,
    bool Devices,
    bool WebAuthn,
    bool Microphone,
    string Audio,
    string Keyboard,
    int ColorDepth,
    string Display,
    bool Nla);

/// <summary>
/// Catalogo delle opzioni RDP. I predefiniti per tutti si impostano nella sezione admin, ogni asset
/// può sovrascriverne alcune. Senza configurazione: nessun reindirizzamento tranne gli appunti, tasti
/// di Windows al computer remoto, risoluzione adattata alla finestra.
/// </summary>
public static class RdpOptionCatalog
{
    private static readonly (string, string)[] YesNo = [("true", "Sì"), ("false", "No")];

    public static OptionCatalog Catalog { get; } = new(
    [
        new("adminSession", "Sessione amministrativa (/admin)", "false", YesNo, "Come mstsc /admin: utile sui server RDS"),
        new("clipboard", "Appunti", "true", YesNo),
        new("drives", "Unità locali", "false", YesNo),
        new("printers", "Stampanti", "false", YesNo),
        new("smartCards", "Smart card", "false", YesNo),
        new("ports", "Porte seriali (COM)", "false", YesNo),
        new("devices", "Altri dispositivi Plug and Play", "false", YesNo),
        new("webAuthn", "WebAuthn (Windows Hello, chiavi di sicurezza)", "false", YesNo, "Richiede Windows 11 su entrambi i lati"),
        new("microphone", "Microfono", "false", YesNo),
        new("audio", "Audio del computer remoto", "none",
            [("none", "Non riprodurre"), ("local", "Riproduci su questo PC"), ("remote", "Lascia sul computer remoto")]),
        new("keyboard", "Tasti di Windows (Alt+Tab, Win…)", "remote",
            [("remote", "Al computer remoto"), ("fullscreen", "Al remoto solo a schermo intero"), ("local", "A questo PC")]),
        new("colorDepth", "Colori", "32", [("32", "32 bit"), ("24", "24 bit"), ("16", "16 bit"), ("15", "15 bit")]),
        new("display", "Dimensione del desktop", "resize",
            [("resize", "Adatta alla finestra (segue il ridimensionamento)"), ("scale", "Fissa all'apertura, poi scalata")]),
        new("nla", "Autenticazione a livello di rete (NLA)", "true", YesNo, "Disattivare solo per server molto vecchi"),
    ]);

    public static IReadOnlyList<OptionDefinition> All => Catalog.All;

    public static Dictionary<string, string> Clean(IEnumerable<KeyValuePair<string, string?>> values) => Catalog.Clean(values);

    public static Dictionary<string, string> Parse(string? json) => Catalog.Parse(json);

    public static string? Serialize(IReadOnlyDictionary<string, string> values) => OptionCatalog.Serialize(values);

    public static string Effective(string key, IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides = null) =>
        Catalog.Effective(key, defaults, overrides);

    public static string Describe(IReadOnlyDictionary<string, string> values) => Catalog.Describe(values);

    public static RdpOptions Resolve(IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides)
    {
        string V(string key) => Effective(key, defaults, overrides);
        bool B(string key) => V(key) == "true";
        return new RdpOptions(
            B("adminSession"), B("clipboard"), B("drives"), B("printers"), B("smartCards"), B("ports"), B("devices"),
            B("webAuthn"), B("microphone"), V("audio"), V("keyboard"),
            int.Parse(V("colorDepth"), CultureInfo.InvariantCulture), V("display"), B("nla"));
    }
}

/// <summary>
/// Pagina web da aprire e opzioni inviate al Launcher. <see cref="Url"/> è l'indirizzo vero
/// dell'apparato: il Launcher lo apre nella sua scheda facendo passare il traffico nel tunnel, così
/// nome host, certificato e reindirizzamenti restano quelli dell'apparato.
/// </summary>
public sealed record WebOptions(
    string Url,
    bool External,
    string Certificate,
    bool Popups,
    bool Downloads,
    bool KeepCookies,
    int Zoom);

/// <summary>Opzioni delle pagine web (interfacce di firewall, iDRAC, vCenter…), come quelle RDP.</summary>
public static class WebOptionCatalog
{
    private static readonly (string, string)[] YesNo = [("true", "Sì"), ("false", "No")];

    public static OptionCatalog Catalog { get; } = new(
    [
        new("open", "Apri la pagina", "launcher",
            [("launcher", "In una scheda del Launcher"), ("browser", "Nel browser predefinito")],
            "Nel Launcher la pagina usa l'indirizzo vero dell'apparato (iDRAC, vCenter funzionano anche tramite jump host)"),
        new("certificate", "Certificati non validi", "ask",
            [("ask", "Chiedi e ricorda (come le chiavi SSH)"), ("ignore", "Ignora gli errori"), ("block", "Blocca la pagina")],
            "Gli apparati hanno spesso certificati autofirmati: con \"Chiedi\" il Launcher avvisa se il certificato cambia"),
        new("popups", "Nuove finestre (console HTML5)", "true", YesNo, "La console di iDRAC e iLO si apre in una nuova finestra"),
        new("downloads", "Download di file", "true", YesNo, "Log, backup di configurazione, report"),
        new("cookies", "Accesso al sito", "session",
            [("session", "Dimentica alla chiusura della scheda"), ("keep", "Ricorda per questo asset")],
            "Con \"Ricorda\" cookie e accesso restano sul PC del tecnico fino alla prossima apertura"),
        new("zoom", "Zoom iniziale", "100",
            [("80", "80%"), ("90", "90%"), ("100", "100%"), ("110", "110%"), ("125", "125%"), ("150", "150%")]),
    ]);

    public static IReadOnlyList<OptionDefinition> All => Catalog.All;

    public static WebOptions Resolve(string url, IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides)
    {
        string V(string key) => Catalog.Effective(key, defaults, overrides);
        return new WebOptions(url, V("open") == "browser", V("certificate"), V("popups") == "true", V("downloads") == "true",
            V("cookies") == "keep", int.Parse(V("zoom"), CultureInfo.InvariantCulture));
    }
}

/// <summary>Indirizzi delle pagine web scritti nel portale.</summary>
public static class WebUrls
{
    public const int MaxLength = 500;

    /// <summary>
    /// Accetta solo http/https assoluti verso un host valido, senza utente e password nell'indirizzo
    /// (le credenziali stanno in Hudu). Restituisce l'URL normalizzato o un errore.
    /// </summary>
    public static (Uri? Url, string? Error) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var text = value.Trim();
        if (text.Length > MaxLength)
        {
            return (null, "indirizzo troppo lungo");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return (null, "serve un indirizzo completo http:// o https://");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return (null, "niente utente o password nell'indirizzo: le credenziali stanno in Hudu");
        }

        if (!Inventory.HostParser.IsValidHost(uri.Host.Trim('[', ']')))
        {
            return (null, "nome host non valido");
        }

        return (uri, null);
    }

    /// <summary>Indirizzo predefinito di una connessione web senza URL: schema, host e porta di destinazione.</summary>
    public static string Default(bool https, string host, int port)
    {
        var urlHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var standard = https ? port == 443 : port == 80;
        return $"{(https ? "https" : "http")}://{urlHost}{(standard ? "" : ":" + port.ToString(CultureInfo.InvariantCulture))}/";
    }
}

/// <summary>Regola di evidenziazione del terminale: testo che corrisponde all'espressione, nel colore indicato.</summary>
public sealed record HighlightRule(string Pattern, string Color);

public sealed record TerminalOptions(bool Highlight, IReadOnlyList<HighlightRule> Rules);

/// <summary>
/// Regole di colore per SSH/Telnet, scritte dagli admin una per riga: <c>colore espressione</c>.
/// Le espressioni sono regolari (JavaScript, senza distinzione tra maiuscole e minuscole).
/// </summary>
public static partial class HighlightRules
{
    public static readonly IReadOnlyList<string> Colors = ["red", "green", "yellow", "blue", "magenta", "cyan", "orange", "gray"];

    public const string Default = """
        # colore  espressione regolare (maiuscole e minuscole indifferenti); colori: red green yellow blue magenta cyan orange gray o #rrggbb
        # prompt del dispositivo (Comware/HPE <SW>, [SW], Huawei [~SW], Cisco SW# SW(config)# SW>, Linux utente@host:~$): si vedono i comandi nuovi
        orange  ^\s*(?:<[\w.:/-]+>|\[[~*]?[\w.:/-]+\]|[\w.-]+@[\w.-]+:[^\s$#]*[$#]|[a-z][\w.:/-]*(?:\([\w-]+\))?[#>])
        red     \b(errors?|err|fail(ed|ure|ures)?|denied|refused|critical|fatal|down|disabled|blocked|invalid|timed? ?out|unreachable|offline|err-disabled)\b
        yellow  \b(warn(ing)?s?|alert|notice|deprecated|retry(ing)?|degraded|pending|standby)\b
        green   \b(ok|success(ful(ly)?)?|up|enabled|active|running|online|connected|established|passed|done|forwarding|master)\b
        cyan    \b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b
        # MAC: 00:1a:2b:3c:4d:5e, 00-1a-2b-3c-4d-5e, 001a.2b3c.4d5e (Cisco), 001a-2b3c-4d5e (Comware/HPE)
        magenta \b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b|\b(?:[0-9a-f]{4}-){2}[0-9a-f]{4}\b
        blue    \b(?:GigabitEthernet|TenGigabitEthernet|FortyGigE|Ten-GigabitEthernet|FastEthernet|Ethernet|Bridge-Aggregation|Port-channel|Vlan-interface|Vlan|XGE|GE|Gi|Te|Fa|Eth|Po|BAGG)\d+(?:[/.:]\d+)*\b
        """;

    public const int MaxRules = 50;

    /// <summary>Legge il testo; restituisce le regole valide e un errore per ogni riga scartata.</summary>
    public static (IReadOnlyList<HighlightRule> Rules, IReadOnlyList<string> Errors) Parse(string? text)
    {
        var rules = new List<HighlightRule>();
        var errors = new List<string>();
        var lines = (text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || (line.StartsWith('#') && !HexPrefix().IsMatch(line)))
            {
                continue;
            }

            var match = RuleLine().Match(line);
            if (!match.Success)
            {
                errors.Add($"Riga {i + 1}: serve \"colore espressione\".");
                continue;
            }

            var color = match.Groups[1].Value.ToLowerInvariant();
            var pattern = match.Groups[2].Value;
            if (!Colors.Contains(color) && !HexColor().IsMatch(color))
            {
                errors.Add($"Riga {i + 1}: colore \"{color}\" sconosciuto.");
                continue;
            }

            if (pattern.Length > 500)
            {
                errors.Add($"Riga {i + 1}: espressione troppo lunga.");
                continue;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Riga {i + 1}: espressione non valida ({ex.Message.Split('\n')[0]}).");
                continue;
            }

            rules.Add(new HighlightRule(pattern, color));
        }

        if (rules.Count > MaxRules)
        {
            errors.Add($"Al massimo {MaxRules} regole.");
            rules = rules.Take(MaxRules).ToList();
        }

        return (rules, errors);
    }

    [GeneratedRegex(@"^(#[0-9a-fA-F]{6}|[a-zA-Z]+)\s+(.+)$")]
    private static partial Regex RuleLine();

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\s")]
    private static partial Regex HexPrefix();
}
