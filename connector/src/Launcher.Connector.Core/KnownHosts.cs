using System.Text.Json;

namespace Launcher.Connector.Core;

public enum HostKeyStatus
{
    Unknown,
    Trusted,
    Changed,
}

/// <summary>
/// Impronte delle chiavi SSH già accettate, per nome stabile dell'asset (la porta locale del
/// tunnel cambia a ogni connessione, quindi non si può usare host:porta).
/// </summary>
public sealed class KnownHosts(string path)
{
    private readonly Lock _lock = new();

    public HostKeyStatus Check(string alias, string fingerprint)
    {
        lock (_lock)
        {
            var known = Load();
            if (!known.TryGetValue(alias, out var stored))
            {
                return HostKeyStatus.Unknown;
            }

            return string.Equals(stored, fingerprint, StringComparison.Ordinal) ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
        }
    }

    public void Trust(string alias, string fingerprint)
    {
        lock (_lock)
        {
            var known = Load();
            known[alias] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
