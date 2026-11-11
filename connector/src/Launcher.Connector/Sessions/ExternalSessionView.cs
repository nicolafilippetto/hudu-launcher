using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Connessioni aperte con un programma del tecnico: WinBox (il Launcher lo avvia), database Oracle
/// (voce HUDULAUNCHER_n nel tnsnames.ora) e "Altro" (indirizzo e porta da usare). La scheda mostra solo
/// le istruzioni e tiene aperto il tunnel; chiudendola si chiudono tunnel e voce del tnsnames.ora.
/// </summary>
public sealed class ExternalSessionView : ISessionView
{
    private readonly ConnectionInfo _info;
    private readonly SessionEndpoint _endpoint;
    private readonly OracleTnsNames _tns;
    private readonly StackPanel _root;
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly WrapPanel _buttons;
    private string? _alias;
    private string _status = "Pronta";

    public ExternalSessionView(ConnectionInfo info, SessionEndpoint endpoint, OracleTnsNames tns)
    {
        _info = info;
        _endpoint = endpoint;
        _tns = tns;
        var glyph = info.Protocol switch
        {
            ConnectionProtocol.Winbox => "",
            ConnectionProtocol.Oracle => "",
            _ => "",
        };
        _root = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 600 };
        _root.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = glyph, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 });
        _title = new TextBlock { Text = $"{info.Label} di {info.Name}", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.8, LineHeight = 20 };
        _buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        _root.Children.Add(_title);
        _root.Children.Add(_text);
        _root.Children.Add(_buttons);
        Commands = BuildCommands();
    }

    /// <summary>
    /// Prima del tunnel: per WinBox serve winbox.exe, per Oracle un tnsnames.ora modificabile. Se il tecnico
    /// annulla, la connessione si ferma qui con un messaggio chiaro.
    /// </summary>
    public static void Prepare(ConnectionInfo info, Window? owner)
    {
        if (info.Protocol == ConnectionProtocol.Winbox && Winbox.Locate(owner) is null)
        {
            throw new InvalidOperationException(
                "WinBox non indicato: scegli winbox.exe nelle impostazioni del Launcher (ingranaggio in basso a destra) e riprova.");
        }

        if (info.Protocol == ConnectionProtocol.Oracle && OracleTnsNames.WritableFile(owner) is null)
        {
            throw new InvalidOperationException(
                "Connessione Oracle annullata: serve un tnsnames.ora modificabile (impostazioni del Launcher, ingranaggio in basso a destra).");
        }
    }

    public UIElement View => _root;

    public SessionState State => SessionState.Connected;

    public string StatusText => _status;

    public IReadOnlyList<CommandGroup> Commands { get; private set; }

    public event Action? StateChanged;

    /// <summary>Indirizzo da usare nel programma del tecnico: porta locale del tunnel o dispositivo (diretta).</summary>
    private string Address => $"{_endpoint.UrlHost}:{_endpoint.Port}";

    private string Route => _endpoint.Direct
        ? "Connessione diretta: il PC raggiunge già il dispositivo."
        : $"Tunnel verso {_info.Target.Host}:{_info.Target.Port} {_info.Route}: resta attivo finché questa scheda è aperta.";

    public Task StartAsync()
    {
        switch (_info.Protocol)
        {
            case ConnectionProtocol.Winbox:
                StartWinbox();
                break;
            case ConnectionProtocol.Oracle:
                StartOracle();
                break;
            default:
                _status = "Tunnel aperto";
                _text.Text = $"Aperta la connessione verso {_info.Target.Host}:{_info.Target.Port}.\n\n" +
                             $"Apri il programma che ti serve e fallo puntare all'indirizzo {_endpoint.Host}, porta {_endpoint.Port}.\n\n{Route}";
                _buttons.Children.Add(Button("Copia indirizzo e porta", () => ClipboardHelper.Copy(Address)));
                break;
        }

        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void StartWinbox()
    {
        var owner = Window.GetWindow(_root) ?? Application.Current.MainWindow;
        var exe = Winbox.Locate(owner) ?? throw new InvalidOperationException(
            "WinBox non indicato: scegli winbox.exe nelle impostazioni del Launcher (ingranaggio in basso a destra) e riprova.");
        Winbox.Start(exe, _endpoint, _info.Credential);
        _status = "WinBox aperto";
        var user = _info.Credential?.Username is { } username ? $"Utente {username} (credenziale di Hudu)." : "Nessuna credenziale in Hudu: inseriscila in WinBox.";
        _text.Text = $"WinBox aperto verso {Address}. {user}\n\n{Route}\n\nSe chiudi WinBox puoi riaprirlo da qui.";
        _buttons.Children.Add(Button("Apri di nuovo WinBox", ReopenWinbox));
        _buttons.Children.Add(Button("Copia indirizzo", () => ClipboardHelper.Copy(Address)));
    }

    private void ReopenWinbox()
    {
        if (Winbox.Locate(Window.GetWindow(_root)) is { } exe)
        {
            Winbox.Start(exe, _endpoint, _info.Credential);
        }
    }

    private void StartOracle()
    {
        var database = _info.Database ?? throw new InvalidOperationException("Il portale non ha indicato il nome del database Oracle.");
        var owner = Window.GetWindow(_root) ?? Application.Current.MainWindow;
        var (alias, file) = _tns.Add(owner, _endpoint.Host, _endpoint.Port, database);
        _alias = alias;
        _status = $"Pronta: {alias}";
        _title.Text = $"Database {database.Name} di {_info.Name}";
        var user = database.Username is { } username
            ? $"Utente: {username}" + (database.CredentialName is { } name ? $" (credenziale \"{name}\" in Hudu, dove trovi anche la password)." : ".")
            : "Utente e password: da Hudu.";
        _text.Text = $"Apri il tuo programma per il database (SQL Developer, DBeaver, Toad…) e scegli la connessione {alias}.\n\n" +
                     $"{user}\n\n{Route}\n\nLa voce {alias} è in {file} e sparisce quando chiudi questa scheda.";
        _buttons.Children.Add(Button($"Copia \"{alias}\"", () => ClipboardHelper.Copy(alias)));
        _buttons.Children.Add(Button("Copia indirizzo e porta", () => ClipboardHelper.Copy(Address)));
        Commands = BuildCommands();
    }

    private IReadOnlyList<CommandGroup> BuildCommands()
    {
        var commands = new List<SessionCommand>
        {
            new("", "Copia indirizzo", $"Copia {Address} negli appunti", () => ClipboardHelper.Copy(Address)),
        };
        if (_info.Protocol == ConnectionProtocol.Winbox)
        {
            commands.Insert(0, new SessionCommand("", "Apri WinBox", "Riapre WinBox verso il dispositivo", ReopenWinbox));
        }

        if (_alias is { } alias)
        {
            commands.Insert(0, new SessionCommand("", "Copia nome", $"Copia {alias}, la voce da scegliere nel programma del database", () => ClipboardHelper.Copy(alias)));
        }

        return [new CommandGroup(_info.Protocol switch
        {
            ConnectionProtocol.Winbox => "WinBox",
            ConnectionProtocol.Oracle => "Oracle",
            _ => "Tunnel",
        }, commands)];
    }

    private static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    public ValueTask DisposeAsync()
    {
        if (_alias is { } alias)
        {
            _alias = null;
            _tns.Remove(alias);
        }

        return ValueTask.CompletedTask;
    }
}
