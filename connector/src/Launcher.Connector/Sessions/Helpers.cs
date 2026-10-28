using System.Windows;
using System.Windows.Controls;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>Copia del testo selezionato nel terminale.</summary>
public static class ClipboardHelper
{
    public static void Copy(string value)
    {
        try
        {
            Clipboard.SetDataObject(value, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn("Appunti non disponibili: " + ex.Message);
        }
    }
}

/// <summary>Finestra per chiedere utente e password quando il portale non ha una credenziale.</summary>
public static class CredentialPrompt
{
    public static (string User, string Password)? Ask(string title, string? defaultUser)
    {
        var window = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ShowInTaskbar = false,
        };
        var user = new TextBox { Text = defaultUser ?? "", Margin = new Thickness(0, 4, 0, 10) };
        var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 14) };
        var ok = new Button { Content = "Connetti", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Padding = new Thickness(16, 4, 16, 4) };
        ok.Click += (_, _) => window.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Nessuna credenziale associata nel portale. Inseriscila per questa connessione.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Utente" });
        panel.Children.Add(user);
        panel.Children.Add(new TextBlock { Text = "Password" });
        panel.Children.Add(password);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => (string.IsNullOrEmpty(user.Text) ? (Control)user : password).Focus();

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(user.Text)
            ? (user.Text.Trim(), password.Password)
            : null;
    }
}
