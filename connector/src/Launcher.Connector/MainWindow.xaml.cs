using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Launcher.Connector.Core;
using Launcher.Connector.Services;
using Launcher.Connector.Sessions;

namespace Launcher.Connector;

public partial class MainWindow : Window
{
    private readonly ConnectionWorkflow _workflow;
    private readonly LauncherUpdater _updater = new();
    private bool _updateOffered;
    private bool _updating;
    private readonly System.Windows.Threading.DispatcherTimer _offlineTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>Manutenzione in background: accesso 365 rinnovato e controllo degli aggiornamenti.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _maintenance = new() { Interval = TimeSpan.FromMinutes(20) };
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);
    private DateTimeOffset _lastUpdateCheck;
    private TrayIcon? _tray;
    private bool _exiting;

    public MainWindow(ConnectionWorkflow workflow)
    {
        _workflow = workflow;
        InitializeComponent();
        RestorePlacement();
        // Ogni volta che la finestra si nasconde (X, uscita, aggiornamento) se ne ricorda posizione e dimensione.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _shown = true;
            }
            else
            {
                SavePlacement();
            }
        };
        Tabs.Items.Clear();
        UpdateEmptyState();
        UpdateStatus();
        Closing += OnClosing;
        _workflow.Offline.Changed += () => Dispatcher.BeginInvoke(RenderOffline);
        _workflow.UpdateRequired += () => Dispatcher.BeginInvoke(() => _ = CheckForUpdatesAsync(required: true));
        _offlineTimer.Tick += (_, _) =>
        {
            _workflow.Offline.ExpireIfNeeded();
            RenderOffline();
        };
        _offlineTimer.Start();
        _maintenance.Tick += (_, _) =>
        {
            _ = _workflow.WarmUpAsync(Handle, periodic: true);
            if (DateTimeOffset.UtcNow - _lastUpdateCheck >= UpdateCheckInterval)
            {
                _ = CheckForUpdatesAsync(required: false);
            }
        };
    }

    /// <summary>
    /// Lavoro d'avvio, anche con la finestra nascosta: icona nell'area di notifica, aggiornamenti e
    /// accesso 365 preparati subito, poi rinnovati periodicamente. L'indirizzo del portale non si
    /// chiede più all'avvio: arriva dalla compilazione, da un criterio o dal primo link del portale.
    /// </summary>
    public void Start(bool background)
    {
        _tray = new TrayIcon(BringToFront, ExitFromTray);
        _ = CheckForUpdatesAsync(required: false);
        _ = _workflow.WarmUpAsync(Handle);
        _workflow.CleanupStaleEntries();
        // Dopo l'avvio, a Launcher inattivo: WebView2, Desktop remoto e SSH caricati e rilasciati, senza finestre.
        _ = Prewarm.RunAsync(Handle, () => !_exiting && Tabs.Items.Count == 0);
        _maintenance.Start();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        if (background)
        {
            Log.Info("Launcher pronto in background (icona nell'area di notifica)");
        }
    }

    /// <summary>
    /// Rete tornata disponibile (per esempio Wi-Fi connesso dopo l'accesso a Windows): l'accesso 365 si
    /// prepara subito, senza aspettare il prossimo rinnovo periodico.
    /// </summary>
    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable && !_exiting)
        {
            Dispatcher.BeginInvoke(() => _ = _workflow.WarmUpAsync(Handle, periodic: true));
        }
    }

    private bool _shown;

    /// <summary>
    /// Posizione e dimensione dell'ultima volta, se cadono ancora su uno schermo (altrimenti al centro,
    /// come al primo avvio). Lo stato ingrandito si applica quando la finestra si mostra.
    /// </summary>
    private void RestorePlacement()
    {
        if (WindowBounds.Parse(ConnectorSettings.WindowPlacement) is not { } saved)
        {
            return;
        }

        if (saved.FitTo(ScreenAreas()) is not { } bounds)
        {
            Log.Info("Posizione della finestra salvata fuori dagli schermi attuali: si apre al centro");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
        if (bounds.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        // Mai mostrata (avvio in background senza aprirla): non c'è niente di nuovo da ricordare.
        if (!_shown)
        {
            return;
        }

        var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (normal.IsEmpty || normal.Width < MinWidth / 2 || double.IsNaN(normal.Left))
        {
            return;
        }

        try
        {
            ConnectorSettings.WindowPlacement = new WindowBounds(normal.Left, normal.Top, normal.Width, normal.Height,
                WindowState == WindowState.Maximized).Serialize();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Posizione della finestra non salvata: " + ex.Message);
        }
    }

    /// <summary>Aree di lavoro degli schermi collegati, convertite nelle unità della finestra (DIP).</summary>
    private List<ScreenArea> ScreenAreas()
    {
        new WindowInteropHelper(this).EnsureHandle();
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return [.. System.Windows.Forms.Screen.AllScreens.Select(screen =>
        {
            var area = screen.WorkingArea;
            var topLeft = toDip.Transform(new Point(area.Left, area.Top));
            var size = toDip.Transform(new Vector(area.Width, area.Height));
            return new ScreenArea(topLeft.X, topLeft.Y, size.X, size.Y);
        })];
    }

    /// <summary>Disconnessione o spegnimento di Windows: pulizia veloce, senza attese né domande.</summary>
    public void PrepareForSessionEnd()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        SavePlacement();
        _tray?.Dispose();
        _workflow.Shutdown();
        _updater.ApplyOnExit();
        Log.Info("Chiusura per fine sessione di Windows");
    }

    /// <summary>
    /// Finestra nascosta, nessuna connessione e cache offline vuota (si perderebbe al riavvio): si può
    /// aggiornare senza disturbare.
    /// </summary>
    private bool Idle => !IsVisible && Tabs.Items.Count == 0 && _workflow.Offline.Items.Count == 0;

    private void UpdateStatus() =>
        StatusText.Text = $"Versione {ConnectorSettings.Version} · Portale: {ConnectorSettings.PortalUrl ?? "non configurato (Impostazioni in basso a destra)"}";

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (SettingsDialog.Show(this))
        {
            UpdateStatus();
        }
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Apre una nuova scheda per il biglietto ricevuto dal browser (o prepara la cache offline).</summary>
    public void Open(LaunchRequest request)
    {
        if (_exiting)
        {
            Log.Info("Link ignorato: il Launcher si sta chiudendo");
            return;
        }

        BringToFront();
        if (request.Offline)
        {
            _ = PrepareOfflineAsync(request);
            return;
        }

        var tab = NewTab();
        _ = _workflow.RunAsync(request, tab, Handle, ConfirmPortal);
    }

    private SessionTab NewTab()
    {
        var tab = new SessionTab();
        tab.CloseRequested += () => _ = CloseTabAsync(tab);
        tab.ContextMenu = TabMenu(tab);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateEmptyState();
        return tab;
    }

    private ContextMenu TabMenu(SessionTab tab)
    {
        var menu = new ContextMenu();
        var close = new MenuItem { Header = "Chiudi", InputGestureText = "Ctrl+W" };
        close.Click += (_, _) => _ = CloseTabAsync(tab);
        var others = new MenuItem { Header = "Chiudi le altre" };
        others.Click += async (_, _) =>
        {
            foreach (var other in Tabs.Items.OfType<SessionTab>().Where(t => t != tab).ToList())
            {
                await CloseTabAsync(other);
            }
        };
        menu.Opened += (_, _) => others.IsEnabled = Tabs.Items.Count > 1;
        menu.Items.Add(close);
        menu.Items.Add(others);
        return menu;
    }

    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem is SessionTab tab)
        {
            tab.BringIntoView();
        }
    }

    /// <summary>La rotellina sulla striscia delle schede le fa scorrere in orizzontale.</summary>
    private void OnTabStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer strip)
        {
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }
    }

    /// <summary>Elenco di tutte le connessioni aperte, utile quando le schede non stanno nella striscia.</summary>
    private void OnTabList(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var tab in Tabs.Items.OfType<SessionTab>())
        {
            var item = new MenuItem { Header = tab.DisplayName, ToolTip = tab.ToolTip, IsChecked = tab == Tabs.SelectedItem };
            item.Click += (_, _) => Tabs.SelectedItem = tab;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ aggiornamenti dal portale

    /// <summary>
    /// Controlla sul portale se c'è una versione più recente; la propone una volta per avvio (sempre se
    /// il portale la rende obbligatoria). Gli errori non disturbano: si riprova al prossimo avvio.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool required)
    {
        if (_updating || PortalClient.TryNormalizePortalUrl(ConnectorSettings.PortalUrl) is not { } portal)
        {
            return;
        }

        string? version;
        _lastUpdateCheck = DateTimeOffset.UtcNow;
        try
        {
            version = _updater.AvailableVersion ?? await _updater.CheckAsync(portal, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Controllo degli aggiornamenti non riuscito: " + ex.Message);
            if (required && IsVisible)
            {
                Dialogs.Show(this, "Il Launcher va aggiornato",
                    "Il portale richiede una versione più recente, ma l'aggiornamento automatico non è riuscito. " +
                    "Scarica il Launcher dal portale (pulsante Portale in basso a destra) e installalo.", DialogKind.Warning, ex.Message);
            }

            return;
        }

        if (version is null)
        {
            return;
        }

        UpdateButtonText.Text = $"Aggiorna a {version}";
        UpdateButton.Visibility = Visibility.Visible;
        if (Idle)
        {
            await UpdateSilentlyAsync(version);
            return;
        }

        if (_updateOffered && !required)
        {
            return;
        }

        _updateOffered = true;
        var text = required
            ? $"Il portale accetta solo Launcher aggiornati: installa la versione {version} (questa è la {ConnectorSettings.Version}) per continuare a collegarti."
            : $"È disponibile la versione {version} del Launcher (questa è la {ConnectorSettings.Version}), distribuita dal portale.";
        text += Tabs.Items.Count > 0
            ? "\n\nCi sono connessioni aperte: l'aggiornamento si scarica adesso e si installa alla chiusura del Launcher."
            : "\n\nIl Launcher si chiude, si aggiorna e si riapre in pochi secondi.";
        if (Dialogs.Confirm(this, required ? "Aggiornamento necessario" : "Aggiornamento disponibile", text, "Aggiorna ora", "Più tardi",
                required ? DialogKind.Warning : DialogKind.Info))
        {
            await UpdateAsync();
        }
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => _ = UpdateAsync();

    /// <summary>
    /// In background: scarica, installa senza finestre e riparte nascosto. Se nel frattempo il tecnico
    /// ha aperto il Launcher, l'aggiornamento resta pronto e si installa alla chiusura.
    /// </summary>
    private async Task UpdateSilentlyAsync(string version)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            if (!_updater.IsDownloaded)
            {
                await _updater.DownloadAsync(_ => { }, CancellationToken.None);
            }

            if (Idle && _updater.PrepareRestart(background: true))
            {
                Log.Info($"Aggiornamento alla versione {version} in background");
                await ExitAsync([]);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Aggiornamento in background non riuscito: " + ex.Message);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Scarica (pacchetto verificato con gli hash del feed firmato) e installa subito o alla chiusura.</summary>
    private async Task UpdateAsync()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (!_updater.IsDownloaded)
            {
                await _updater.DownloadAsync(p => Dispatcher.BeginInvoke(() => SetStatus($"Download dell'aggiornamento… {p}%")), CancellationToken.None);
            }

            if (Tabs.Items.Count == 0)
            {
                SetStatus("Installazione dell'aggiornamento: il Launcher si riapre da solo tra pochi secondi…");
                if (_updater.PrepareRestart())
                {
                    // Chiusura normale: l'updater attende l'uscita, installa e riapre il Launcher.
                    await ExitAsync([]);
                }

                return;
            }

            UpdateButtonText.Text = "Si aggiorna alla chiusura";
            UpdateStatus();
            Dialogs.Show(this, "Aggiornamento pronto", "Si installerà alla chiusura del Launcher, senza interrompere le connessioni aperte.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Aggiornamento non riuscito: " + ex.Message);
            UpdateStatus();
            Dialogs.Show(this, "Aggiornamento non riuscito", "Riprova più tardi o scarica il Launcher dal portale.", DialogKind.Warning, ex.Message);
        }
        finally
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ cache offline

    private async Task PrepareOfflineAsync(LaunchRequest request)
    {
        SetStatus("Preparazione della cache offline…");
        try
        {
            var (count, expiresAt) = await _workflow.PrepareOfflineAsync(request, Handle, ConfirmPortal, CancellationToken.None);
            Dialogs.Show(this, $"{count} connessioni pronte offline fino alle {expiresAt.ToLocalTime():HH:mm}",
                "Le trovi in \"Pronte offline\" (in basso a destra o nella schermata iniziale) e si aprono in diretta anche senza internet.\n\n" +
                "Non chiudere il Launcher: chiudendolo o spegnendo il PC la cache viene cancellata.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Cache offline non preparata", ex);
            Dialogs.Show(this, "Cache offline non preparata", ex.Message, DialogKind.Warning);
        }
        finally
        {
            UpdateStatus();
            RenderOffline();
        }
    }

    private static string Describe(ConnectionInfo info) =>
        $"{info.Name} · {info.Company} — {info.Label} {info.Target.Host}:{info.Target.Port}";

    private void RenderOffline()
    {
        var items = _workflow.Offline.Items;
        var minutes = (int)Math.Ceiling(_workflow.Offline.Remaining.TotalMinutes);
        OfflineButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OfflineButtonText.Text = $"Offline ({items.Count}) · {minutes} min";

        OfflinePanel.Children.Clear();
        OfflinePanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            return;
        }

        OfflinePanel.Children.Add(new TextBlock
        {
            Text = $"Pronte offline · ancora {minutes} min",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var info in items)
        {
            var button = new Button { Content = Describe(info), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(12, 6, 12, 6) };
            button.Click += (_, _) => OpenCached(info);
            OfflinePanel.Children.Add(button);
        }

        var clear = new Button { Content = "Svuota la cache offline", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        clear.Click += (_, _) => _workflow.Offline.Clear();
        OfflinePanel.Children.Add(clear);
    }

    private void OnOfflineMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OfflineButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var info in _workflow.Offline.Items)
        {
            var item = new MenuItem { Header = Describe(info) };
            item.Click += (_, _) => OpenCached(info);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Svuota la cache offline" };
        clear.Click += (_, _) => _workflow.Offline.Clear();
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void OpenCached(ConnectionInfo info)
    {
        if (_workflow.Offline.Remaining <= TimeSpan.Zero)
        {
            _workflow.Offline.ExpireIfNeeded();
            Dialogs.Show(this, "Cache offline scaduta", "Le connessioni preparate non valgono più: preparale di nuovo dal portale.");
            return;
        }

        var tab = NewTab();
        _ = _workflow.RunCachedAsync(info, tab);
    }

    /// <summary>Primo avvio da un link: si chiede se usare il portale da cui arriva.</summary>
    private bool ConfirmPortal(Uri portal)
    {
        var address = portal.GetLeftPart(UriPartial.Authority);
        var secure = portal.Scheme == Uri.UriSchemeHttps;
        var text = "Il Launcher non ha ancora un portale configurato. Conferma solo se è l'indirizzo che hai aperto nel browser; " +
                   "si può cambiare in Impostazioni.";
        if (!secure)
        {
            text += "\n\nAttenzione: l'indirizzo non è https. Va bene solo per prova sulla rete interna: " +
                    "le credenziali viaggiano in chiaro e l'accesso con Microsoft 365 non sarà disponibile.";
        }

        if (!Dialogs.Confirm(this, "Usare questo portale?", text, "Usa questo portale", "Annulla",
                secure ? DialogKind.Question : DialogKind.Warning, address, confirmIsDefault: false))
        {
            return false;
        }

        Dispatcher.BeginInvoke(UpdateStatus);
        return true;
    }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Native.SetForegroundWindow(Handle);
    }

    public void SetStatus(string text) => StatusText.Text = text;

    private async Task CloseTabAsync(SessionTab tab)
    {
        Tabs.Items.Remove(tab);
        UpdateEmptyState();
        await tab.DisposeAsync();
    }

    private void OnCloseTabCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (Tabs.SelectedItem is SessionTab tab)
        {
            _ = CloseTabAsync(tab);
        }
    }

    private void UpdateEmptyState()
    {
        var hasTabs = Tabs.Items.Count > 0;
        EmptyState.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenPortal(object sender, RoutedEventArgs e)
    {
        if (PortalClient.TryNormalizePortalUrl(ConnectorSettings.PortalUrl) is { } uri)
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            Dialogs.Show(this, "Portale non configurato", "Imposta l'indirizzo del portale in Impostazioni (ingranaggio in basso a destra).");
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConnectorSettings.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConnectorSettings.LogDirectory}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>
    /// X della finestra: in modalità background il Launcher si nasconde nell'area di notifica (chiudendo
    /// le connessioni) e resta pronto; altrimenti esce. Per uscire del tutto c'è Esci nell'icona.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        var open = Tabs.Items.OfType<SessionTab>().ToList();
        if (!ConfirmCloseSessions(open))
        {
            return;
        }

        if (!AutoStart.IsWanted)
        {
            await ExitAsync(open);
            return;
        }

        Hide();
        foreach (var tab in open)
        {
            await CloseTabAsync(tab);
        }

        if (!ConnectorSettings.TrayHintShown)
        {
            ConnectorSettings.TrayHintShown = true;
            _tray?.Notify("Il Launcher resta pronto", "Continua a lavorare in background. Per riaprirlo usa l'icona qui o un link del portale; per uscire, Esci dal menu dell'icona.");
        }

        // Un aggiornamento in attesa si installa adesso che non disturba.
        _ = CheckForUpdatesAsync(required: false);
    }

    private bool ConfirmCloseSessions(IReadOnlyList<SessionTab> open)
    {
        if (open.Count == 0)
        {
            return true;
        }

        var question = open.Count == 1 ? "Chiudere la connessione aperta?" : $"Chiudere le {open.Count} connessioni aperte?";
        return Dialogs.Confirm(this, question, "Le sessioni e i tunnel verranno chiusi.", "Chiudi tutto");
    }

    /// <summary>Esci dal menu dell'icona: chiude tutto e termina il Launcher.</summary>
    private void ExitFromTray()
    {
        if (_exiting)
        {
            return;
        }

        var open = Tabs.Items.OfType<SessionTab>().ToList();
        if (open.Count > 0)
        {
            BringToFront();
        }

        if (ConfirmCloseSessions(open))
        {
            _ = ExitAsync(open);
        }
    }

    /// <summary>
    /// Uscita vera: la finestra sparisce subito; sessioni, tunnel e aggiornamento si chiudono dopo,
    /// senza bloccarla (prima restava visibile e "Non risponde" durante la chiusura).
    /// </summary>
    private async Task ExitAsync(IReadOnlyList<SessionTab> open)
    {
        _exiting = true;
        _maintenance.Stop();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        Hide();
        await ShutdownAsync(open);
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Chiude sessioni e tunnel e avvia l'aggiornamento scaricato, con i tempi nel registro.</summary>
    private async Task ShutdownAsync(IReadOnlyList<SessionTab> open)
    {
        var watch = Stopwatch.StartNew();
        var budget = Task.Delay(TimeSpan.FromSeconds(10));
        // Le schede si chiudono insieme e dentro lo stesso limite di tempo: un tunnel che si sta ancora
        // aprendo non deve trattenere l'uscita.
        var closing = open.Select(tab =>
        {
            Tabs.Items.Remove(tab);
            return tab.DisposeAsync().AsTask();
        }).ToList();
        if (await Task.WhenAny(Task.WhenAll(closing), budget) == budget)
        {
            Log.Warn($"Chiusura: schede non chiuse dopo {watch.ElapsedMilliseconds} ms, si prosegue");
        }

        var sessions = watch.ElapsedMilliseconds;
        var cleanup = Task.Run(() =>
        {
            _workflow.Shutdown();
            var tunnels = watch.ElapsedMilliseconds;
            _updater.ApplyOnExit();
            Log.Info($"Chiusura: sessioni {sessions} ms, tunnel {tunnels - sessions} ms, aggiornamento {watch.ElapsedMilliseconds - tunnels} ms");
        });

        // Oltre 10 secondi in tutto non si aspetta più: l'app si chiude comunque (l'updater attende l'uscita).
        if (await Task.WhenAny(cleanup, budget) != cleanup)
        {
            Log.Warn($"Chiusura lenta: dopo {watch.ElapsedMilliseconds} ms tunnel o aggiornamento non hanno finito");
        }
        else if (cleanup.Exception is { } ex)
        {
            Log.Error("Chiusura: errore durante la pulizia", ex.GetBaseException());
        }
    }
}
