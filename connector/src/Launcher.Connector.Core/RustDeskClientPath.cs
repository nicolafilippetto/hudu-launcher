namespace Launcher.Connector.Core;

/// <summary>
/// Percorso del client RustDesk indicato dal portale: si usa solo se è un .exe dentro le cartelle
/// dei programmi installati (Programmi). Così chi controllasse il portale non potrebbe far avviare
/// al Launcher un programma qualsiasi: in Programmi scrive solo un amministratore del PC.
/// </summary>
public static class RustDeskClientPath
{
    public static bool IsAllowed(string? path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260 || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // GetFullPath risolve ".." e "." : il confronto avviene sul percorso effettivo.
        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar)
            .Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }
}
