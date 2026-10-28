using System.Globalization;
using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Launcher.Server.Pages;

/// <summary>Base delle pagine: utente corrente e messaggi flash.</summary>
public abstract class LauncherPageModel : PageModel
{
    public string Email => User.GetEmail() ?? "";

    [Microsoft.AspNetCore.Mvc.TempData]
    public string? Flash { get; set; }

    [Microsoft.AspNetCore.Mvc.TempData]
    public string? FlashError { get; set; }
}

public static class Format
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string DateTime(DateTimeOffset? value, string timeZone)
    {
        if (value is null)
        {
            return "—";
        }

        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var tz) ? tz : TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(value.Value, zone).ToString("dd/MM/yyyy HH:mm:ss", Italian);
    }

    public static string Relative(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "mai";
        }

        var delta = DateTimeOffset.UtcNow - value.Value;
        return delta.TotalSeconds switch
        {
            < 60 => "pochi secondi fa",
            < 3600 => $"{(int)delta.TotalMinutes} min fa",
            < 7200 => "1 ora fa",
            < 86400 => $"{(int)delta.TotalHours} ore fa",
            < 172800 => "ieri",
            _ => $"{(int)delta.TotalDays} giorni fa",
        };
    }

    public static string Icon(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "i-monitor",
        ConnectionProtocol.Ssh or ConnectionProtocol.Telnet => "i-terminal",
        _ => "i-globe",
    };

    public static string ProtocolName(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Rdp => "RDP",
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Telnet => "Telnet",
        ConnectionProtocol.Http => "HTTP",
        ConnectionProtocol.Https => "HTTPS",
        _ => protocol.ToString(),
    };

    public static string PeerLabel(CachedPeer peer)
    {
        var name = peer.Alias ?? peer.Hostname;
        return string.IsNullOrWhiteSpace(name) ? peer.Id : $"{peer.Id} — {name}";
    }

    public static string ConnectionTitle(ResolvedConnection c) =>
        c.CanConnect
            ? $"{ProtocolName(c.Protocol)} {c.Host}:{c.Port} tramite RustDesk {c.GatewayPeerId} ({c.GatewaySource}) oppure diretta"
            : c.CanConnectDirect
                ? $"{ProtocolName(c.Protocol)} {c.Host}:{c.Port}: connessione diretta" + (c.Issues.Count > 0 ? $" ({string.Join(", ", c.Issues)})" : "")
                : "Non collegabile: " + string.Join(", ", c.Issues);
}

/// <summary>Dati comuni al layout (nome del portale, logo).</summary>
public sealed class LayoutInfo(SettingsService settings, AppDbContext db)
{
    public string PortalName => settings.Portal.Name;

    public bool HasLogo => db.Branding.Any(b => b.Kind == Endpoints.BrandingEndpoints.Logo);

    public string TimeZone => settings.Portal.TimeZone;

    /// <summary>Asset selezionabili per la cache offline (0 = funzione disattivata).</summary>
    public int OfflineMaxAssets => settings.Portal.OfflineMaxAssets;

    public int OfflineMinutes => settings.Portal.OfflineMinutes;

    /// <summary>false = RustDesk spento: solo connessioni dirette, niente gateway.</summary>
    public bool RustDeskEnabled => settings.RustDesk.Enabled;

    /// <summary>Richieste di accesso da approvare (mostrate agli amministratori).</summary>
    public int PendingUsers => db.PortalUsers.Count(u => u.Status == Data.PortalUserStatus.Pending);
}
