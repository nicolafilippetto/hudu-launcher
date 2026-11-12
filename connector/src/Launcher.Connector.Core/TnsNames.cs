using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Connector.Core;

/// <summary>Voce del tnsnames.ora per una connessione Oracle aperta dal Launcher.</summary>
public sealed record TnsEntry(string Alias, string Host, int Port, string DatabaseName, bool IsSid);

/// <summary>
/// Gestione del blocco del Launcher in fondo al tnsnames.ora del tecnico. Il resto del file non si
/// tocca: il blocco è delimitato da due righe di commento, si aggiunge subito dopo l'ultima riga e si
/// riscrive per intero (o si toglie) a ogni connessione aperta o chiusa. Togliendolo il file torna
/// identico, salvo l'a capo finale aggiunto se il file non ne aveva uno.
/// </summary>
public static partial class TnsNames
{
    public const string BeginMarker = "# >>> HuduLauncher: voci gestite dal Launcher, tolte alla chiusura (non modificare)";
    public const string EndMarker = "# <<< HuduLauncher";

    /// <summary>Inizio del blocco scritto dalle versioni 1.5.2–1.5.5, preceduto da una riga vuota aggiunta dal Launcher.</summary>
    private const string LegacyBeginMarker = "# >>> HuduLauncher: voci gestite dal Launcher, si aggiornano da sole (non modificare)";

    private const string MarkerPrefix = "# >>> HuduLauncher";

    /// <summary>Nome della voce: HUDULAUNCHER_1 per la prima connessione aperta, poi _2, _3…</summary>
    public static string Alias(int number) => $"HUDULAUNCHER_{number}";

    /// <summary>Contenuto del file con il blocco del Launcher sostituito (o tolto, se non ci sono voci).</summary>
    public static string Apply(string content, IReadOnlyCollection<TnsEntry> entries)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) || !content.Contains('\n', StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = RemoveBlock(content, newline);
        if (entries.Count == 0)
        {
            return text;
        }

        var block = new StringBuilder();
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            block.Append(newline);
        }

        block.Append(BeginMarker).Append(newline);
        foreach (var entry in entries.OrderBy(e => e.Alias, StringComparer.Ordinal))
        {
            block.Append(Render(entry, newline));
        }

        block.Append(EndMarker).Append(newline);
        return text + block;
    }

    /// <summary>Toglie esattamente le righe del blocco del Launcher; il resto del file resta com'è.</summary>
    public static string RemoveBlock(string content, string? newline = null)
    {
        var begin = content.IndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (begin < 0)
        {
            return content;
        }

        var end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        // Blocco senza fine (file troncato o modificato a mano): si toglie fino in fondo.
        var after = end < 0 ? content.Length : content.IndexOf('\n', end) is var lineEnd and >= 0 ? lineEnd + 1 : content.Length;
        var before = content[..begin];
        if (content.AsSpan(begin).StartsWith(LegacyBeginMarker, StringComparison.Ordinal))
        {
            // Versioni precedenti: anche la riga vuota che il Launcher aveva messo prima del blocco.
            before = before.EndsWith("\r\n\r\n", StringComparison.Ordinal) ? before[..^2]
                : before.EndsWith("\n\n", StringComparison.Ordinal) ? before[..^1]
                : before;
        }

        return before + content[after..];
    }

    /// <summary>Alias delle voci del Launcher presenti nel file.</summary>
    public static IReadOnlyList<string> ManagedAliases(string content)
    {
        var begin = content.IndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (begin < 0)
        {
            return [];
        }

        var end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        var block = end < 0 ? content[begin..] : content[begin..end];
        return [.. AliasPattern().Matches(block).Select(m => m.Groups[1].Value)];
    }

    public static string Render(TnsEntry entry, string newline = "\r\n")
    {
        if (!IsSafeName(entry.Alias) || !IsSafeName(entry.DatabaseName) || !IsSafeHost(entry.Host) || entry.Port is <= 0 or > 65535)
        {
            // Il portale li valida già: qui si evita comunque di scrivere sintassi TNS arbitraria nel file.
            throw new ArgumentException("Dati del database non validi per il tnsnames.ora.");
        }

        var connectData = entry.IsSid ? $"(SID = {entry.DatabaseName})" : $"(SERVICE_NAME = {entry.DatabaseName})";
        return $"{entry.Alias} ={newline}" +
               $"  (DESCRIPTION ={newline}" +
               $"    (ADDRESS = (PROTOCOL = TCP)(HOST = {entry.Host})(PORT = {entry.Port})){newline}" +
               $"    (CONNECT_DATA = {connectData}){newline}" +
               $"  ){newline}";
    }

    public static bool IsSafeName(string? value) => value is { Length: > 0 and <= 128 } && SafeName().IsMatch(value);

    private static bool IsSafeHost(string? value) => value is { Length: > 0 and <= 253 } && SafeHost().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9_.$#-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]+$")]
    private static partial Regex SafeHost();

    [GeneratedRegex(@"^(HUDULAUNCHER_\d+)\s*=", RegexOptions.Multiline)]
    private static partial Regex AliasPattern();
}

/// <summary>Argomenti di riga di comando di WinBox: indirizzo[:porta], utente, password.</summary>
public static class WinboxCommand
{
    public static IReadOnlyList<string> Arguments(SessionEndpoint endpoint, CredentialInfo? credential)
    {
        var address = endpoint.Port == 8291 ? endpoint.UrlHost : $"{endpoint.UrlHost}:{endpoint.Port}";
        var arguments = new List<string> { address };
        if (!string.IsNullOrEmpty(credential?.Username))
        {
            arguments.Add(credential.Username);
            if (!string.IsNullOrEmpty(credential.Password))
            {
                arguments.Add(credential.Password);
            }
        }

        return arguments;
    }
}
