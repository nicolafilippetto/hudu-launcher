namespace Launcher.Connector.Core;

/// <summary>
/// Cache d'emergenza: le connessioni dirette preparate dal portale, da usare senza internet.
/// Solo in memoria (mai su disco): si perde chiudendo il Launcher o spegnendo il PC. Un nuovo
/// pacchetto sostituisce il precedente; alla scadenza viene svuotata.
/// </summary>
public sealed class OfflineCache(TimeProvider time)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<ConnectionInfo> _items = [];
    private DateTimeOffset _expiresAt;

    /// <summary>Scatta quando il contenuto cambia (nuovo pacchetto, scadenza, svuotamento).</summary>
    public event Action? Changed;

    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _items.Count == 0 ? null : _expiresAt;
            }
        }
    }

    /// <summary>Connessioni ancora valide; se il tempo è scaduto la cache si svuota.</summary>
    public IReadOnlyList<ConnectionInfo> Items
    {
        get
        {
            ExpireIfNeeded();
            lock (_gate)
            {
                return _items;
            }
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                var left = _expiresAt - time.GetUtcNow();
                return _items.Count == 0 || left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }
    }

    public void Replace(IReadOnlyList<ConnectionInfo> connections, TimeSpan validity)
    {
        // Solo connessioni dirette: senza internet il jump host non è raggiungibile.
        var direct = connections.Where(c => c.IsDirect).ToList();
        lock (_gate)
        {
            _items = direct;
            _expiresAt = time.GetUtcNow() + validity;
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items = [];
        }

        Changed?.Invoke();
    }

    /// <summary>Da chiamare periodicamente: svuota la cache scaduta.</summary>
    public void ExpireIfNeeded()
    {
        bool expired;
        lock (_gate)
        {
            expired = _items.Count > 0 && time.GetUtcNow() >= _expiresAt;
        }

        if (expired)
        {
            Clear();
        }
    }
}
