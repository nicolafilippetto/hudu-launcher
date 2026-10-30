using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Connector.Core;

/// <summary>Dove collegare una richiesta del browser integrato: un host e una porta da raggiungere davvero.</summary>
public sealed record ProxyRoute(string Host, int Port);

/// <summary>
/// Regole del proxy di una scheda web: l'indirizzo dell'asset (host e porta) passa dal tunnel
/// RustDesk (o va diretto, se la connessione è diretta); le altre porte dello stesso host sono
/// bloccate quando si passa dal tunnel, perché non sono raggiungibili; il resto va diretto.
/// </summary>
public static class WebRoutes
{
    public static Func<string, int, ProxyRoute?> For(TargetInfo target, SessionEndpoint endpoint) => (host, port) =>
    {
        if (!SameHost(host, target.Host))
        {
            return new ProxyRoute(host, port);
        }

        if (port == target.Port)
        {
            return new ProxyRoute(endpoint.Host, endpoint.Port);
        }

        return endpoint.Direct ? new ProxyRoute(host, port) : null;
    };

    /// <summary>Stesso host, senza distinguere maiuscole e scritture diverse dello stesso IP.</summary>
    public static bool SameHost(string a, string b)
    {
        a = a.Trim('[', ']');
        b = b.Trim('[', ']');
        return IPAddress.TryParse(a, out var ipA) && IPAddress.TryParse(b, out var ipB)
            ? ipA.Equals(ipB)
            : string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Proxy SOCKS5 minimale (solo CONNECT, senza autenticazione) in ascolto su 127.0.0.1, usato dal
/// browser integrato di una scheda. Il browser gli passa il nome host così com'è (risoluzione
/// remota): la pagina usa l'indirizzo vero dell'apparato e la cifratura resta tra browser e
/// apparato; il proxy decide solo dove instradare i byte.
/// </summary>
public sealed class Socks5Proxy : IAsyncDisposable
{
    private const byte Version = 5;
    private const byte CommandConnect = 1;
    private const byte AddressIPv4 = 1;
    private const byte AddressDomain = 3;
    private const byte AddressIPv6 = 4;

    private readonly Func<string, int, ProxyRoute?> _route;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;
    private Task? _accepting;

    public Socks5Proxy(Func<string, int, ProxyRoute?> route, Action<string>? log = null)
    {
        _route = route;
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _accepting = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, ct);
                if (request is null)
                {
                    return;
                }

                var (command, host, port) = request.Value;
                if (command != CommandConnect)
                {
                    await ReplyAsync(stream, 7, ct);
                    return;
                }

                if (_route(host, port) is not { } route)
                {
                    _log?.Invoke($"Proxy web: {host}:{port} bloccato (non raggiungibile tramite il tunnel)");
                    await ReplyAsync(stream, 2, ct);
                    return;
                }

                using var upstream = new TcpClient { NoDelay = true };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await upstream.ConnectAsync(route.Host, route.Port, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    _log?.Invoke($"Proxy web: {host}:{port} non raggiungibile ({ex.Message})");
                    await ReplyAsync(stream, ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? (byte)5 : (byte)4, ct);
                    return;
                }

                await ReplyAsync(stream, 0, ct);
                var remote = upstream.GetStream();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = PumpAsync(stream, remote, linked.Token);
                var down = PumpAsync(remote, stream, linked.Token);
                await Task.WhenAny(up, down);
                await linked.CancelAsync();
                // Si attende anche l'altra direzione, così le sue eccezioni non restano non osservate.
                await Task.WhenAll(up, down).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // connessione chiusa da una delle due parti
            }
        }
    }

    /// <summary>Saluto e richiesta SOCKS5; null se il client non parla SOCKS5 senza autenticazione.</summary>
    private static async Task<(byte Command, string Host, int Port)?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        if (header[0] != Version)
        {
            return null;
        }

        var methods = new byte[header[1]];
        await stream.ReadExactlyAsync(methods, ct);
        if (!methods.Contains((byte)0))
        {
            await stream.WriteAsync(new byte[] { Version, 0xFF }, ct);
            return null;
        }

        await stream.WriteAsync(new byte[] { Version, 0 }, ct);

        var request = new byte[4];
        await stream.ReadExactlyAsync(request, ct);
        if (request[0] != Version)
        {
            return null;
        }

        string host;
        switch (request[3])
        {
            case AddressIPv4:
                var v4 = new byte[4];
                await stream.ReadExactlyAsync(v4, ct);
                host = new IPAddress(v4).ToString();
                break;
            case AddressIPv6:
                var v6 = new byte[16];
                await stream.ReadExactlyAsync(v6, ct);
                host = new IPAddress(v6).ToString();
                break;
            case AddressDomain:
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, ct);
                var name = new byte[length[0]];
                await stream.ReadExactlyAsync(name, ct);
                host = Encoding.ASCII.GetString(name);
                break;
            default:
                await ReplyAsync(stream, 8, ct);
                return null;
        }

        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, ct);
        return (request[1], host, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    private static Task ReplyAsync(Stream stream, byte status, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { Version, status, 0, AddressIPv4, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        if (_accepting is not null)
        {
            await _accepting;
        }

        _listener.Dispose();
        _cts.Dispose();
    }
}
