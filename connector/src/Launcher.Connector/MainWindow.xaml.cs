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
    private bool _closingConfirmed;

    public MainWindow(ConnectionWorkflow workflow)
    {
        _workflow = workflow;
        InitializeComponent();
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
        Loaded += (_, _) =>
        {
            // Primo avvio senza indirizzo del portale: si chiede subito, a meno che sia stato aperto
            // da un link del portale (in quel caso l'indirizzo lo propone il link).
            Dispatcher.BeginInvoke(() =>
            {
                if (ConnectorSettings.PortalUrl is null && Tabs.Items.Count == 0)
                {
                    OnOpenSettings(this, new RoutedEventArgs());
                }

                _ = CheckForUpdatesAsync(required: false);
            });
        };
    }

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
        try
        {
            version = _updater.AvailableVersion ?? await _updater.CheckAsync(portal, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Controllo degli aggiornamenti non riuscito: " + ex.Message);
            if (required)
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
                _workflow.Shutdown();
                if (_updater.PrepareRestart())
                {
                    // Chiusura normale: l'updater attende l'uscita, installa e riapre il Launcher.
                    _closingConfirmed = true;
                    Application.Current.Shutdown();
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
        catch (Exception ex) when (ex is PortalException or System.Net.Http.HttpRequestException or InvalidOperationException)
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

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        var open = Tabs.Items.OfType<SessionTab>().ToList();
        if (open.Count == 0)
        {
            // Nessuna sessione: si chiude subito, senza annullare la chiusura.
            _closingConfirmed = true;
            _workflow.Shutdown();
            _updater.ApplyOnExit();
            return;
        }

        var question = open.Count == 1 ? "Chiudere la connessione aperta?" : $"Chiudere le {open.Count} connessioni aperte?";
        if (!Dialogs.Confirm(this, question, "Le sessioni e i tunnel verranno chiusi.", "Chiudi tutto"))
        {
            e.Cancel = true;
            return;
        }

        // Si chiudono le sessioni (e i tunnel) e poi la finestra, fuori dall'evento di chiusura.
        e.Cancel = true;
        _closingConfirmed = true;
        foreach (var tab in open)
        {
            Tabs.Items.Remove(tab);
            await tab.DisposeAsync();
        }

        _workflow.Shutdown();
        _updater.ApplyOnExit();
        _ = Dispatcher.BeginInvoke(Close);
    }
}
