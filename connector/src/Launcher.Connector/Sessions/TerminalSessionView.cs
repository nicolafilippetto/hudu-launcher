using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Launcher.Connector.Sessions;

public enum TerminalKind
{
    Ssh,
    Telnet,
}

/// <summary>Terminale SSH/Telnet: xterm.js in WebView2, collegamento gestito dal Launcher.</summary>
public sealed class TerminalSessionView : ISessionView
{
    private const string VirtualHost = "launcher.terminal";

    private readonly ConnectionInfo _info;
    private readonly SessionEndpoint _endpoint;
    private readonly TerminalKind _kind;
    private readonly KnownHosts _knownHosts;
    private readonly WebView2 _web = new();
    private readonly Grid _root = new();
    private readonly TaskCompletionSource<(int Cols, int Rows)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private ITerminalBackend? _backend;
    private (int Cols, int Rows) _size = (80, 24);
    private bool _connecting;
    private SessionState _state = SessionState.Connecting;
    private string _statusText = "Connessione in corso…";

    public TerminalSessionView(ConnectionInfo info, SessionEndpoint endpoint, TerminalKind kind, KnownHosts knownHosts)
    {
        _info = info;
        _endpoint = endpoint;
        _kind = kind;
        _knownHosts = knownHosts;
        _root.Children.Add(_web);
        Commands = BuildCommands();
    }

    public UIElement View => _root;

    public SessionState State => _state;

    public string StatusText => _statusText;

    public IReadOnlyList<CommandGroup> Commands { get; }

    public event Action? StateChanged;

    private string Protocol => _kind == TerminalKind.Ssh ? "SSH" : "Telnet";

    private bool WebReady => _web.CoreWebView2 is not null;

    private IReadOnlyList<CommandGroup> BuildCommands() =>
    [
        new("Sessione",
        [
            new SessionCommand("\uE72C", "Riconnetti", "Chiude il collegamento e lo riapre nello stesso terminale", () => _ = ConnectAsync())
            {
                CanExecute = () => WebReady && !_connecting,
            },
            new SessionCommand("\uEA14", "Disconnetti", "Chiude il collegamento lasciando aperta la scheda con il testo ricevuto", Disconnect)
            {
                CanExecute = () => _state == SessionState.Connected,
            },
        ]),
        new("Appunti",
        [
            new SessionCommand("\uE8C8", "Copia", "Copia il testo selezionato (anche Ctrl+Maiusc+C; selezionando si copia già)", () => PostJson(new { type = "copy-selection" }))
            {
                CanExecute = () => WebReady,
            },
            new SessionCommand("\uE77F", "Incolla", "Incolla gli appunti nel terminale (anche tasto destro o Ctrl+Maiusc+V)", Paste)
            {
                CanExecute = () => _state == SessionState.Connected,
            },
            new SessionCommand("\uE8B3", "Seleziona tutto", "Seleziona tutto il testo del terminale", () => PostJson(new { type = "select-all" }))
            {
                CanExecute = () => WebReady,
            },
        ]),
        new("Terminale",
        [
            new SessionCommand("\uE75C", "Pulisci", "Svuota lo schermo e la cronologia del terminale (non invia nulla al server)", () => PostJson(new { type = "clear" }))
            {
                CanExecute = () => WebReady,
            },
            new SessionCommand("\uE74E", "Salva testo", "Salva in un file di testo tutto l'output del terminale", () => PostJson(new { type = "export" }))
            {
                CanExecute = () => WebReady,
            },
            new SessionCommand("\uE8E7", "Riduci", "Testo più piccolo (anche Ctrl+rotellina)", () => PostJson(new { type = "zoom", delta = -1 }))
            {
                CanExecute = () => WebReady,
            },
            new SessionCommand("\uE8E8", "Ingrandisci", "Testo più grande (anche Ctrl+rotellina)", () => PostJson(new { type = "zoom", delta = 1 }))
            {
                CanExecute = () => WebReady,
            },
        ]),
    ];

    public async Task StartAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ConnectorSettings.WebViewDataDirectory);
        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.Deny);
        // Solo la pagina del terminale: nessuna navigazione verso l'esterno.
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.Ordinal);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{VirtualHost}/index.html");

        _size = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        PostJson(new { type = "theme", dark = IsDarkTheme() });
        if (_info.Terminal is { Highlight: true, Rules: { Count: > 0 } rules })
        {
            // Regole di colore scritte dagli admin nel portale: applicate da terminal.js.
            PostJson(new { type = "highlight", rules = rules.Select(r => new { pattern = r.Pattern, color = r.Color }) });
        }

        await ConnectAsync();
    }

    /// <summary>Apre (o riapre) il collegamento: ogni volta un collegamento nuovo, nello stesso terminale.</summary>
    private async Task ConnectAsync()
    {
        if (_connecting || _cts.IsCancellationRequested)
        {
            return;
        }

        _connecting = true;
        if (_backend is not null)
        {
            var old = _backend;
            _backend = null;
            old.Dispose();
            WriteLine("\r\n\u001b[90m──────── riconnessione ────────\u001b[0m");
        }

        var backend = _kind == TerminalKind.Ssh
            ? (ITerminalBackend)new SshBackend(_info, _endpoint, _knownHosts)
            : new TelnetBackend(_info, _endpoint);
        _backend = backend;
        SetStatus("Connessione in corso…", SessionState.Connecting);
        WriteLine($"Connessione {Protocol} a {_info.Name} ({_info.Target.Host}:{_info.Target.Port}), {_info.Route}…");

        // Gli eventi di un collegamento sostituito non devono più arrivare al terminale.
        backend.DataReceived += data => _root.Dispatcher.BeginInvoke(() =>
        {
            if (_backend == backend)
            {
                PostJson(new { type = "data", data = Convert.ToBase64String(data) });
            }
        });
        backend.Closed += reason => _root.Dispatcher.BeginInvoke(() =>
        {
            if (_backend == backend)
            {
                WriteLine("\r\n\u001b[33m" + reason + "\u001b[0m");
                SetStatus(reason.TrimEnd('.'), SessionState.Disconnected);
            }
        });

        try
        {
            await backend.ConnectAsync(_size.Cols, _size.Rows, _cts.Token);
            if (_backend == backend)
            {
                SetStatus("Connesso", SessionState.Connected);
            }
        }
        catch (OperationCanceledException)
        {
            WriteLine("\u001b[33mConnessione annullata.\u001b[0m");
            SetStatus("Connessione annullata", SessionState.Disconnected);
        }
        catch (InvalidOperationException ex)
        {
            WriteLine("\u001b[31m" + ex.Message + "\u001b[0m");
            SetStatus(ex.Message.TrimEnd('.'), SessionState.Failed);
        }
        finally
        {
            _connecting = false;
            StateChanged?.Invoke();
        }

        PostJson(new { type = "focus" });
    }

    private void Disconnect()
    {
        if (_backend is not { } backend)
        {
            return;
        }

        _backend = null;
        backend.Dispose();
        WriteLine("\r\n\u001b[33mDisconnesso. Riconnetti per riaprire il collegamento.\u001b[0m");
        SetStatus("Disconnesso", SessionState.Disconnected);
    }

    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                PostJson(new { type = "paste", data = Clipboard.GetText() });
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn("Appunti non disponibili: " + ex.Message);
        }

        PostJson(new { type = "focus" });
    }

    private void SetStatus(string text, SessionState state)
    {
        _statusText = text;
        _state = state;
        StateChanged?.Invoke();
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ready.TrySetResult((root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32()));
                break;
            case "input":
                _backend?.Send(root.GetProperty("data").GetString() ?? "");
                break;
            case "resize":
                _size = (root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                _backend?.Resize(_size.Cols, _size.Rows);
                break;
            case "copy":
                ClipboardHelper.Copy(root.GetProperty("text").GetString() ?? "");
                break;
            case "paste-request":
                Paste();
                break;
            case "export":
                SaveText(root.GetProperty("text").GetString() ?? "");
                break;
        }
    }

    /// <summary>Salva l'output del terminale in un file di testo scelto dall'utente.</summary>
    private void SaveText(string text)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salva il testo del terminale",
            FileName = FileNames.For(_info.Name, "txt", DateTime.Now),
            Filter = "Testo|*.txt|Registro|*.log",
            DefaultExt = ".txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var owner = Window.GetWindow(_root);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Show(owner, "Testo non salvato", ex.Message, DialogKind.Error);
        }
    }

    private void WriteLine(string text) => PostJson(new { type = "text", data = text + "\r\n" });

    private void PostJson(object message) => _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _backend?.Dispose();
        _backend = null;
        _web.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
