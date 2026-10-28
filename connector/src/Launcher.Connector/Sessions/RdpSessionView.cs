using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Sessione RDP nella scheda: utente e password vengono passati al controllo da codice, senza
/// credenziali salvate in Windows e senza file .rdp. Il desktop riempie sempre la scheda: la
/// risoluzione segue la finestra (o, se il server non lo consente, l'immagine viene scalata).
/// Le opzioni (reindirizzamenti, /admin, tasti…) arrivano dal portale.
/// </summary>
public sealed class RdpSessionView : ISessionView
{
    private readonly ConnectionInfo _info;
    private readonly SessionEndpoint _endpoint;
    private readonly RustDeskTunnels _tunnels;
    private readonly DockPanel _root = new();
    private readonly Grid _area = new();
    private readonly WindowsFormsHost _formsHost = new();
    private readonly StackPanel _ended = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520, Visibility = Visibility.Collapsed };
    private readonly TextBlock _endedText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 16) };
    private bool _reconnectPending;
    private readonly RdpClientHost _rdp = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly RdpOptions _options;
    private bool _loggedIn;
    private bool _closing;
    private bool _scaled;
    private (int Width, int Height) _sessionSize;

    public RdpSessionView(ConnectionInfo info, SessionEndpoint endpoint, RustDeskTunnels tunnels)
    {
        _info = info;
        _endpoint = endpoint;
        _tunnels = tunnels;
        _options = info.Rdp ?? RdpOptions.Default;
        _scaled = _options.Display == "scale";

        var toolbar = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ToolButton("", "Schermo intero (Ctrl+Alt+Pausa per uscire)", (_, _) => SetFullScreen()));
        buttons.Children.Add(ToolButton("", "Riconnetti", (_, _) => Reconnect()));
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);
        toolbar.Children.Add(new TextBlock
        {
            Text = $"{info.Name} · {info.Target.Host}:{info.Target.Port} {info.Route}",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        toolbar.Children.Add(_status);
        DockPanel.SetDock(toolbar, Dock.Top);
        _root.Children.Add(toolbar);

        _formsHost.Child = _rdp;
        _ended.Children.Add(new TextBlock { Text = "Sessione non attiva", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _ended.Children.Add(_endedText);
        var again = new Button { Content = "Riconnetti", Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Center };
        again.Click += (_, _) => Reconnect();
        _ended.Children.Add(again);
        _area.Children.Add(_formsHost);
        _area.Children.Add(_ended);
        _root.Children.Add(_area);

        _rdp.Status += text => _status.Text = text;
        _rdp.LoginComplete += () =>
        {
            _loggedIn = true;
            _status.Text = "Connesso" + (_options.AdminSession ? " (sessione amministrativa)" : "");
            // La finestra può essere cambiata durante l'accesso: si allinea subito la risoluzione.
            UpdateResolution();
        };
        _rdp.Disconnected += OnDisconnected;
        _rdp.FatalError += code => _status.Text = $"Errore grave del controllo RDP ({code})";
        _rdp.LogonError += code =>
        {
            if (code is 0 or -1073741715)
            {
                _status.Text = "Credenziali rifiutate dal server: inseriscile a mano o correggi la credenziale nel portale.";
            }
        };
        _formsHost.SizeChanged += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            UpdateResolution();
        };
        _tunnels.Restarted += OnTunnelsRestarted;
    }

    public UIElement View => _root;

    /// <summary>La sessione è finita per scelta dell'utente (disconnessione o disconnetti/esci da Windows): la scheda si chiude.</summary>
    public event Action? Ended;

    public async Task StartAsync()
    {
        // La dimensione del desktop si decide quando la scheda è già disposta a schermo.
        await WaitForLayoutAsync();
        await _root.Dispatcher.InvokeAsync(() =>
        {
            // Il controllo ActiveX va configurato dopo la creazione dell'handle.
            _rdp.CreateControl();
            Configure();
            _status.Text = "Connessione in corso…";
            _rdp.Rdp.Connect();
        }, DispatcherPriority.Loaded);
    }

    private Task WaitForLayoutAsync()
    {
        if (_formsHost.ActualWidth > 0 && _formsHost.ActualHeight > 0)
        {
            return Task.CompletedTask;
        }

        var laidOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSize(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                _formsHost.SizeChanged -= OnSize;
                laidOut.TrySetResult();
            }
        }

        _formsHost.SizeChanged += OnSize;
        return laidOut.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void Configure()
    {
        var rdp = _rdp.Rdp;
        var (width, height, desktopScale, deviceScale) = DesktopSize();
        _sessionSize = (width, height);
        rdp.Server = _endpoint.Host;
        rdp.DesktopWidth = width;
        rdp.DesktopHeight = height;
        rdp.ColorDepth = _options.SafeColorDepth;
        TrySet(rdp, "ConnectingText", $"Connessione a {_info.Name}…");
        TrySet(rdp, "FullScreenTitle", $"{_info.Name} — {_info.Company}");

        var settings = rdp.AdvancedSettings9;
        settings.RDPPort = _endpoint.Port;
        settings.EnableCredSspSupport = _options.Nla;
        // Tramite tunnel il certificato non corrisponde a 127.0.0.1 e il canale è già cifrato end-to-end
        // da RustDesk: nessun avviso. In diretta si viaggia sulla rete del cliente: se il certificato
        // del server non è verificabile, Windows avvisa (come mstsc).
        settings.AuthenticationLevel = _endpoint.Direct ? 2 : 0;

        // Se una versione del controllo non supporta un'impostazione, si registra e si va avanti.
        object advanced = settings;
        TrySet(advanced, "SmartSizing", _scaled);
        TrySet(advanced, "EnableAutoReconnect", true);
        TrySet(advanced, "MaxReconnectAttempts", 20);
        TrySet(advanced, "BitmapPeristence", 1);
        TrySet(advanced, "Compress", 1);
        TrySet(advanced, "GrabFocusOnConnect", true);
        TrySet(advanced, "ConnectToAdministerServer", _options.AdminSession);

        // Reindirizzamenti: solo quelli scelti nel portale.
        TrySet(advanced, "RedirectClipboard", _options.Clipboard);
        TrySet(advanced, "RedirectDrives", _options.Drives);
        TrySet(advanced, "RedirectPrinters", _options.Printers);
        TrySet(advanced, "RedirectSmartCards", _options.SmartCards);
        TrySet(advanced, "RedirectPorts", _options.Ports);
        TrySet(advanced, "RedirectDevices", _options.Devices);
        TrySet(advanced, "RedirectPOSDevices", false);
        TrySet(advanced, "AudioCaptureRedirectionMode", _options.Microphone);

        if ((TryGet(rdp, "SecuredSettings3") ?? TryGet(rdp, "SecuredSettings2")) is { } secured)
        {
            TrySet(secured, "AudioRedirectionMode", _options.AudioMode);
            TrySet(secured, "KeyboardHookMode", _options.KeyboardMode);
        }

        // Proprietà estese: WebAuthn (nome usato anche da mRemoteNG) e scala per gli schermi ad alta densità.
        if (!_rdp.TrySetExtended("RedirectWebAuthn", _options.WebAuthn) && _options.WebAuthn)
        {
            Log.Warn("Il controllo RDP di questo Windows non accetta il reindirizzamento WebAuthn");
        }

        _rdp.TrySetExtended("DesktopScaleFactor", desktopScale);
        _rdp.TrySetExtended("DeviceScaleFactor", deviceScale);

        if (_info.Credential is { } credential)
        {
            var (domain, user) = SplitUser(credential.Username);
            if (user is not null)
            {
                rdp.UserName = user;
            }

            if (domain is not null)
            {
                rdp.Domain = domain;
            }

            if (!string.IsNullOrEmpty(credential.Password))
            {
                settings.ClearTextPassword = credential.Password;
            }
        }
    }

    private static object? TryGet(object target, string property)
    {
        try
        {
            return target.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, target, null, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMethodException or System.Reflection.TargetInvocationException)
        {
            Log.Warn($"Proprietà RDP {property} non disponibile: {ex.Message}");
            return null;
        }
    }

    private static void TrySet(object target, string property, object value)
    {
        try
        {
            target.GetType().InvokeMember(property, System.Reflection.BindingFlags.SetProperty, null, target, [value], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMethodException or System.Reflection.TargetInvocationException)
        {
            Log.Warn($"Impostazione RDP {property} non applicata: {ex.Message}");
        }
    }

    /// <summary>"DOMINIO\utente" → dominio e utente; "utente@dominio" resta intero.</summary>
    internal static (string? Domain, string? User) SplitUser(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return (null, null);
        }

        var slash = username.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? (username[..slash], username[(slash + 1)..]) : (null, username);
    }

    /// <summary>Area della scheda in pixel reali, con la scala di Windows da riportare nella sessione.</summary>
    private (int Width, int Height, uint DesktopScale, uint DeviceScale) DesktopSize()
    {
        var source = PresentationSource.FromVisual(_root);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var width = (int)Math.Clamp(_formsHost.ActualWidth * scale, 200, 8192);
        var height = (int)Math.Clamp(_formsHost.ActualHeight * scale, 200, 8192);
        var desktopScale = (uint)Math.Clamp(Math.Round(scale * 100), 100, 500);
        var deviceScale = scale >= 1.8 ? 180u : scale >= 1.4 ? 140u : 100u;
        return (width & ~1, height & ~1, desktopScale, deviceScale);
    }

    private void UpdateResolution()
    {
        if (!_loggedIn || _closing || _scaled)
        {
            return;
        }

        var (width, height, desktopScale, deviceScale) = DesktopSize();
        if ((width, height) == _sessionSize)
        {
            return;
        }

        try
        {
            _rdp.Rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, (uint)width, (uint)height, 0u, desktopScale, deviceScale);
            _sessionSize = (width, height);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Server vecchio (prima di 2012 R2) o criterio che lo vieta: l'immagine si scala nella scheda.
            Log.Warn("Ridimensionamento RDP non supportato dal server, passo alla scala: " + ex.Message);
            _scaled = true;
            TrySet(_rdp.Rdp.AdvancedSettings9, "SmartSizing", true);
        }
    }

    private void SetFullScreen()
    {
        if (_loggedIn)
        {
            _rdp.Rdp.FullScreen = true;
        }
    }

    private void Reconnect()
    {
        try
        {
            if ((int)_rdp.Rdp.Connected != 0)
            {
                // Si riconnette quando arriva l'evento di disconnessione.
                _reconnectPending = true;
                _rdp.Rdp.Disconnect();
                return;
            }

            ShowDesktop();
            Configure();
            _status.Text = "Connessione in corso…";
            _rdp.Rdp.Connect();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _status.Text = "Riconnessione non riuscita: " + ex.Message;
        }
    }

    private void ShowDesktop()
    {
        _ended.Visibility = Visibility.Collapsed;
        _formsHost.Visibility = Visibility.Visible;
    }

    private void ShowEnded(string message)
    {
        // Il controllo ActiveX resterebbe un rettangolo bianco: lo si nasconde e si mostra un pannello.
        _formsHost.Visibility = Visibility.Collapsed;
        _endedText.Text = message;
        _ended.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Fine della sessione voluta dall'utente: disconnessione locale (barra a schermo intero),
    /// disconnetti o esci dal menu Start del server (motivo 2) o uscita segnalata dal server.
    /// </summary>
    internal static bool EndedByUser(int reason, int extendedReason) =>
        reason is 1 or 2 || (reason == 3 && extendedReason is 2 or 11 or 12);

    private void OnTunnelsRestarted() => _root.Dispatcher.BeginInvoke(() =>
    {
        if (!_closing)
        {
            _status.Text = "Tunnel riaperto, riconnessione automatica…";
        }
    });

    private void OnDisconnected(int reason)
    {
        _loggedIn = false;
        if (_closing)
        {
            return;
        }

        string description;
        try
        {
            description = (string)_rdp.Rdp.GetErrorDescription((uint)reason, (uint)_rdp.Rdp.ExtendedDisconnectReason);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            description = $"codice {reason}";
        }

        int extended;
        try
        {
            extended = (int)_rdp.Rdp.ExtendedDisconnectReason;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            extended = 0;
        }

        Log.Info($"RDP {_info.Name} disconnesso: {reason}/{extended} {description}");
        if (_reconnectPending)
        {
            _reconnectPending = false;
            Reconnect();
            return;
        }

        if (EndedByUser(reason, extended))
        {
            _status.Text = "Sessione chiusa";
            Ended?.Invoke();
            return;
        }

        _status.Text = "Disconnesso";
        ShowEnded(reason == 3 ? "Il server ha chiuso la connessione (" + description + ")." : description);
    }

    private static Button ToolButton(string glyph, string tooltip, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = glyph },
            ToolTip = tooltip,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

    public ValueTask DisposeAsync()
    {
        _closing = true;
        _tunnels.Restarted -= OnTunnelsRestarted;
        _resizeTimer.Stop();
        try
        {
            if ((int)_rdp.Rdp.Connected != 0)
            {
                _rdp.Rdp.Disconnect();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn("Chiusura RDP: " + ex.Message);
        }

        _formsHost.Child = null;
        _rdp.Dispose();
        _formsHost.Dispose();
        return ValueTask.CompletedTask;
    }
}
