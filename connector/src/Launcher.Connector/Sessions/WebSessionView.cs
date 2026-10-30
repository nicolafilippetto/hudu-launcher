using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Interfaccia web: si apre nel browser predefinito sulla porta locale del tunnel (o direttamente sul
/// dispositivo). La scheda resta aperta per tenere attivo il tunnel. Utente e password si prendono da Hudu.
/// </summary>
public sealed class WebSessionView : ISessionView
{
    private readonly ConnectionInfo _info;
    private readonly Uri _url;
    private readonly StackPanel _root;

    public WebSessionView(ConnectionInfo info, SessionEndpoint endpoint)
    {
        _info = info;
        var scheme = info.Protocol == ConnectionProtocol.Http ? "http" : "https";
        _url = new Uri($"{scheme}://{endpoint.UrlHost}:{endpoint.Port}/");

        _root = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560 };
        _root.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 });
        _root.Children.Add(new TextBlock { Text = $"{info.Label} di {info.Name}", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) });
        _root.Children.Add(new TextBlock
        {
            Text = endpoint.Direct
                ? $"Aperta nel browser all'indirizzo {_url} (connessione diretta).\n\nUtente e password: da Hudu."
                : $"Aperta nel browser all'indirizzo {_url}\n(tunnel verso {info.Target.Host}:{info.Target.Port} {info.Route}).\n\n" +
                  "Il browser avviserà che il certificato non corrisponde: è normale, perché ci si collega a 127.0.0.1.\n" +
                  "Il tunnel resta attivo finché questa scheda è aperta. Utente e password: da Hudu.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.75,
        });

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Button("Apri di nuovo", (_, _) => OpenBrowser()));
        _root.Children.Add(buttons);

        Commands =
        [
            new("Browser",
            [
                new SessionCommand("\uE774", "Apri di nuovo", "Riapre l'interfaccia web nel browser predefinito", OpenBrowser),
                new SessionCommand("\uE8C8", "Copia indirizzo", $"Copia {_url} negli appunti, per aprirlo in un altro browser", () => ClipboardHelper.Copy(_url.ToString())),
            ]),
        ];
    }

    public UIElement View => _root;

    public SessionState State => SessionState.Connected;

    public string StatusText => "Aperta nel browser";

    public IReadOnlyList<CommandGroup> Commands { get; }

    // Nessun cambio di stato: la scheda tiene solo aperto il tunnel.
    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync()
    {
        OpenBrowser();
        return Task.CompletedTask;
    }

    private void OpenBrowser()
    {
        Process.Start(new ProcessStartInfo(_url.ToString()) { UseShellExecute = true })?.Dispose();
        Log.Info($"Browser aperto su {_url} per {_info.Name}");
    }

    private static Button Button(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(4) };
        button.Click += onClick;
        return button;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
