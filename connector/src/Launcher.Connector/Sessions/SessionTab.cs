using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>Contenuto di una scheda: la sessione vera e propria.</summary>
public interface ISessionView : IAsyncDisposable
{
    UIElement View { get; }

    Task StartAsync();
}

/// <summary>Scheda di una connessione: intestazione, avanzamento e poi la sessione.</summary>
public sealed class SessionTab : TabItem, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Grid _body = new();
    private readonly Border _warning;
    private Tunnel? _tunnel;
    private ISessionView? _session;

    public SessionTab()
    {
        _icon = new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "", Margin = new Thickness(0, 0, 8, 0) };
        _title = new TextBlock { Text = "Connessione…", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock { Opacity = 0.65, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Style = (Style)Application.Current.FindResource("TabCloseButton"), Content = "" };
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_icon);
        header.Children.Add(_title);
        header.Children.Add(_subtitle);
        header.Children.Add(close);
        Header = header;

        Progress = new ConnectProgress();
        _warning = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xA6, 0x23)),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _body.Children.Add(Progress);
        _body.Children.Add(_warning);
        // Nel tema Fluent la scheda selezionata ha il testo scuro: il contenuto usa il colore del tema.
        _body.SetBinding(TextElement.ForegroundProperty, new System.Windows.Data.Binding(nameof(Window.Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });
        Content = _body;
    }

    public event Action? CloseRequested;

    public ConnectProgress Progress { get; }

    public CancellationToken Cancellation => _cts.Token;

    public void SetConnection(ConnectionInfo info)
    {
        _title.Text = info.Name;
        _subtitle.Text = info.Company;
        _icon.Text = info.Protocol switch
        {
            ConnectionProtocol.Rdp => "",
            ConnectionProtocol.Ssh or ConnectionProtocol.Telnet => "",
            _ => "",
        };
        ToolTip = $"{info.Name} — {info.Company}\n{info.Label} {info.Target.Host}:{info.Target.Port} {info.Route}";
    }

    public void AttachTunnel(Tunnel tunnel) => _tunnel = tunnel;

    public void ShowSession(ISessionView session)
    {
        _session = session;
        if (session is RdpSessionView rdp)
        {
            rdp.Ended += () => CloseRequested?.Invoke();
        }

        _body.Children.Remove(Progress);
        _body.Children.Insert(0, session.View);
    }

    public void ShowWarning(string message)
    {
        _warning.Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        _warning.Visibility = Visibility.Visible;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
        }

        _cts.Dispose();
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
    });
}
