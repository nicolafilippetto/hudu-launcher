using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Connections;

/// <summary>Un file del Launcher distribuito dal portale.</summary>
public sealed record LauncherFile(string Name, long Size);

/// <summary>Launcher incluso nell'immagine: versione, installer, MSI per Intune e feed firmato.</summary>
public sealed record LauncherBundle(string Version, LauncherFile? Setup, LauncherFile? Msi, bool Signed)
{
    /// <summary>Il file da scaricare: l'MSI (per utente, anche con doppio clic); l'installer exe solo se l'MSI manca.</summary>
    public LauncherFile? Download => Msi ?? Setup;
}

/// <summary>
/// Il Launcher per Windows è dentro l'immagine Docker (cartella <c>launcher</c> accanto al portale),
/// della stessa versione del portale: installer, MSI e feed di Velopack firmato dalla pipeline. Il
/// portale li serve in sola lettura; i file non si possono cambiare dal portale.
/// </summary>
public sealed partial class LauncherReleases
{
    public const string Feed = "releases.win.json";

    private readonly Lazy<LauncherBundle?> _bundle;

    /// <param name="configuration">Launcher:ReleasesDirectory sostituisce la cartella predefinita (per i test).</param>
    public LauncherReleases(IWebHostEnvironment environment, IConfiguration configuration)
    {
        Directory = configuration["Launcher:ReleasesDirectory"] is { Length: > 0 } custom ? custom : Path.Combine(environment.ContentRootPath, "launcher");
        _bundle = new(() => Load(Directory));
    }

    public string Directory { get; }

    /// <summary>Launcher incluso, o null se l'immagine non lo contiene (es. compilazione locale).</summary>
    public LauncherBundle? Bundle => _bundle.Value;

    /// <summary>Percorso di un file distribuibile, solo con nomi semplici e solo se esiste nella cartella.</summary>
    public string? Resolve(string name)
    {
        // Solo i file del Launcher: installer, MSI, pacchetti, feed e firma.
        if (!SafeName().IsMatch(name) || Path.GetExtension(name).ToLowerInvariant() is not (".exe" or ".msi" or ".nupkg" or ".json" or ".sig"))
        {
            return null;
        }

        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".exe" => "application/vnd.microsoft.portable-executable",
        ".msi" => "application/x-msi",
        _ => "application/octet-stream",
    };

    private static LauncherBundle? Load(string directory)
    {
        var feed = Path.Combine(directory, Feed);
        if (!File.Exists(feed))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(feed));
            var version = doc.RootElement.GetProperty("Assets").EnumerateArray()
                .Select(a => a.GetProperty("Version").GetString())
                .OfType<string>()
                .OrderByDescending(v => v, LauncherVersion.Comparer)
                .FirstOrDefault();
            if (version is null)
            {
                return null;
            }

            LauncherFile? Find(string pattern) => new DirectoryInfo(directory).EnumerateFiles(pattern)
                .Where(f => SafeName().IsMatch(f.Name))
                .Select(f => new LauncherFile(f.Name, f.Length))
                .FirstOrDefault();

            return new LauncherBundle(version, Find("*-Setup.exe"), Find("*.msi"), File.Exists(feed + ".sig"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();
}

/// <summary>
/// Versioni del Launcher (X.Y.Z, con suffisso per le build di prova: 1.3.2-ci.40 &lt; 1.3.2).
/// Stesse regole del Launcher.
/// </summary>
public static partial class LauncherVersion
{
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create((a, b) => Compare(a, b));

    public static bool IsValid(string? value) => value is { Length: <= 50 } && Pattern().IsMatch(value);

    /// <summary>Negativo se a &lt; b; le versioni non valide (o assenti) valgono meno di tutte.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = IsValid(a);
        var okB = IsValid(b);
        if (!okA || !okB)
        {
            return okA.CompareTo(okB);
        }

        var ma = Pattern().Match(a!);
        var mb = Pattern().Match(b!);
        for (var i = 1; i <= 3; i++)
        {
            var result = int.Parse(ma.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(mb.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture));
            if (result != 0)
            {
                return result;
            }
        }

        var sa = ma.Groups[4].Success ? ma.Groups[4].Value : null;
        var sb = mb.Groups[4].Success ? mb.Groups[4].Value : null;
        if (sa is null || sb is null)
        {
            return (sa is null).CompareTo(sb is null);
        }

        return int.TryParse(sa.Split('.')[^1], out var na) && int.TryParse(sb.Split('.')[^1], out var nb)
            ? na.CompareTo(nb)
            : string.CompareOrdinal(sa, sb);
    }

    [GeneratedRegex(@"^(\d{1,5})\.(\d{1,5})\.(\d{1,6})(?:-([0-9A-Za-z.]{1,30}))?$")]
    private static partial Regex Pattern();
}

public enum LauncherState
{
    /// <summary>Mai usato con questo account.</summary>
    Missing,
    /// <summary>Più vecchio della versione minima: il portale lo rifiuta.</summary>
    BelowMinimum,
    /// <summary>C'è una versione più recente nel portale.</summary>
    Outdated,
    Current,
}

/// <summary>Situazione del Launcher di un utente rispetto a quello incluso nel portale e al minimo richiesto.</summary>
public sealed record LauncherStatus(LauncherState State, string? Installed, DateTimeOffset? LastSeen, string? Machine, string? Available, string? Minimum)
{
    public static LauncherStatus Of(LauncherClient? client, string? available, string? minimum)
    {
        var installed = client?.Version;
        var state = client is null ? LauncherState.Missing
            : minimum is not null && LauncherVersion.Compare(installed, minimum) < 0 ? LauncherState.BelowMinimum
            : available is not null && LauncherVersion.Compare(installed, available) < 0 ? LauncherState.Outdated
            : LauncherState.Current;
        return new LauncherStatus(state, installed, client?.LastSeenAt, client?.Machine, available, minimum);
    }

    /// <summary>Per le tabelle: la versione, o "versione precedente" per i Launcher che non la comunicano.</summary>
    public string InstalledLabel => Installed ?? "versione precedente";

    /// <summary>Per le frasi: "la 1.4.0" oppure "una versione precedente".</summary>
    public string InstalledText => Installed is null ? "una versione precedente" : "la " + Installed;
}

/// <summary>Versione e ultimo uso del Launcher per utente, comunicati dal Launcher a ogni richiesta.</summary>
public sealed partial class LauncherClients(AppDbContext db, TimeProvider time)
{
    public const string VersionHeader = "X-Launcher-Version";
    public const string MachineHeader = "X-Launcher-Machine";

    /// <summary>Versione dichiarata dal Launcher (null se assente o non valida, come i Launcher precedenti).</summary>
    public static string? VersionOf(HttpContext http) =>
        http.Request.Headers[VersionHeader].ToString() is var value && LauncherVersion.IsValid(value) ? value : null;

    public async Task RecordAsync(string email, HttpContext http, CancellationToken ct)
    {
        var machine = http.Request.Headers[MachineHeader].ToString();
        var row = await db.LauncherClients.FindAsync([email.ToLowerInvariant()], ct);
        if (row is null)
        {
            row = new LauncherClient { Email = email.ToLowerInvariant() };
            db.LauncherClients.Add(row);
        }

        row.Version = VersionOf(http);
        row.Machine = MachineName().IsMatch(machine) ? machine : null;
        row.LastSeenAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public Task<LauncherClient?> FindAsync(string email, CancellationToken ct) =>
        db.LauncherClients.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email.ToLowerInvariant(), ct);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex MachineName();
}
