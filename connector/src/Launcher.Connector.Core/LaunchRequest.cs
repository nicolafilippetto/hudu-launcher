using System.Text.RegularExpressions;

namespace Launcher.Connector.Core;

/// <summary>
/// Richiesta arrivata dal browser tramite il link
/// <c>hudulauncher://connect?ticket=…&amp;portal=https://…&amp;auth=entra|local</c>
/// (oppure <c>hudulauncher://offline?…</c> per preparare la cache d'emergenza).
/// Il link non deve contenere altro: host, porte e credenziali arrivano solo dal portale.
/// <see cref="Portal"/> serve solo a proporre l'indirizzo al primo avvio o a riconoscere un link
/// di un altro portale; <see cref="LocalAuth"/> indica un biglietto di admin locale (senza 365).
/// </summary>
public sealed partial record LaunchRequest(string Ticket, Uri? Portal = null, bool LocalAuth = false, bool Offline = false)
{
    public const string Scheme = "hudulauncher";

    public static LaunchRequest? FromArgs(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            var request = Parse(arg);
            if (request is not null)
            {
                return request;
            }
        }

        return null;
    }

    public static LaunchRequest? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 600
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(uri.Host, "connect", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "offline", StringComparison.OrdinalIgnoreCase))
            || uri.AbsolutePath.Trim('/').Length > 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                return null;
            }

            // Parametri sconosciuti o ripetuti: il link è stato manipolato.
            var key = Uri.UnescapeDataString(parts[0]);
            if (key is not ("ticket" or "portal" or "auth") || !values.TryAdd(key, Uri.UnescapeDataString(parts[1])))
            {
                return null;
            }
        }

        if (!values.TryGetValue("ticket", out var ticket) || !TicketPattern().IsMatch(ticket))
        {
            return null;
        }

        Uri? portal = null;
        if (values.TryGetValue("portal", out var portalText))
        {
            portal = PortalClient.TryNormalizePortalUrl(portalText);
            if (portal is null)
            {
                return null;
            }
        }

        var auth = values.GetValueOrDefault("auth", "entra");
        if (auth is not ("entra" or "local"))
        {
            return null;
        }

        return new LaunchRequest(ticket, portal, auth == "local", string.Equals(uri.Host, "offline", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ricostruisce il link (per passarlo all'istanza già aperta).</summary>
    public string ToLink() =>
        $"{Scheme}://{(Offline ? "offline" : "connect")}?ticket={Ticket}"
        + (Portal is null ? "" : "&portal=" + Uri.EscapeDataString(Portal.GetLeftPart(UriPartial.Authority)))
        + (LocalAuth ? "&auth=local" : "");

    [GeneratedRegex("^[A-Za-z0-9_-]{20,100}$")]
    private static partial Regex TicketPattern();
}
