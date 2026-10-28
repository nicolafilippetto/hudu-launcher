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
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateEmptyState();
        return tab;
    }

    // ------------------------------------------------------------------ cache offline

    private async Task PrepareOfflineAsync(LaunchRequest request)
    {
        SetStatus("Preparazione della cache offline…");
        try
        {
            var (count, expiresAt) = await _workflow.PrepareOfflineAsync(request, Handle, ConfirmPortal, CancellationToken.None);
            MessageBox.Show(this,
                $"{count} connessioni pronte offline fino alle {expiresAt.ToLocalTime():HH:mm}.\n\n" +
                "Le trovi in \"Pronte offline\" (in basso a destra o nella schermata iniziale) e si aprono in diretta anche senza internet.\n\n" +
                "Non chiudere il Launcher: chiudendolo o spegnendo il PC la cache viene cancellata.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is PortalException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            Log.Error("Cache offline non preparata", ex);
            MessageBox.Show(this, "Cache offline non preparata: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, "La cache offline è scaduta: preparala di nuovo dal portale.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tab = NewTab();
        _ = _workflow.RunCachedAsync(info, tab);
    }

    /// <summary>Primo avvio da un link: si chiede se usare il portale da cui arriva.</summary>
    private bool ConfirmPortal(Uri portal)
    {
        var address = portal.GetLeftPart(UriPartial.Authority);
        var text = $"Il Launcher non ha ancora un portale configurato.\n\nUsare questo indirizzo come portale?\n\n{address}";
        if (portal.Scheme != Uri.UriSchemeHttps)
        {
            text += "\n\nAttenzione: l'indirizzo non è https. Va bene solo per prova sulla rete interna; " +
                    "le credenziali viaggiano in chiaro e l'accesso con Microsoft 365 non sarà disponibile.";
        }

        text += "\n\nRispondi Sì solo se è l'indirizzo che hai aperto nel browser. Si può cambiare in Impostazioni.";
        var answer = MessageBox.Show(this, text, Title, MessageBoxButton.YesNo,
            portal.Scheme == Uri.UriSchemeHttps ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
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
            MessageBox.Show(this, "L'indirizzo del portale non è configurato.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
            return;
        }

        var answer = MessageBox.Show(this, $"Chiudere {open.Count} connessioni aperte?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
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
        _ = Dispatcher.BeginInvoke(Close);
    }
}
