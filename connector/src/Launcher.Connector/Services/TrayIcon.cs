using System.Drawing;
using System.Windows.Forms;

namespace Launcher.Connector.Services;

/// <summary>
/// Icona nell'area di notifica per la modalità in background: doppio clic apre il Launcher, il menu
/// permette di aprirlo o di uscire del tutto.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new ContextMenuStrip();
        var openItem = menu.Items.Add("Apri Hudu Launcher", null, (_, _) => open());
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Hudu Launcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    /// <summary>Avviso breve accanto all'icona (per esempio la prima volta che la finestra si chiude).</summary>
    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/launcher.ico"));
        if (resource is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
