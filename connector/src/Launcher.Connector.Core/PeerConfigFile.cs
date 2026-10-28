using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Connector.Core;

public sealed record TunnelRule(int LocalPort, string RemoteHost, int RemotePort);

/// <summary>
/// Modifica le regole di tunnel nel file di un dispositivo RustDesk
/// (<c>%APPDATA%\&lt;client&gt;\config\peers\&lt;ID&gt;.toml</c>, chiave <c>port_forwards</c>).
/// All'apertura di <c>--port-forward &lt;ID&gt;</c> RustDesk avvia da solo le regole salvate.
/// </summary>
public static partial class PeerConfigFile
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Sostituisce le regole mantenendo invariato il resto del file.</summary>
    public static string SetPortForwards(string toml, IReadOnlyCollection<TunnelRule> rules)
    {
        foreach (var rule in rules)
        {
            Validate(rule);
        }

        var line = "port_forwards = [" + string.Join(", ", rules.Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"[{r.LocalPort}, \"{r.RemoteHost}\", {r.RemotePort}]"))) + "]";

        var existing = PortForwardsPattern().Match(toml);
        if (existing.Success)
        {
            return string.Concat(toml.AsSpan(0, existing.Index), line, toml.AsSpan(existing.Index + existing.Length));
        }

        // Le chiavi di primo livello devono stare prima della prima tabella TOML.
        var newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var firstTable = FirstTablePattern().Match(toml);
        return firstTable.Success
            ? toml.Insert(firstTable.Index, line + newline)
            : (toml.Length == 0 ? "" : toml.TrimEnd() + newline) + line + newline;
    }

    public static void Validate(TunnelRule rule)
    {
        if (rule.LocalPort is <= 0 or > 65535 || rule.RemotePort is <= 0 or > 65535)
        {
            throw new ArgumentException("Porta non valida.", nameof(rule));
        }

        if (!HostPattern().IsMatch(rule.RemoteHost))
        {
            throw new ArgumentException($"Host non valido: {rule.RemoteHost}", nameof(rule));
        }
    }

    // port_forwards = [...], anche su più righe e con un livello di liste annidate.
    [GeneratedRegex(@"(?ms)^port_forwards\s*=\s*\[(?:[^\[\]]|\[[^\[\]]*\])*\]")]
    private static partial Regex PortForwardsPattern();

    [GeneratedRegex(@"(?m)^\[")]
    private static partial Regex FirstTablePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.\-:]{1,253}$")]
    private static partial Regex HostPattern();
}
