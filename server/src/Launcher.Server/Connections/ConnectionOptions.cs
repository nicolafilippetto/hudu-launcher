using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Server.Connections;

/// <summary>Un'opzione di Desktop remoto: valori ammessi ed etichette per il portale.</summary>
public sealed record RdpOptionDefinition(string Key, string Label, string Default, IReadOnlyList<(string Value, string Label)> Choices, string? Hint = null)
{
    public bool IsSwitch => Choices.Count == 2 && Choices[0].Value == "true";

    public string LabelOf(string value) => Choices.FirstOrDefault(c => c.Value == value).Label ?? value;
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

    public static readonly IReadOnlyList<RdpOptionDefinition> All =
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
    ];

    private static readonly Dictionary<string, RdpOptionDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>Tiene solo chiavi e valori ammessi.</summary>
    public static Dictionary<string, string> Clean(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (value is not null && ByKey.TryGetValue(key, out var definition) && definition.Choices.Any(c => c.Value == value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static Dictionary<string, string> Parse(string? json)
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

    /// <summary>Valore effettivo per il portale: override dell'asset, poi predefinito admin, poi catalogo.</summary>
    public static string Effective(string key, IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides = null) =>
        overrides?.GetValueOrDefault(key) ?? defaults.GetValueOrDefault(key) ?? ByKey[key].Default;

    public static RdpOptions Resolve(IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string>? overrides)
    {
        string V(string key) => Effective(key, defaults, overrides);
        bool B(string key) => V(key) == "true";
        return new RdpOptions(
            B("adminSession"), B("clipboard"), B("drives"), B("printers"), B("smartCards"), B("ports"), B("devices"),
            B("webAuthn"), B("microphone"), V("audio"), V("keyboard"),
            int.Parse(V("colorDepth"), CultureInfo.InvariantCulture), V("display"), B("nla"));
    }

    /// <summary>Riassunto delle opzioni diverse dal predefinito, per il registro e per l'elenco.</summary>
    public static string Describe(IReadOnlyDictionary<string, string> values) =>
        string.Join(", ", values.Select(v => $"{ByKey[v.Key].Label}: {ByKey[v.Key].LabelOf(v.Value)}"));
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
