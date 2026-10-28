using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Core;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>Impostazioni del Launcher: per ora solo l'indirizzo del portale.</summary>
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
        var save = new Button { Content = "Salva", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = !fromPolicy };
        var cancel = new Button { Content = fromPolicy ? "Chiudi" : "Annulla", IsCancel = true, Padding = new Thickness(16, 4, 16, 4) };

        save.Click += (_, _) =>
        {
            try
            {
                var normalized = PortalClient.ValidatePortalUrl(url.Text).ToString().TrimEnd('/');
                ConnectorSettings.SavePortalUrl(normalized);
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
