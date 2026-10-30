using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Launcher.Connector.Services;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Comando della barra della sessione. Con <see cref="IsChecked"/> è un interruttore, con
/// <see cref="Menu"/> apre un elenco di comandi.
/// </summary>
public sealed class SessionCommand(string glyph, string label, string toolTip, Action? execute = null)
{
    public string Glyph => glyph;

    public string Label => label;

    public string ToolTip => toolTip;

    public Action? Execute => execute;

    public Func<bool>? CanExecute { get; init; }

    public Func<bool>? IsChecked { get; init; }

    public IReadOnlyList<SessionCommand>? Menu { get; init; }
}

public sealed record CommandGroup(string Title, IReadOnlyList<SessionCommand> Commands);

/// <summary>
/// Barra dei comandi della scheda, a gruppi come una ribbon: icona sopra, etichetta sotto e nome
/// del gruppo. Ridotta, mostra solo le icone. A destra il riepilogo della connessione.
/// </summary>
public sealed class CommandBar : Border
{
    private static readonly Brush SeparatorBrush = Frozen(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private readonly StackPanel _groups = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _detail = new() { FontSize = 11.5, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _toggle;
    private readonly List<(SessionCommand Command, ButtonBase Button)> _buttons = [];
    private IReadOnlyList<CommandGroup> _commands = [];

    public CommandBar()
    {
        Padding = new Thickness(6, 4, 8, 4);
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = SeparatorBrush;
        Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x80, 0x80, 0x80));

        _toggle = new Button { Style = (Style)Application.Current.FindResource("RibbonButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _toggle.Click += (_, _) => Compact = !Compact;

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 4, 0) };
        info.Children.Add(_title);
        info.Children.Add(_detail);

        var scroll = new ScrollViewer
        {
            Content = _groups,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_toggle, Dock.Right);
        DockPanel.SetDock(scroll, Dock.Left);
        layout.Children.Add(_toggle);
        layout.Children.Add(scroll);
        layout.Children.Add(info);
        Child = layout;

        CompactChanged += Rebuild;
        Unloaded += (_, _) => CompactChanged -= Rebuild;
        Loaded += (_, _) =>
        {
            CompactChanged -= Rebuild;
            CompactChanged += Rebuild;
            Rebuild();
        };
        Rebuild();
    }

    /// <summary>Barra ridotta (solo icone), uguale per tutte le schede e ricordata tra un avvio e l'altro.</summary>
    public static bool Compact
    {
        get => ConnectorSettings.CompactToolbar;
        set
        {
            ConnectorSettings.CompactToolbar = value;
            CompactChanged?.Invoke();
        }
    }

    private static event Action? CompactChanged;

    public void SetCommands(IReadOnlyList<CommandGroup> commands)
    {
        _commands = commands;
        Rebuild();
    }

    public void SetInfo(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = detail;
        _title.ToolTip = title;
        _detail.ToolTip = detail;
    }

    /// <summary>Aggiorna pulsanti attivi e interruttori dopo un cambio di stato della sessione.</summary>
    public void Refresh()
    {
        foreach (var (command, button) in _buttons)
        {
            button.IsEnabled = command.CanExecute?.Invoke() ?? true;
            if (button is ToggleButton toggle && command.IsChecked is { } isChecked)
            {
                toggle.IsChecked = isChecked();
            }
        }
    }

    private void Rebuild()
    {
        var compact = Compact;
        _toggle.Content = Glyph(compact ? "" : "", 10);
        _toggle.ToolTip = compact ? "Mostra le etichette dei comandi" : "Riduci la barra (solo icone)";
        _detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _groups.Children.Clear();
        _buttons.Clear();

        for (var g = 0; g < _commands.Count; g++)
        {
            if (g > 0)
            {
                _groups.Children.Add(new Border { Width = 1, Background = SeparatorBrush, Margin = new Thickness(6, 2, 6, 2) });
            }

            var group = _commands[g];
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var command in group.Commands)
            {
                var button = CreateButton(command, compact);
                _buttons.Add((command, button));
                row.Children.Add(button);
            }

            var panel = new StackPanel();
            panel.Children.Add(row);
            if (!compact)
            {
                panel.Children.Add(new TextBlock { Text = group.Title, FontSize = 10.5, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
            }

            _groups.Children.Add(panel);
        }

        Refresh();
    }

    private ButtonBase CreateButton(SessionCommand command, bool compact)
    {
        ButtonBase button = command.IsChecked is not null
            ? new ToggleButton { Style = (Style)Application.Current.FindResource("RibbonToggle") }
            : new Button { Style = (Style)Application.Current.FindResource("RibbonButton") };

        var content = new StackPanel { MinWidth = compact ? 0 : 52 };
        content.Children.Add(Glyph(command.Glyph, compact ? 16 : 18));
        if (!compact)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            label.Children.Add(new TextBlock { Text = command.Label, FontSize = 11.5, MaxWidth = 92, TextTrimming = TextTrimming.CharacterEllipsis });
            if (command.Menu is not null)
            {
                label.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "", FontSize = 8, Margin = new Thickness(4, 1, 0, 0) });
            }

            content.Children.Add(label);
        }

        button.Content = content;
        button.ToolTip = compact ? $"{command.Label}: {command.ToolTip}" : command.ToolTip;
        button.Click += (_, _) =>
        {
            if (command.Menu is { } menu)
            {
                OpenMenu(button, menu);
            }
            else
            {
                command.Execute?.Invoke();
            }

            Refresh();
        };
        return button;
    }

    private void OpenMenu(ButtonBase button, IReadOnlyList<SessionCommand> commands)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var command in commands)
        {
            var item = new MenuItem
            {
                Header = command.Label,
                ToolTip = command.ToolTip,
                Icon = Glyph(command.Glyph, 14),
                IsEnabled = command.CanExecute?.Invoke() ?? true,
            };
            item.Click += (_, _) =>
            {
                command.Execute?.Invoke();
                Refresh();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Style = (Style)Application.Current.FindResource("Icon"),
        Text = glyph,
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
