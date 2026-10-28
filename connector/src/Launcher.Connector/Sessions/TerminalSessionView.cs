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
    private readonly WebView2 _web = new();
    private readonly Grid _root = new();
    private readonly ITerminalBackend _backend;
    private readonly TaskCompletionSource<(int Cols, int Rows)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    public TerminalSessionView(ConnectionInfo info, SessionEndpoint endpoint, TerminalKind kind, KnownHosts knownHosts)
    {
        _info = info;
        _backend = kind == TerminalKind.Ssh
            ? new SshBackend(info, endpoint, knownHosts)
            : new TelnetBackend(info, endpoint);
        _root.Children.Add(_web);
    }

    public UIElement View => _root;

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

        var (cols, rows) = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        PostJson(new { type = "theme", dark = IsDarkTheme() });
        if (_info.Terminal is { Highlight: true, Rules: { Count: > 0 } rules })
        {
            // Regole di colore scritte dagli admin nel portale: applicate da terminal.js.
            PostJson(new { type = "highlight", rules = rules.Select(r => new { pattern = r.Pattern, color = r.Color }) });
        }
        WriteLine($"Connessione {(_backend is SshBackend ? "SSH" : "Telnet")} a {_info.Name} ({_info.Target.Host}:{_info.Target.Port}), {_info.Route}…");

        _backend.DataReceived += data => _root.Dispatcher.BeginInvoke(() => PostJson(new { type = "data", data = Convert.ToBase64String(data) }));
        _backend.Closed += reason => _root.Dispatcher.BeginInvoke(() => WriteLine("\r\n\u001b[33m" + reason + "\u001b[0m"));

        try
        {
            await _backend.ConnectAsync(cols, rows, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            WriteLine("\u001b[33mConnessione annullata.\u001b[0m");
        }
        catch (InvalidOperationException ex)
        {
            WriteLine("\u001b[31m" + ex.Message + "\u001b[0m");
        }

        PostJson(new { type = "focus" });
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
                _backend.Send(root.GetProperty("data").GetString() ?? "");
                break;
            case "resize":
                _backend.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                break;
            case "copy":
                ClipboardHelper.Copy(root.GetProperty("text").GetString() ?? "");
                break;
            case "paste-request":
                if (Clipboard.ContainsText())
                {
                    PostJson(new { type = "paste", data = Clipboard.GetText() });
                }

                break;
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
        _backend.Dispose();
        _web.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
