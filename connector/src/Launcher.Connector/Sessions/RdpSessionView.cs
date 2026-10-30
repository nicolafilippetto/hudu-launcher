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
    private readonly Grid _root = new();
    private readonly WindowsFormsHost _formsHost = new();
    private readonly StackPanel _ended = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520, Visibility = Visibility.Collapsed };
    private readonly TextBlock _endedTitle = new() { Text = "Sessione non attiva", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _endedText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 16) };
    private readonly RdpClientHost _rdp = new();
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly RdpOptions _options;
    private bool _reconnectPending;
    private bool _disconnectRequested;
    private bool _loggedIn;
    private bool _closing;
    private bool _smartSizing;
    private bool _resizeUnsupported;
    private bool _viewOnly;
    private bool _typing;
    private uint? _zoom;
    private (int Width, int Height, uint Scale) _sessionSize;
    private SessionState _state = SessionState.Connecting;
    private string _statusText = "Connessione in corso…";

    public RdpSessionView(ConnectionInfo info, SessionEndpoint endpoint, RustDeskTunnels tunnels)
    {
        _info = info;
        _endpoint = endpoint;
        _tunnels = tunnels;
        _options = info.Rdp ?? RdpOptions.Default;
        _smartSizing = _options.Display == "scale";

        _formsHost.Child = _rdp;
        _ended.Children.Add(_endedTitle);
        _ended.Children.Add(_endedText);
        var again = new Button { Content = "Riconnetti", Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Center };
        again.Click += (_, _) => Reconnect();
        _ended.Children.Add(again);
        _root.Children.Add(_formsHost);
        _root.Children.Add(_ended);

        _rdp.Status += text => SetStatus(text);
        _rdp.LoginComplete += () =>
        {
            _loggedIn = true;
            SetStatus("Connesso" + (_options.AdminSession ? " (sessione amministrativa)" : ""), SessionState.Connected);
            // La finestra può essere cambiata durante l'accesso: si allinea subito la risoluzione.
            UpdateResolution();
        };
        _rdp.Disconnected += OnDisconnected;
        _rdp.FatalError += code => SetStatus($"Errore grave del controllo RDP ({code})", SessionState.Failed);
        _rdp.LogonError += code =>
        {
            if (code is 0 or -1073741715)
            {
                SetStatus("Credenziali rifiutate dal server: inseriscile a mano o correggi la credenziale nel portale");
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
        Commands = BuildCommands();
    }

    public UIElement View => _root;

    public SessionState State => _state;

    public string StatusText => _statusText;

    public IReadOnlyList<CommandGroup> Commands { get; }

    public event Action? StateChanged;

    /// <summary>La sessione è finita per scelta dell'utente (disconnessione o disconnetti/esci da Windows): la scheda si chiude.</summary>
    public event Action? Ended;

    private bool Ready => _loggedIn && !_closing;

    private bool CanType => Ready && !_viewOnly && !_typing;

    private IReadOnlyList<CommandGroup> BuildCommands() =>
    [
        new("Sessione",
        [
            new SessionCommand("\uE72C", "Riconnetti", "Chiude e riapre la sessione con le stesse impostazioni", Reconnect) { CanExecute = () => !_closing },
            new SessionCommand("\uEA14", "Disconnetti", "Disconnette lasciando aperta la scheda (la sessione resta attiva sul server)", Disconnect)
            {
                CanExecute = () => !_closing && _state is SessionState.Connected or SessionState.Connecting,
            },
            new SessionCommand("\uE740", "Schermo intero", "Schermo intero (Ctrl+Alt+Pausa per tornare alla finestra)", SetFullScreen) { CanExecute = () => Ready },
        ]),
        new("Appunti",
        [
            new SessionCommand("\uE77F", "Digita appunti", "Digita il testo degli appunti nella sessione, tasto per tasto: utile alla schermata di accesso, nelle console o dove incollare non funziona",
                () => _ = TypeClipboardAsync()) { CanExecute = () => CanType },
            new SessionCommand("\uE722", "Screenshot", "Copia negli appunti un'immagine della sessione", () => _ = ScreenshotAsync(save: false)) { CanExecute = () => Ready },
            new SessionCommand("\uE74E", "Salva immagine", "Salva un'immagine della sessione in un file PNG", () => _ = ScreenshotAsync(save: true)) { CanExecute = () => Ready },
        ]),
        new("Tastiera",
        [
            new SessionCommand("\uE72E", "Ctrl+Alt+Canc", RemoteKeys.CtrlAltDel.ToolTip, () => Send(RemoteKeys.CtrlAltDel)) { CanExecute = () => CanType },
            new SessionCommand("\uE80F", "Start", "Apre il menu Start del server", OpenStart) { CanExecute = () => CanType },
            new SessionCommand("\uE92E", "Altri tasti", "Combinazioni da inviare alla sessione")
            {
                CanExecute = () => CanType,
                Menu = [.. RemoteKeys.Others.Select(k => new SessionCommand("\uE765", k.Label, k.ToolTip, () => Send(k)) { CanExecute = () => CanType })],
            },
        ]),
        new("Visualizzazione",
        [
            new SessionCommand("\uE9A6", "Adatta", "Scala l'immagine nella scheda invece di cambiare la risoluzione del server", ToggleSmartSizing)
            {
                CanExecute = () => _rdp.IsHandleCreated && !_closing && !_resizeUnsupported,
                IsChecked = () => _smartSizing || _resizeUnsupported,
            },
            new SessionCommand("\uE71F", "Riduci", "Rimpicciolisce testi e finestre nella sessione", () => Zoom(-1)) { CanExecute = () => CanZoom(-1) },
            new SessionCommand("\uE8A3", "Ingrandisci", "Ingrandisce testi e finestre nella sessione", () => Zoom(1)) { CanExecute = () => CanZoom(1) },
            new SessionCommand("\uE890", "Solo visualizzazione", "Blocca mouse e tastiera verso la sessione: si guarda senza intervenire", ToggleViewOnly)
            {
                CanExecute = () => Ready,
                IsChecked = () => _viewOnly,
            },
        ]),
    ];

    private void SetStatus(string text, SessionState? state = null)
    {
        _statusText = text;
        if (state is { } value)
        {
            _state = value;
        }

        StateChanged?.Invoke();
    }

    public async Task StartAsync()
    {
        // La dimensione del desktop si decide quando la scheda è già disposta a schermo.
        await WaitForLayoutAsync();
        await _root.Dispatcher.InvokeAsync(() =>
        {
            // Il controllo ActiveX va configurato dopo la creazione dell'handle.
            _rdp.CreateControl();
            Configure();
            SetStatus("Connessione in corso…", SessionState.Connecting);
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
        var (width, height, windowsScale) = DesktopSize();
        var desktopScale = _zoom ?? windowsScale;
        _sessionSize = (width, height, desktopScale);
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
        TrySet(advanced, "SmartSizing", _smartSizing || _resizeUnsupported);
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
        _rdp.TrySetExtended("DeviceScaleFactor", DisplayScale.DeviceScale(desktopScale));

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
    private (int Width, int Height, uint Scale) DesktopSize()
    {
        var source = PresentationSource.FromVisual(_root);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var width = (int)Math.Clamp(_formsHost.ActualWidth * scale, 200, 8192);
        var height = (int)Math.Clamp(_formsHost.ActualHeight * scale, 200, 8192);
        var desktopScale = (uint)Math.Clamp(Math.Round(scale * 100), 100, 500);
        return (width & ~1, height & ~1, desktopScale);
    }

    private void UpdateResolution()
    {
        if (!_loggedIn || _closing || _smartSizing || _resizeUnsupported)
        {
            return;
        }

        var (width, height, windowsScale) = DesktopSize();
        ApplyDisplay(width, height, _zoom ?? windowsScale);
    }

    /// <summary>Risoluzione e scala della sessione senza riconnettersi (server 2012 R2 e successivi).</summary>
    private bool ApplyDisplay(int width, int height, uint desktopScale)
    {
        if ((width, height, desktopScale) == _sessionSize)
        {
            return true;
        }

        try
        {
            _rdp.Rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, (uint)width, (uint)height, 0u, desktopScale, DisplayScale.DeviceScale(desktopScale));
            _sessionSize = (width, height, desktopScale);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Server vecchio (prima di 2012 R2) o criterio che lo vieta: l'immagine si scala nella scheda.
            Log.Warn("Ridimensionamento RDP non supportato dal server, passo alla scala: " + ex.Message);
            _resizeUnsupported = true;
            TrySet(_rdp.Rdp.AdvancedSettings9, "SmartSizing", true);
            SetStatus("Il server non consente di cambiare risoluzione: l'immagine viene scalata");
            return false;
        }
    }

    private void ToggleSmartSizing()
    {
        _smartSizing = !_smartSizing;
        TrySet(_rdp.Rdp.AdvancedSettings9, "SmartSizing", _smartSizing);
        UpdateResolution();
        StateChanged?.Invoke();
    }

    private bool CanZoom(int direction)
    {
        if (!Ready || _smartSizing || _resizeUnsupported)
        {
            return false;
        }

        return DisplayScale.Step(_sessionSize.Scale, direction) is not null;
    }

    private void Zoom(int direction)
    {
        if (!CanZoom(direction) || DisplayScale.Step(_sessionSize.Scale, direction) is not { } scale)
        {
            return;
        }

        var (width, height, windowsScale) = DesktopSize();
        if (ApplyDisplay(width, height, scale))
        {
            _zoom = scale == windowsScale ? null : scale;
            SetStatus($"Zoom {scale}%");
        }
    }

    private void ToggleViewOnly()
    {
        _viewOnly = !_viewOnly;
        // Con il controllo disabilitato mouse e tastiera non arrivano alla sessione; l'immagine resta aggiornata.
        _rdp.Enabled = !_viewOnly;
        SetStatus(_viewOnly ? "Solo visualizzazione: mouse e tastiera bloccati" : "Connesso");
    }

    private void Send(KeyCombination keys)
    {
        if (!CanType)
        {
            return;
        }

        SetStatus(_rdp.SendKeys(keys.Strokes) ? $"Inviato {keys.Label}" : $"{keys.Label} non inviato: il controllo RDP non lo consente");
    }

    private void OpenStart()
    {
        if (!CanType)
        {
            return;
        }

        try
        {
            // Azione remota del controllo (Windows 8 / Server 2012 e successivi); se manca, Ctrl+Esc.
            _rdp.Focus();
            _rdp.Rdp.SendRemoteAction(3);
            SetStatus("Menu Start aperto");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Send(RemoteKeys.CtrlEsc);
        }
    }

    /// <summary>Digita il testo degli appunti nella sessione come tasti della tastiera, a piccoli blocchi.</summary>
    private async Task TypeClipboardAsync()
    {
        if (!CanType)
        {
            return;
        }

        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            text = "";
        }

        if (text.Length == 0)
        {
            SetStatus("Gli appunti non contengono testo");
            return;
        }

        if (text.Length > ClipboardTyping.MaxCharacters)
        {
            Dialogs.Show(Window.GetWindow(_root), "Testo troppo lungo",
                $"Gli appunti contengono {text.Length} caratteri: se ne possono digitare al massimo {ClipboardTyping.MaxCharacters}. " +
                "Per testi lunghi usa gli appunti condivisi o copia un file.", DialogKind.Warning);
            return;
        }

        // Ogni carattere diventa i tasti del layout in uso; quelli che il layout non ha si saltano.
        var characters = new List<IReadOnlyList<KeyStroke>>(text.Length);
        var skipped = 0;
        foreach (var key in ClipboardTyping.Plan(text))
        {
            if (Native.StrokesFor(key) is { } strokes)
            {
                characters.Add(strokes);
            }
            else
            {
                skipped++;
            }
        }

        _typing = true;
        SetStatus($"Digitazione di {characters.Count} caratteri…");
        try
        {
            _rdp.Focus();
            foreach (var batch in KeyStrokes.Batch(characters))
            {
                // I tasti vanno solo al controllo RDP, mai ad altre finestre; ci si ferma se la sessione cade.
                if (!Ready || !_rdp.SendKeys(batch))
                {
                    SetStatus("Digitazione interrotta: la sessione non è più disponibile");
                    return;
                }

                await Task.Delay(15);
            }

            SetStatus(skipped == 0
                ? $"Digitati {characters.Count} caratteri dagli appunti"
                : $"Digitati {characters.Count} caratteri; {skipped} saltati perché assenti dal layout di tastiera");
        }
        finally
        {
            _typing = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Immagine dell'area della sessione così com'è a schermo.</summary>
    private async Task ScreenshotAsync(bool save)
    {
        if (!Ready)
        {
            return;
        }

        // Si lascia chiudere il suggerimento del pulsante, che potrebbe coprire la sessione.
        await Task.Delay(200);
        using var bitmap = Capture();
        if (bitmap is null)
        {
            SetStatus("Immagine non acquisita: la sessione non è visibile");
            return;
        }

        if (!save)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetImage(bitmap);
                SetStatus("Immagine della sessione copiata negli appunti");
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                SetStatus("Appunti non disponibili: " + ex.Message);
            }

            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salva l'immagine della sessione",
            FileName = FileNames.For(_info.Name, "png", DateTime.Now),
            Filter = "Immagine PNG|*.png",
            DefaultExt = ".png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        var owner = Window.GetWindow(_root);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
        {
            return;
        }

        try
        {
            bitmap.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            SetStatus("Immagine salvata: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            Dialogs.Show(Window.GetWindow(_root), "Immagine non salvata", ex.Message, DialogKind.Error);
        }
    }

    private System.Drawing.Bitmap? Capture()
    {
        if (!_rdp.IsHandleCreated || !_rdp.Visible || _rdp.Width <= 0 || _rdp.Height <= 0)
        {
            return null;
        }

        var bounds = _rdp.RectangleToScreen(_rdp.ClientRectangle);
        var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return bitmap;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            bitmap.Dispose();
            return null;
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
                SetStatus("Riconnessione…", SessionState.Connecting);
                _rdp.Rdp.Disconnect();
                return;
            }

            ShowDesktop();
            Configure();
            SetStatus("Connessione in corso…", SessionState.Connecting);
            _rdp.Rdp.Connect();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            SetStatus("Riconnessione non riuscita: " + ex.Message, SessionState.Disconnected);
        }
    }

    /// <summary>Disconnette senza chiudere la scheda; la sessione sul server resta aperta.</summary>
    private void Disconnect()
    {
        try
        {
            if ((int)_rdp.Rdp.Connected != 0)
            {
                _disconnectRequested = true;
                _rdp.Rdp.Disconnect();
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _disconnectRequested = false;
            SetStatus("Disconnessione non riuscita: " + ex.Message);
        }
    }

    private void ShowDesktop()
    {
        _ended.Visibility = Visibility.Collapsed;
        _formsHost.Visibility = Visibility.Visible;
    }

    private void ShowEnded(string title, string message)
    {
        // Il controllo ActiveX resterebbe un rettangolo bianco: lo si nasconde e si mostra un pannello.
        _formsHost.Visibility = Visibility.Collapsed;
        _endedTitle.Text = title;
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
            SetStatus("Tunnel riaperto, riconnessione automatica…");
        }
    });

    private void OnDisconnected(int reason)
    {
        _loggedIn = false;
        if (_viewOnly)
        {
            _viewOnly = false;
            _rdp.Enabled = true;
        }

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

        if (_disconnectRequested)
        {
            _disconnectRequested = false;
            SetStatus("Disconnesso", SessionState.Disconnected);
            ShowEnded("Disconnesso", "La sessione resta attiva sul server: con Riconnetti si riprende da dove si era rimasti.");
            return;
        }

        if (EndedByUser(reason, extended))
        {
            SetStatus("Sessione chiusa", SessionState.Disconnected);
            Ended?.Invoke();
            return;
        }

        SetStatus("Disconnesso", SessionState.Disconnected);
        ShowEnded("Sessione non attiva", reason == 3 ? "Il server ha chiuso la connessione (" + description + ")." : description);
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
