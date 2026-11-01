using Launcher.Server.Connections;
using Launcher.Server.Settings;

namespace Launcher.Server.Pages;

/// <summary>Download del Launcher incluso nel portale e situazione del Launcher dell'utente.</summary>
public sealed class LauncherModel(LauncherReleases releases, LauncherClients clients, SettingsService settings) : LauncherPageModel
{
    public LauncherBundle? Bundle { get; private set; }

    public LauncherStatus Status { get; private set; } = LauncherStatus.Of(null, null, null);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Bundle = releases.Bundle;
        Status = LauncherStatus.Of(await clients.FindAsync(Email, ct), Bundle?.Version, settings.LauncherMinVersion);
    }
}
