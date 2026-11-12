using System.Collections.Concurrent;

namespace Launcher.Server.Audit;

/// <summary>
/// Le richieste respinte dai limiti non devono scrivere una riga ciascuna nel registro (sarebbe un modo per
/// gonfiare il database senza autenticazione): per ogni indirizzo e percorso si scrive la prima della
/// finestra, con il numero delle respinte nella finestra precedente.
/// </summary>
public sealed class AuditThrottle(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxKeys = 10_000;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Since, int Suppressed)> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// true se va scritta una riga per questa chiave (prima della finestra); <paramref name="suppressed"/> è il
    /// numero di respinte non scritte dalla riga precedente.
    /// </summary>
    public bool ShouldWrite(string key, out int suppressed)
    {
        var now = time.GetUtcNow();
        if (_entries.Count > MaxKeys)
        {
            foreach (var old in _entries.Where(e => now - e.Value.Since > Window).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(old, out _);
            }
        }

        suppressed = 0;
        var write = false;
        var previous = 0;
        _entries.AddOrUpdate(key,
            _ =>
            {
                write = true;
                return (now, 0);
            },
            (_, current) =>
            {
                if (now - current.Since >= Window)
                {
                    write = true;
                    previous = current.Suppressed;
                    return (now, 0);
                }

                write = false;
                return (current.Since, current.Suppressed + 1);
            });
        suppressed = previous;
        return write;
    }
}
