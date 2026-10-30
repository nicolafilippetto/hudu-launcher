using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Launcher.Connector.Sessions;

public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question,
    Security,
}

/// <summary>
/// Messaggi e conferme con lo stesso stile delle Impostazioni (tema Fluent di Windows), al posto
/// delle vecchie MessageBox. Ctrl+C copia il testo, come nelle finestre di Windows.
/// </summary>
public static class Dialogs
{
    private static readonly Brush InfoBrush = Frozen(0x00, 0x78, 0xD4);
    private static readonly Brush WarningBrush = Frozen(0xDF, 0x6C, 0x01);
    private static readonly Brush ErrorBrush = Frozen(0xE5, 0x53, 0x4B);

    /// <summary>Messaggio con il solo pulsante OK.</summary>
    public static void Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info, string? detail = null) =>
        Run(owner, title, message, kind, detail, "OK", cancel: null, confirmIsDefault: true);

    /// <summary>Domanda con due pulsanti: true se si sceglie <paramref name="confirm"/>.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirm, string cancel = "Annulla",
        DialogKind kind = DialogKind.Question, string? detail = null, bool confirmIsDefault = true) =>
        Run(owner, title, message, kind, detail, confirm, cancel, confirmIsDefault);

    private static bool Run(Window? owner, string title, string message, DialogKind kind, string? detail,
        string confirm, string? cancel, bool confirmIsDefault)
    {
        owner ??= Application.Current?.MainWindow;
        var hasOwner = owner is { IsVisible: true };
        var window = new Window
        {
            Title = "Hudu Launcher",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = hasOwner ? owner : null,
            ShowInTaskbar = !hasOwner,
        };

        var (glyph, brush) = kind switch
        {
            DialogKind.Warning => ("", WarningBrush),
            DialogKind.Error => ("", ErrorBrush),
            DialogKind.Question => ("", InfoBrush),
            DialogKind.Security => ("", WarningBrush),
            _ => ("", InfoBrush),
        };

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 20 });
        if (detail is not null)
        {
            // Testo selezionabile (impronte, percorsi), in carattere a larghezza fissa.
            text.Children.Add(new TextBox
            {
                Text = detail,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
            });
        }

        var body = new Grid { Margin = new Thickness(24, 22, 24, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock
        {
            Style = (Style)Application.Current!.FindResource("Icon"),
            Text = glyph,
            FontSize = 28,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0),
        };
        body.Children.Add(icon);
        Grid.SetColumn(text, 1);
        body.Children.Add(text);

        var primary = new Button { Content = confirm, MinWidth = 96, Padding = new Thickness(16, 5, 16, 5) };
        if (Application.Current.TryFindResource("AccentButtonStyle") is Style accent)
        {
            primary.Style = accent;
        }

        primary.Click += (_, _) => window.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(primary);
        Button? secondary = null;
        if (cancel is null)
        {
            primary.IsDefault = true;
            primary.IsCancel = true;
        }
        else
        {
            secondary = new Button { Content = cancel, IsCancel = true, MinWidth = 96, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(secondary);
            primary.IsDefault = confirmIsDefault;
            secondary.IsDefault = !confirmIsDefault;
        }

        // Fascia dei pulsanti come nelle finestre di Windows 11.
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            Padding = new Thickness(24, 14, 24, 14),
            Margin = new Thickness(0, 22, 0, 0),
            Child = buttons,
        };

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        window.Content = root;

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox)
            {
                ClipboardHelper.Copy($"{title}\r\n\r\n{message}" + (detail is null ? "" : $"\r\n\r\n{detail}"));
                e.Handled = true;
            }
        };
        window.Loaded += (_, _) =>
        {
            ((confirmIsDefault || secondary is null) ? primary : secondary).Focus();
            Sound(kind);
        };

        return window.ShowDialog() == true;
    }

    private static void Sound(DialogKind kind)
    {
        switch (kind)
        {
            case DialogKind.Error:
                System.Media.SystemSounds.Hand.Play();
                break;
            case DialogKind.Warning or DialogKind.Security:
                System.Media.SystemSounds.Exclamation.Play();
                break;
            case DialogKind.Question:
                System.Media.SystemSounds.Question.Play();
                break;
            default:
                System.Media.SystemSounds.Asterisk.Play();
                break;
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
