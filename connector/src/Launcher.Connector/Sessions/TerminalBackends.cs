using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Launcher.Connector.Core.ConnectionInfo;

namespace Launcher.Connector.Sessions;

/// <summary>Collegamento testuale (SSH o Telnet) mostrato nel terminale della scheda.</summary>
public interface ITerminalBackend : IDisposable
{
    event Action<byte[]>? DataReceived;

    event Action<string>? Closed;

    Task ConnectAsync(int columns, int rows, CancellationToken ct);

    void Send(string text);

    void Resize(int columns, int rows);
}

/// <summary>SSH con SSH.NET. La chiave del server si verifica con <see cref="KnownHosts"/> (prima fiducia).</summary>
public sealed class SshBackend(ConnectionInfo info, SessionEndpoint endpoint, KnownHosts knownHosts) : ITerminalBackend
{
    private SshClient? _client;
    private ShellStream? _shell;

    public event Action<byte[]>? DataReceived;

    public event Action<string>? Closed;

    public async Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        var user = info.Credential?.Username;
        var password = info.Credential?.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            var typed = Application.Current.Dispatcher.Invoke(() => CredentialPrompt.Ask($"SSH — {info.Name}", user))
                ?? throw new OperationCanceledException();
            (user, password) = typed;
        }

        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = password;
                }
            }
        };
        var connection = new Renci.SshNet.ConnectionInfo(endpoint.Host, endpoint.Port, user,
            new PasswordAuthenticationMethod(user, password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        _client = new SshClient(connection);
        _client.HostKeyReceived += (_, e) => e.CanTrust = ConfirmHostKey(e.FingerPrintSHA256);
        _client.ErrorOccurred += (_, e) => Closed?.Invoke("Errore SSH: " + e.Exception.Message);

        try
        {
            await _client.ConnectAsync(ct);
        }
        catch (SshAuthenticationException)
        {
            throw new InvalidOperationException("Credenziali SSH rifiutate dal server.");
        }
        catch (SshConnectionException ex) when (ex.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable)
        {
            throw new InvalidOperationException("Connessione annullata: chiave del server non accettata.");
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException("Connessione SSH non riuscita: " + ex.Message);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"{endpoint.Host}:{endpoint.Port} non raggiungibile: " + ex.Message);
        }

        _shell = _client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 16 * 1024);
        _shell.DataReceived += (_, e) => DataReceived?.Invoke(e.Data);
        _shell.Closed += (_, _) => Closed?.Invoke("Sessione SSH terminata.");
    }

    private bool ConfirmHostKey(string fingerprint)
    {
        var alias = info.HostKeyAlias;
        var status = knownHosts.Check(alias, fingerprint);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        var accepted = Application.Current.Dispatcher.Invoke(() =>
        {
            var message = status == HostKeyStatus.Unknown
                ? $"Prima connessione SSH a {info.Name}.\n\nImpronta della chiave del server:\nSHA256:{fingerprint}\n\nAccettarla e ricordarla?"
                : $"ATTENZIONE: la chiave SSH di {info.Name} è CAMBIATA.\n\nNuova impronta:\nSHA256:{fingerprint}\n\n" +
                  "Può essere una reinstallazione del server, ma anche un tentativo di intercettazione. " +
                  "Continuare solo se il cambio è noto.\n\nAccettare la nuova chiave?";
            return MessageBox.Show(Application.Current.MainWindow!, message, "Chiave del server SSH",
                MessageBoxButton.YesNo, status == HostKeyStatus.Unknown ? MessageBoxImage.Question : MessageBoxImage.Warning,
                status == HostKeyStatus.Unknown ? MessageBoxResult.Yes : MessageBoxResult.No) == MessageBoxResult.Yes;
        });
        if (accepted)
        {
            knownHosts.Trust(alias, fingerprint);
            Log.Info($"Chiave SSH accettata per {alias}");
        }

        return accepted;
    }

    public void Send(string text)
    {
        if (_shell is { CanWrite: true })
        {
            _shell.Write(text);
            _shell.Flush();
        }
    }

    public void Resize(int columns, int rows) => _shell?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);

    public void Dispose()
    {
        _shell?.Dispose();
        if (_client?.IsConnected == true)
        {
            _client.Disconnect();
        }

        _client?.Dispose();
    }
}

/// <summary>Telnet minimale, con accesso automatico quando il server chiede utente e password.</summary>
public sealed partial class TelnetBackend(ConnectionInfo info, SessionEndpoint endpoint) : ITerminalBackend
{
    private readonly TelnetFilter _filter = new();
    private readonly StringBuilder _recent = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private bool _userSent;
    private bool _passwordSent;

    public event Action<byte[]>? DataReceived;

    public event Action<string>? Closed;

    public async Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        _client = new TcpClient { NoDelay = true };
        try
        {
            await _client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"{endpoint.Host}:{endpoint.Port} non raggiungibile: " + ex.Message);
        }

        _stream = _client.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await _stream!.ReadAsync(buffer, ct)) > 0)
            {
                var (data, reply) = _filter.Process(buffer.AsSpan(0, read));
                if (reply.Length > 0)
                {
                    await _stream.WriteAsync(reply, ct);
                }

                if (data.Length > 0)
                {
                    DataReceived?.Invoke(data);
                    AutoLogin(Encoding.UTF8.GetString(data));
                }
            }

            Closed?.Invoke("Connessione Telnet chiusa dal server.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            Closed?.Invoke("Connessione Telnet interrotta: " + ex.Message);
        }
    }

    private void AutoLogin(string text)
    {
        if (info.Credential is not { } credential || (_userSent && _passwordSent))
        {
            return;
        }

        _recent.Append(text);
        if (_recent.Length > 400)
        {
            _recent.Remove(0, _recent.Length - 400);
        }

        var tail = _recent.ToString();
        if (!_userSent && !string.IsNullOrEmpty(credential.Username) && LoginPrompt().IsMatch(tail))
        {
            _userSent = true;
            _recent.Clear();
            Send(credential.Username + "\r");
        }
        else if (!_passwordSent && !string.IsNullOrEmpty(credential.Password) && PasswordPrompt().IsMatch(tail))
        {
            _passwordSent = true;
            _recent.Clear();
            Send(credential.Password + "\r");
        }
    }

    public void Send(string text)
    {
        if (_stream is null)
        {
            return;
        }

        // Invio = CR LF in Telnet.
        var bytes = TelnetFilter.Escape(Encoding.UTF8.GetBytes(text.Replace("\r", "\r\n", StringComparison.Ordinal)));
        _stream.Write(bytes);
    }

    public void Resize(int columns, int rows)
    {
        // Nessuna negoziazione NAWS: la dimensione resta quella del server.
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _cts?.Dispose();
    }

    [GeneratedRegex(@"(login|username|user name|utente)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LoginPrompt();

    [GeneratedRegex(@"password\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();
}
