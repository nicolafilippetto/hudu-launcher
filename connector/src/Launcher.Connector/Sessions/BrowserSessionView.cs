using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Pagina web in una scheda del Launcher (WebView2). La pagina usa l'indirizzo vero dell'apparato
/// (IP o FQDN): il browser integrato passa da un proxy SOCKS5 locale che porta quell'indirizzo nel
/// tunnel RustDesk. Così l'intestazione Host, il certificato e i reindirizzamenti restano quelli
/// dell'apparato (iDRAC con controllo dell'host, SSO di vCenter sull'FQDN) e la cifratura resta tra
/// pagina e apparato. Ogni scheda ha un profilo suo: cookie separati tra clienti con gli stessi IP.
/// </summary>
public sealed class BrowserSessionView : ISessionView
{
    private readonly ConnectionInfo _info;
    private readonly SessionEndpoint _endpoint;
    private readonly KnownHosts _knownHosts;
    private readonly WebOptions _options;
    private readonly Uri _home;
    private readonly Grid _root = new();
    private readonly WebView2 _web = new();
    private readonly List<Window> _popups = [];
    private readonly Dictionary<string, Task<bool>> _certificateAnswers = new(StringComparer.Ordinal);
    private readonly Func<int, CancellationToken, Task<CredentialInfo>>? _fetchLogin;
    private readonly IReadOnlyList<WebLoginCandidate> _candidates;
    private readonly List<CoreWebView2Frame> _frames = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _filling;
    private bool _autoLoginDone;
    private Socks5Proxy? _proxy;
    private CoreWebView2Environment? _environment;
    private string? _profile;
    private bool _temporaryProfile;
    private SessionState _state = SessionState.Connecting;
    private string _statusText = "Apertura della pagina…";
    private bool _closing;

    public BrowserSessionView(ConnectionInfo info, SessionEndpoint endpoint, KnownHosts knownHosts,
        Func<int, CancellationToken, Task<CredentialInfo>>? fetchLogin = null)
    {
        _info = info;
        _endpoint = endpoint;
        _knownHosts = knownHosts;
        _options = info.WebOrDefault;
        _fetchLogin = fetchLogin;
        _candidates = fetchLogin is not null && _options.Login != "off" ? info.WebLogin?.Candidates ?? [] : [];
        _home = new Uri(_options.Url);
        _root.Children.Add(_web);
        Commands = BuildCommands();
    }

    public UIElement View => _root;

    public SessionState State => _state;

    public string StatusText => _statusText;

    public IReadOnlyList<CommandGroup> Commands { get; }

    public event Action? StateChanged;

    private CoreWebView2? Core => _web.CoreWebView2;

    private IReadOnlyList<CommandGroup> BuildCommands()
    {
        var groups = new List<CommandGroup>();
        if (_candidates.Count > 0)
        {
            // Una credenziale: un clic. Più credenziali: si sceglie dall'elenco (nome e utente, mai la password).
            var login = _candidates.Count == 1
                ? new SessionCommand("\uE8D7", "Accesso", $"Compila l'accesso con {_candidates[0].Label} (da Hudu)", () => _ = FillLoginAsync(_candidates[0]))
                {
                    CanExecute = () => Core is not null && !_filling,
                }
                : new SessionCommand("\uE8D7", "Accesso", "Scegli la credenziale di Hudu con cui compilare l'accesso")
                {
                    CanExecute = () => Core is not null && !_filling,
                    Menu = [.. _candidates.Select(c => new SessionCommand("\uE77B", c.Label, "Compila utente e password nella pagina", () => _ = FillLoginAsync(c)))],
                };
            groups.Add(new CommandGroup("Hudu", [login]));
        }

        groups.AddRange(BaseCommands());
        return groups;
    }

    private IReadOnlyList<CommandGroup> BaseCommands() =>
    [
        new("Navigazione",
        [
            new SessionCommand("", "Indietro", "Pagina precedente (Alt+Freccia sinistra)", () => Core?.GoBack()) { CanExecute = () => Core?.CanGoBack == true },
            new SessionCommand("", "Avanti", "Pagina successiva (Alt+Freccia destra)", () => Core?.GoForward()) { CanExecute = () => Core?.CanGoForward == true },
            new SessionCommand("", "Ricarica", "Ricarica la pagina (F5)", () => Core?.Reload()) { CanExecute = () => Core is not null },
            new SessionCommand("", "Inizio", $"Torna a {_home}", () => Core?.Navigate(_home.ToString())) { CanExecute = () => Core is not null },
        ]),
        new("Visualizzazione",
        [
            new SessionCommand("", "Riduci", "Rimpicciolisce la pagina (anche Ctrl+rotellina)", () => Zoom(-0.1)) { CanExecute = () => Core is not null },
            new SessionCommand("", "Ingrandisci", "Ingrandisce la pagina (anche Ctrl+rotellina)", () => Zoom(0.1)) { CanExecute = () => Core is not null },
            new SessionCommand("", "Zoom 100%", "Dimensione normale", () => _web.ZoomFactor = 1) { CanExecute = () => Core is not null },
        ]),
        new("Pagina",
        [
            new SessionCommand("", "Apri nel browser", ExternalToolTip(), OpenExternal),
            new SessionCommand("", "Copia indirizzo", "Copia negli appunti l'indirizzo della pagina", () => ClipboardHelper.Copy(Core?.Source ?? _home.ToString())),
        ]),
    ];

    private string ExternalToolTip() => _endpoint.Direct
        ? "Apre la pagina nel browser predefinito"
        : "Apre la pagina nel browser predefinito tramite la porta locale del tunnel (alcuni apparati, come iDRAC e vCenter, lì non funzionano)";

    public async Task StartAsync()
    {
        _proxy = new Socks5Proxy(WebRoutes.For(_info.Target, _endpoint), Log.Warn);
        _proxy.Start();
        _environment = await CreateEnvironmentAsync();
        await _web.EnsureCoreWebView2Async(_environment);
        Configure(Core!);
        Core!.NavigationCompleted += OnNavigationCompleted;
        Core.DocumentTitleChanged += (_, _) => UpdateTitle();
        Core.HistoryChanged += (_, _) => StateChanged?.Invoke();
        Core.ProcessFailed += (_, e) => SetStatus($"Il browser integrato si è interrotto ({e.ProcessFailedKind}): premi Ricarica", SessionState.Failed);
        // Riquadri della pagina (alcuni apparati mettono il modulo di accesso in un iframe).
        Core.FrameCreated += (_, e) =>
        {
            _frames.Add(e.Frame);
            e.Frame.Destroyed += (_, _) => _frames.Remove(e.Frame);
        };
        _web.ZoomFactor = _options.ZoomFactor;
        Log.Info($"Pagina web di {_info.Name} nel Launcher: {_home} ({_info.Route})");
        Core.Navigate(_home.ToString());
    }

    /// <summary>
    /// Ambiente WebView2 della scheda: proxy SOCKS5 del Launcher e profilo proprio. Con "ricorda
    /// l'accesso" il profilo è quello dell'asset; se è già aperto in un'altra scheda se ne usa uno temporaneo.
    /// </summary>
    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = $"--proxy-server=socks5://127.0.0.1:{_proxy!.Port}",
        };

        if (_options.KeepCookies)
        {
            var name = new string(_info.HostKeyAlias.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            _profile = Path.Combine(ConnectorSettings.WebProfilesDirectory, "asset-" + name);
            try
            {
                Directory.CreateDirectory(_profile);
                return await CoreWebView2Environment.CreateAsync(null, _profile, options);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or IOException)
            {
                Log.Warn($"Profilo web di {_info.Name} non disponibile, uso un profilo temporaneo: {ex.Message}");
            }
        }

        _temporaryProfile = true;
        _profile = Path.Combine(ConnectorSettings.WebProfilesDirectory, "tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profile);
        return await CoreWebView2Environment.CreateAsync(null, _profile, options);
    }

    /// <summary>Impostazioni comuni alla scheda e alle finestre che apre (console HTML5).</summary>
    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
        core.ServerCertificateErrorDetected += OnCertificateError;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += (_, e) =>
        {
            if (!_options.Downloads)
            {
                e.Cancel = true;
                SetStatus("Download bloccato dalle impostazioni dell'asset");
            }
        };
        // Solo pagine web: niente file locali o protocolli esterni dalla pagina dell'apparato.
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is not ("http" or "https" or "about" or "data" or "blob"))
            {
                e.Cancel = true;
            }
        };
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            SetStatus(Title(), SessionState.Connected);
            _ = AutoLoginAsync();
            return;
        }

        if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        SetStatus(e.WebErrorStatus switch
        {
            CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset =>
                "Pagina non raggiungibile: controlla indirizzo e porta" + (_endpoint.Direct ? "" : " (tramite il jump host)"),
            CoreWebView2WebErrorStatus.HostNameNotResolved => "Nome host non risolto" + (_endpoint.Direct ? "" : " dal jump host"),
            CoreWebView2WebErrorStatus.Timeout => "La pagina non risponde",
            _ when e.WebErrorStatus.ToString().StartsWith("Certificate", StringComparison.Ordinal) => "Certificato non accettato",
            _ => $"Pagina non caricata ({e.WebErrorStatus})",
        }, SessionState.Failed);
    }

    private void UpdateTitle()
    {
        if (_state == SessionState.Connected)
        {
            SetStatus(Title(), SessionState.Connected);
        }
    }

    private string Title() => Core?.DocumentTitle is { Length: > 0 } title ? title : "Pagina aperta";

    private void Zoom(double delta) => _web.ZoomFactor = Math.Clamp(Math.Round(_web.ZoomFactor + delta, 2), 0.25, 5);

    private void OpenExternal()
    {
        var current = Uri.TryCreate(Core?.Source, UriKind.Absolute, out var source) && source.Scheme is "http" or "https" ? source : _home;
        var target = _endpoint.Direct ? current : new UriBuilder(current) { Host = _endpoint.Host, Port = _endpoint.Port }.Uri;
        Process.Start(new ProcessStartInfo(target.ToString()) { UseShellExecute = true })?.Dispose();
    }

    // ------------------------------------------------------------------ accesso con le credenziali di Hudu

    /// <summary>
    /// Compila utente e password nella pagina. Prima si controlla che ci sia un modulo di accesso
    /// sull'host dell'asset; solo allora si chiede al portale la password della credenziale scelta,
    /// che va dritta nei campi e non resta da nessuna parte.
    /// </summary>
    private async Task FillLoginAsync(WebLoginCandidate candidate)
    {
        if (_filling || Core is not { } core || _fetchLogin is null)
        {
            return;
        }

        _filling = true;
        StateChanged?.Invoke();
        try
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || !WebRoutes.SameHost(page.Host, _info.Target.Host))
            {
                SetStatus("La pagina non è sull'indirizzo dell'asset: accesso non compilato");
                return;
            }

            var targets = await LoginTargetsAsync();
            if (targets.Count == 0)
            {
                SetStatus("Nessun modulo di accesso nella pagina");
                return;
            }

            SetStatus($"Accesso con {candidate.Name}…");
            var credential = await _fetchLogin(candidate.Id, _cts.Token);
            var script = WebLoginScript.Fill(_info.Target.Host, credential.Username, credential.Password);
            var result = WebLoginFill.NoForm;
            foreach (var target in targets)
            {
                var filled = WebLoginScript.Parse(await target(script));
                if (filled > result)
                {
                    result = filled;
                }
            }

            SetStatus(result switch
            {
                WebLoginFill.Filled => $"Accesso compilato con {candidate.Name}: conferma nella pagina",
                WebLoginFill.UserOnly => "Utente compilato: alla schermata della password premi di nuovo Accesso",
                _ => "Modulo di accesso non riconosciuto: compila a mano",
            });
        }
        catch (PortalException ex)
        {
            SetStatus("Accesso non compilato: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn("Compilazione dell'accesso non riuscita: " + ex.Message);
            SetStatus("Accesso non compilato: la pagina è cambiata, riprova");
        }
        finally
        {
            _filling = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Pagina e riquadri sull'host dell'asset che hanno un modulo di accesso.</summary>
    private async Task<List<Func<string, Task<string>>>> LoginTargetsAsync()
    {
        var detect = WebLoginScript.Detect(_info.Target.Host);
        var candidates = new List<Func<string, Task<string>>> { script => Core!.ExecuteScriptAsync(script) };
        candidates.AddRange(_frames.ToList().Select(frame => (Func<string, Task<string>>)(script => frame.ExecuteScriptAsync(script))));
        var found = new List<Func<string, Task<string>>>();
        foreach (var target in candidates)
        {
            try
            {
                if (WebLoginScript.Parse(await target(detect)) is WebLoginFill.Filled or WebLoginFill.UserOnly)
                {
                    found.Add(target);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // riquadro chiuso nel frattempo
            }
        }

        return found;
    }

    /// <summary>Opzione "automatico": con una sola credenziale si compila la prima volta che compare il modulo.</summary>
    private async Task AutoLoginAsync()
    {
        if (_options.Login != "auto" || _candidates.Count != 1 || _autoLoginDone || _filling)
        {
            return;
        }

        // Si lascia comparire il modulo (pagine che lo disegnano dopo il caricamento).
        await Task.Delay(800);
        if (_closing || _autoLoginDone || (await LoginTargetsAsync()).Count == 0)
        {
            return;
        }

        _autoLoginDone = true;
        await FillLoginAsync(_candidates[0]);
    }

    // ------------------------------------------------------------------ certificati

    /// <summary>
    /// Certificato non valido (tipico degli apparati: autofirmato o con un altro nome). Solo per
    /// l'host dell'asset si può proseguire, secondo l'opzione scelta nel portale; "chiedi" ricorda
    /// l'impronta come per le chiavi SSH e avvisa se cambia.
    /// </summary>
    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (!Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) || !WebRoutes.SameHost(uri.Host, _info.Target.Host))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            return;
        }

        switch (_options.Certificate)
        {
            case "ignore":
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                return;
            case "block":
                e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                SetStatus("Certificato non valido: pagina bloccata dalle impostazioni dell'asset", SessionState.Failed);
                return;
        }

        string fingerprint;
        string subject;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(e.ServerCertificate.ToPemEncoding());
            fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
            subject = certificate.Subject;
        }
        catch (CryptographicException)
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            return;
        }

        var alias = "web:" + _info.HostKeyAlias;
        var status = _knownHosts.Check(alias, fingerprint);
        if (status == HostKeyStatus.Trusted)
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            return;
        }

        var deferral = e.GetDeferral();
        _ = AnswerAsync();

        async Task AnswerAsync()
        {
            try
            {
                // Più risorse della stessa pagina possono chiedere insieme: una sola domanda.
                if (!_certificateAnswers.TryGetValue(fingerprint, out var answer))
                {
                    answer = AskAsync(alias, fingerprint, subject, uri.Host, e.ErrorStatus, status);
                    _certificateAnswers[fingerprint] = answer;
                }

                e.Action = await answer ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    private async Task<bool> AskAsync(string alias, string fingerprint, string subject, string host, CoreWebView2WebErrorStatus error, HostKeyStatus status)
    {
        // La domanda si fa fuori dall'evento di WebView2, che non va bloccato.
        await Task.Yield();
        var owner = Window.GetWindow(_root);
        var detail = $"{subject}\nSHA256 {fingerprint}";
        var accepted = status == HostKeyStatus.Unknown
            ? Dialogs.Confirm(owner, $"Certificato non verificabile per {host}",
                $"Il certificato della pagina di {_info.Name} non è valido ({Describe(error)}), come capita spesso con gli apparati " +
                "che usano un certificato autofirmato. Se lo accetti viene ricordato e il Launcher avvisa se dovesse cambiare.",
                "Accetta e ricorda", "Annulla", DialogKind.Question, detail)
            : Dialogs.Confirm(owner, $"Il certificato di {host} è cambiato",
                "Può essere un rinnovo del certificato, ma anche un tentativo di intercettazione. Continua solo se il cambio è noto.",
                "Accetta il nuovo certificato", "Annulla", DialogKind.Security, detail, confirmIsDefault: false);
        if (accepted)
        {
            _knownHosts.Trust(alias, fingerprint);
            Log.Info($"Certificato web accettato per {alias}");
        }
        else
        {
            SetStatus("Certificato non accettato: pagina non aperta", SessionState.Failed);
        }

        return accepted;
    }

    private static string Describe(CoreWebView2WebErrorStatus error) => error switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "il nome non corrisponde",
        CoreWebView2WebErrorStatus.CertificateExpired => "scaduto",
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "certificato client non valido",
        CoreWebView2WebErrorStatus.CertificateRevoked => "revocato",
        _ => "emesso da un'autorità non attendibile",
    };

    // ------------------------------------------------------------------ nuove finestre

    /// <summary>Le pagine che aprono una finestra (console HTML5 di iDRAC e iLO) la aprono nel Launcher, con lo stesso proxy.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!_options.Popups || _closing || _environment is null)
        {
            e.Handled = true;
            SetStatus("Finestra bloccata dalle impostazioni dell'asset");
            return;
        }

        var deferral = e.GetDeferral();
        _ = OpenPopupAsync();

        async Task OpenPopupAsync()
        {
            try
            {
                var browser = new WebView2();
                var window = new Window
                {
                    Title = $"{_info.Name} — {_info.Company}",
                    Width = e.WindowFeatures.HasSize ? Math.Max(400, e.WindowFeatures.Width) : 1200,
                    Height = e.WindowFeatures.HasSize ? Math.Max(300, e.WindowFeatures.Height) : 800,
                    Owner = Window.GetWindow(_root),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = browser,
                    Icon = Window.GetWindow(_root)?.Icon,
                };
                window.Show();
                await browser.EnsureCoreWebView2Async(_environment);
                Configure(browser.CoreWebView2);
                browser.CoreWebView2.DocumentTitleChanged += (_, _) => window.Title = $"{browser.CoreWebView2.DocumentTitle} — {_info.Name}";
                browser.CoreWebView2.WindowCloseRequested += (_, _) => window.Close();
                window.Closed += (_, _) =>
                {
                    _popups.Remove(window);
                    browser.Dispose();
                };
                _popups.Add(window);
                e.NewWindow = browser.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Log.Warn("Finestra della pagina web non aperta: " + ex.Message);
                e.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    private void SetStatus(string text, SessionState? state = null)
    {
        _statusText = text;
        if (state is { } value)
        {
            _state = value;
        }

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _cts.CancelAsync();
        foreach (var popup in _popups.ToList())
        {
            popup.Close();
        }

        _web.Dispose();
        if (_proxy is not null)
        {
            await _proxy.DisposeAsync();
        }

        _cts.Dispose();

        if (_temporaryProfile && _profile is { } profile)
        {
            // Il profilo temporaneo (cookie e cache) si cancella quando il browser integrato ha chiuso i file.
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(profile); attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    ConnectorSettings.TryDeleteDirectory(profile);
                }
            });
        }
    }
}
