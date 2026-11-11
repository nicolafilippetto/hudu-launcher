using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>Impostazioni del Launcher: indirizzo del portale e avvio all'accesso a Windows.</summary>
public static class SettingsDialog
{
    /// <summary>Mostra la finestra; restituisce true se è stato salvato qualcosa.</summary>
    public static bool Show(Window owner)
    {
        var fromPolicy = ConnectorSettings.PortalUrlFromPolicy;
        var window = new Window
        {
            Title = "Impostazioni",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };

        var url = new TextBox
        {
            Text = ConnectorSettings.PortalUrl ?? "https://",
            IsReadOnly = fromPolicy,
            Margin = new Thickness(0, 4, 0, 6),
        };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var autoStartPolicy = ConnectorSettings.AutoStartPolicy;
        var autoStart = new CheckBox
        {
            Content = "Tieni il Launcher pronto in background (avvio con Windows)",
            IsChecked = AutoStart.IsWanted,
            IsEnabled = autoStartPolicy is null,
            Margin = new Thickness(0, 6, 0, 4),
        };
        var save = new Button { Content = "Salva", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Padding = new Thickness(16, 4, 16, 4) };

        save.Click += (_, _) =>
        {
            try
            {
                // Indirizzo lasciato vuoto: resta da proporre con il primo link del portale.
                var empty = ConnectorSettings.PortalUrl is null && url.Text.Trim() is "" or "https://" or "http://";
                if (!fromPolicy && !empty)
                {
                    var normalized = PortalClient.ValidatePortalUrl(url.Text).ToString().TrimEnd('/');
                    ConnectorSettings.SavePortalUrl(normalized);
                }

                if (autoStartPolicy is null && autoStart.IsChecked != AutoStart.IsWanted)
                {
                    AutoStart.Choose(autoStart.IsChecked == true);
                }

                window.DialogResult = true;
            }
            catch (PortalException)
            {
                error.Text = "Indirizzo non valido: serve un indirizzo https, ad esempio https://launcher.example.com " +
                             "(http solo verso un indirizzo IP interno, per prova).";
                error.Visibility = Visibility.Visible;
            }
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Indirizzo del portale", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(url);
        panel.Children.Add(new TextBlock
        {
            Text = fromPolicy
                ? "Impostato dall'amministratore tramite criterio: non modificabile da qui."
                : "Lo stesso indirizzo che apri nel browser per vedere le connessioni. Se resta vuoto, viene proposto dal primo link del portale.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(error);
        panel.Children.Add(autoStart);
        panel.Children.Add(new TextBlock
        {
            Text = autoStartPolicy is null
                ? "Il Launcher parte all'accesso a Windows senza finestra e resta nell'area di notifica (vicino all'orologio): " +
                  "tiene pronto l'accesso Microsoft 365 e si aggiorna da solo, così le connessioni dal portale partono subito. " +
                  "Chiudendo la finestra resta attivo; per uscire del tutto usa Esci dall'icona."
                : "Impostato dall'amministratore tramite criterio.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            url.Focus();
            url.CaretIndex = url.Text.Length;
        };

        return window.ShowDialog() == true;
    }
}
