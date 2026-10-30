using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

public enum SessionState
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>Contenuto di una scheda: la sessione vera e propria, con i suoi comandi.</summary>
public interface ISessionView : IAsyncDisposable
{
    UIElement View { get; }

    SessionState State { get; }

    /// <summary>Stato in breve, mostrato nella barra e nel suggerimento della scheda.</summary>
    string StatusText { get; }

    /// <summary>Gruppi di comandi per la barra della scheda.</summary>
    IReadOnlyList<CommandGroup> Commands { get; }

    /// <summary>Lo stato o i comandi disponibili sono cambiati.</summary>
    event Action? StateChanged;

    Task StartAsync();
}

/// <summary>
/// Scheda di una connessione. L'intestazione è breve (icona, nome dell'asset, pallino di stato e
/// chiusura); cliente, indirizzo e percorso sono nel suggerimento e nella barra dei comandi.
/// </summary>
public sealed class SessionTab : TabItem, IAsyncDisposable
{
    private static readonly Brush ConnectingBrush = Frozen(0xF5, 0xA6, 0x23);
    private static readonly Brush ConnectedBrush = Frozen(0x2E, 0xA0, 0x43);
    private static readonly Brush DisconnectedBrush = Frozen(0x8B, 0x94, 0x9E);
    private static readonly Brush FailedBrush = Frozen(0xE5, 0x53, 0x4B);

    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly Ellipse _dot;
    private readonly CommandBar _bar = new();
    private readonly Grid _body = new();
    private readonly Border _warning;
    private ConnectionInfo? _info;
    private Tunnel? _tunnel;
    private ISessionView? _session;
    private bool _failed;

    public SessionTab()
    {
        Style = (Style)Application.Current.FindResource("SessionTabItem");
        _icon = new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "\uE895", FontSize = 13, Margin = new Thickness(0, 0, 7, 0) };
        _title = new TextBlock { Text = "Connessione…", VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        _dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Stroke = Brushes.White, StrokeThickness = 1 };
        var close = new Button { Style = (Style)Application.Current.FindResource("TabCloseButton"), Content = "\uE711" };
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_icon);
        header.Children.Add(_title);
        header.Children.Add(_dot);
        header.Children.Add(close);
        Header = header;

        // Clic con la rotellina sulla scheda: chiude, come nei browser.
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseRequested?.Invoke();
            }
        };

        Progress = new ConnectProgress();
        Progress.Failed += () =>
        {
            _failed = true;
            UpdateState();
        };
        _warning = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xA6, 0x23)),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _body.Children.Add(Progress);
        _body.Children.Add(_warning);

        var content = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Top);
        content.Children.Add(_bar);
        content.Children.Add(_body);
        // Il contenuto usa il colore del tema, non quello dell'intestazione (bianco sulla scheda attiva).
        content.SetBinding(TextElement.ForegroundProperty, new System.Windows.Data.Binding(nameof(Window.Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });
        Content = content;
        UpdateState();
    }

    public event Action? CloseRequested;

    public ConnectProgress Progress { get; }

    public CancellationToken Cancellation => _cts.Token;

    public SessionState State => _failed ? SessionState.Failed : _session?.State ?? SessionState.Connecting;

    /// <summary>Nome dell'asset (o "Connessione…" finché non si sa).</summary>
    public string DisplayName => _title.Text;

    public void SetConnection(ConnectionInfo info)
    {
        _info = info;
        _title.Text = info.Name;
        _icon.Text = info.Protocol switch
        {
            ConnectionProtocol.Rdp => "\uE7F4",
            ConnectionProtocol.Ssh or ConnectionProtocol.Telnet => "\uE756",
            _ => "\uE774",
        };
        UpdateState();
    }

    public void AttachTunnel(Tunnel tunnel) => _tunnel = tunnel;

    public void ShowSession(ISessionView session)
    {
        _session = session;
        if (session is RdpSessionView rdp)
        {
            rdp.Ended += () => CloseRequested?.Invoke();
        }

        session.StateChanged += UpdateState;
        _body.Children.Remove(Progress);
        _body.Children.Insert(0, session.View);

        var commands = session.Commands.ToList();
        if (_info?.HuduUrl is { } hudu && Uri.TryCreate(hudu, UriKind.Absolute, out var huduUri) && huduUri.Scheme == Uri.UriSchemeHttps)
        {
            commands.Add(new CommandGroup("Hudu",
            [
                new SessionCommand("\uE8A7", "Apri in Hudu", "Apre la scheda dell'asset in Hudu nel browser",
                    () => Process.Start(new ProcessStartInfo(huduUri.ToString()) { UseShellExecute = true })?.Dispose()),
            ]));
        }

        _bar.SetCommands(commands);
        UpdateState();
    }

    public void ShowWarning(string message)
    {
        _warning.Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        _warning.Visibility = Visibility.Visible;
    }

    private void UpdateState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateState);
            return;
        }

        var state = State;
        var status = _failed ? "Connessione non riuscita" : _session?.StatusText ?? "Connessione in corso…";
        _dot.Fill = state switch
        {
            SessionState.Connected => ConnectedBrush,
            SessionState.Disconnected => DisconnectedBrush,
            SessionState.Failed => FailedBrush,
            _ => ConnectingBrush,
        };

        if (_info is { } info)
        {
            var target = $"{info.Label} {info.Target.Host}:{info.Target.Port} · {info.Route}";
            ToolTip = $"{info.Name} — {info.Company}\n{target}\n{status}";
            _bar.SetInfo($"{info.Name} · {info.Company}", $"{target} · {status}");
        }
        else
        {
            ToolTip = status;
            _bar.SetInfo("Apertura della connessione", status);
        }

        _bar.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_session is not null)
        {
            _session.StateChanged -= UpdateState;
            await _session.DisposeAsync();
        }

        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
        }

        _cts.Dispose();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public enum ConnectStep
{
    SignIn,
    Redeem,
    Tunnel,
    Session,
}

/// <summary>Passaggi della connessione, mostrati mentre si apre la sessione.</summary>
public sealed class ConnectProgress : Border
{
    private readonly (ConnectStep Step, string Text, TextBlock Mark, TextBlock Label)[] _steps;
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;

    public ConnectProgress()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 380, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "Apertura della connessione", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });

        _steps = new[]
        {
            (ConnectStep.SignIn, "Accesso con Microsoft 365"),
            (ConnectStep.Redeem, "Richiesta al portale"),
            (ConnectStep.Tunnel, "Tunnel RustDesk"),
            (ConnectStep.Session, "Connessione"),
        }.Select(s =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var mark = new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "", Width = 24, Opacity = 0.4 };
            var label = new TextBlock { Text = s.Item2, Opacity = 0.6 };
            row.Children.Add(mark);
            row.Children.Add(label);
            panel.Children.Add(row);
            return (s.Item1, s.Item2, mark, label);
        }).ToArray();

        _bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 16, 0, 8) };
        _detail = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);
        Child = panel;
    }

    public void Step(ConnectStep step, string? detail = null) => Dispatcher.Invoke(() =>
    {
        foreach (var (s, text, mark, label) in _steps)
        {
            if (s < step)
            {
                mark.Text = "";
                mark.Opacity = 0.9;
                label.Opacity = 0.9;
            }
            else if (s == step)
            {
                mark.Text = "";
                mark.Opacity = 1;
                label.Opacity = 1;
                label.FontWeight = FontWeights.SemiBold;
                label.Text = detail is null ? text : $"{text}: {detail}";
            }
        }

        _detail.Text = "";
    });

    public void Detail(string text) => Dispatcher.Invoke(() => _detail.Text = text);

    /// <summary>La connessione non è riuscita (dopo <see cref="Fail"/>).</summary>
    public event Action? Failed;

    public void Fail(string message) => Dispatcher.Invoke(() =>
    {
        _bar.Visibility = Visibility.Collapsed;
        foreach (var (_, _, mark, label) in _steps)
        {
            if (label.FontWeight == FontWeights.SemiBold && mark.Text == "")
            {
                mark.Text = "";
            }
        }

        _detail.Text = message + "\n\nIl biglietto vale una sola volta: per riprovare, rilancia la connessione dal portale.";
        _detail.Opacity = 1;
        Failed?.Invoke();
    });
}
