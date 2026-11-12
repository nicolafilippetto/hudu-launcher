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

    /// <summary>Campo del codice OTP (secondo passaggio dell'accesso).</summary>
    Otp,

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
        (function (host, user, pass, fill, otp) {
            var here = location.hostname.toLowerCase().replace(/^\[|\]$/g, "").replace(/\.$/, "");
            if (here !== host) { return "host"; }
            function visible(e) {
                var r = e.getBoundingClientRect(), s = getComputedStyle(e);
                return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none" && !e.disabled && !e.readOnly;
            }
            var inputs = Array.prototype.slice.call(document.querySelectorAll("input")).filter(visible);
            // Campo del codice OTP: dichiarato come tale, o con un nome da codice; anche a caselle da una cifra.
            var otpFields = inputs.filter(function (e) {
                if (["", "text", "tel", "number", "password"].indexOf(e.type) < 0) { return false; }
                if (e.autocomplete === "one-time-code") { return true; }
                return /otp|totp|2fa|mfa|one.?time|token|verif|passcode|security.?code|auth.?code|codice/i.test([e.name, e.id, e.placeholder, e.getAttribute("aria-label")].join(" "))
                    && (e.maxLength <= 0 || e.maxLength <= 10);
            });
            var passwords = inputs.filter(function (e) { return e.type === "password" && otpFields.indexOf(e) < 0; });
            var texts = inputs.filter(function (e) { return ["", "text", "email", "tel"].indexOf(e.type) >= 0 && otpFields.indexOf(e) < 0; });
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
            if (!fill) { return passwords.length > 0 ? "both" : userField ? "user" : otpFields.length > 0 ? "otp" : "none"; }
            function set(e, v) {
                var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
                e.focus();
                setter.call(e, v);
                e.dispatchEvent(new Event("input", { bubbles: true }));
                e.dispatchEvent(new Event("change", { bubbles: true }));
            }
            var otpDone = false;
            if (otp && otpFields.length > 0) {
                var boxes = otpFields.filter(function (e) { return e.maxLength === 1; });
                if (boxes.length >= otp.length) {
                    for (var i = 0; i < otp.length; i++) { set(boxes[i], otp.charAt(i)); }
                } else {
                    set(otpFields[0], otp);
                }
                otpDone = true;
            }
            if (userField && user) { set(userField, user); }
            if (passwords.length > 0) { set(passwords[0], pass); passwords[0].focus(); return "both"; }
            if (otpDone) { return "otp"; }
            return userField && user ? "user" : "none";
        })
        """;

    /// <summary>Script che compila utente e password (stringhe JSON, mai codice).</summary>
    public static string Fill(string host, string? user, string? password, string? otp = null) =>
        $"{Body}({Json(Normalize(host))}, {Json(user ?? "")}, {Json(password ?? "")}, true, {Json(otp ?? "")})";

    /// <summary>Script che controlla soltanto se nella pagina c'è un modulo di accesso.</summary>
    public static string Detect(string host) => $"{Body}({Json(Normalize(host))}, \"\", \"\", false, \"\")";

    /// <summary>Risultato di ExecuteScriptAsync (stringa JSON) → esito.</summary>
    public static WebLoginFill Parse(string? result) => result switch
    {
        "\"host\"" => WebLoginFill.OtherHost,
        "\"both\"" => WebLoginFill.Filled,
        "\"user\"" => WebLoginFill.UserOnly,
        "\"otp\"" => WebLoginFill.Otp,
        _ => WebLoginFill.NoForm,
    };

    private static string Normalize(string host) => host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    private static string Json(string value) => JsonSerializer.Serialize(value);
}
