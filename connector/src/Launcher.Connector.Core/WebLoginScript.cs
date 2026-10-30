using System.Text.Json;

namespace Launcher.Connector.Core;

/// <summary>Esito della compilazione nella pagina.</summary>
public enum WebLoginFill
{
    /// <summary>La pagina (o il riquadro) non è sull'host dell'asset: non si è scritto nulla.</summary>
    OtherHost,

    /// <summary>Nessun campo di accesso visibile.</summary>
    NoForm,

    /// <summary>Solo il campo utente (accesso in più passaggi: la password arriva nella schermata dopo).</summary>
    UserOnly,

    /// <summary>Password scritta (e utente, se c'era il campo).</summary>
    Filled,
}

/// <summary>
/// Script iniettati nella pagina dell'apparato per trovare il modulo di accesso e compilarlo come
/// farebbe il tecnico digitando. I valori passano codificati in JSON, mai concatenati come codice, e
/// lo script scrive solo se la pagina è davvero sull'host dell'asset.
/// </summary>
public static class WebLoginScript
{
    private const string Body = """
        (function (host, user, pass, fill) {
            var here = location.hostname.toLowerCase().replace(/^\[|\]$/g, "").replace(/\.$/, "");
            if (here !== host) { return "host"; }
            function visible(e) {
                var r = e.getBoundingClientRect(), s = getComputedStyle(e);
                return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none" && !e.disabled && !e.readOnly;
            }
            var inputs = Array.prototype.slice.call(document.querySelectorAll("input")).filter(visible);
            var passwords = inputs.filter(function (e) { return e.type === "password"; });
            var texts = inputs.filter(function (e) { return ["", "text", "email", "tel"].indexOf(e.type) >= 0; });
            var userField = null;
            if (passwords.length > 0) {
                texts.forEach(function (t) {
                    if (t.compareDocumentPosition(passwords[0]) & Node.DOCUMENT_POSITION_FOLLOWING) { userField = t; }
                });
            } else {
                userField = texts.filter(function (t) {
                    return /user|login|name|mail|account|utente|uid/i.test([t.name, t.id, t.autocomplete, t.placeholder].join(" "));
                })[0] || null;
            }
            if (!fill) { return passwords.length > 0 ? "both" : userField ? "user" : "none"; }
            function set(e, v) {
                var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
                e.focus();
                setter.call(e, v);
                e.dispatchEvent(new Event("input", { bubbles: true }));
                e.dispatchEvent(new Event("change", { bubbles: true }));
            }
            if (userField && user) { set(userField, user); }
            if (passwords.length > 0) { set(passwords[0], pass); passwords[0].focus(); return "both"; }
            return userField && user ? "user" : "none";
        })
        """;

    /// <summary>Script che compila utente e password (stringhe JSON, mai codice).</summary>
    public static string Fill(string host, string? user, string? password) =>
        $"{Body}({Json(Normalize(host))}, {Json(user ?? "")}, {Json(password ?? "")}, true)";

    /// <summary>Script che controlla soltanto se nella pagina c'è un modulo di accesso.</summary>
    public static string Detect(string host) => $"{Body}({Json(Normalize(host))}, \"\", \"\", false)";

    /// <summary>Risultato di ExecuteScriptAsync (stringa JSON) → esito.</summary>
    public static WebLoginFill Parse(string? result) => result switch
    {
        "\"host\"" => WebLoginFill.OtherHost,
        "\"both\"" => WebLoginFill.Filled,
        "\"user\"" => WebLoginFill.UserOnly,
        _ => WebLoginFill.NoForm,
    };

    private static string Normalize(string host) => host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    private static string Json(string value) => JsonSerializer.Serialize(value);
}
