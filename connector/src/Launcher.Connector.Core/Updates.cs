using System.Security.Cryptography;

namespace Launcher.Connector.Core;

/// <summary>
/// Versioni del Launcher: X.Y.Z con suffisso facoltativo delle build di prova (1.3.2-ci.40).
/// Una versione con suffisso viene prima della stessa senza (1.3.2-ci.40 &lt; 1.3.2).
/// </summary>
public static class AppVersion
{
    public static bool TryParse(string? text, out (int Major, int Minor, int Patch, string? Suffix) version)
    {
        version = default;
        var value = text?.Trim().Split('+')[0];
        if (string.IsNullOrEmpty(value) || value.Length > 50)
        {
            return false;
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? value : value[..dash];
        var suffix = dash < 0 ? null : value[(dash + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var patch)
            || suffix?.Length == 0)
        {
            return false;
        }

        version = (major, minor, patch, suffix);
        return true;
    }

    /// <summary>Negativo se a &lt; b, zero se uguali, positivo se a &gt; b. Le versioni non valide valgono meno di tutte.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var va);
        var okB = TryParse(b, out var vb);
        if (!okA || !okB)
        {
            return okA.CompareTo(okB);
        }

        var result = (va.Major, va.Minor, va.Patch).CompareTo((vb.Major, vb.Minor, vb.Patch));
        if (result != 0)
        {
            return result;
        }

        if (va.Suffix is null || vb.Suffix is null)
        {
            return (va.Suffix is null).CompareTo(vb.Suffix is null);
        }

        // Suffissi "ci.N": si confronta il numero; altrimenti in ordine alfabetico.
        return int.TryParse(va.Suffix.Split('.')[^1], out var na) && int.TryParse(vb.Suffix.Split('.')[^1], out var nb)
            ? na.CompareTo(nb)
            : string.CompareOrdinal(va.Suffix, vb.Suffix);
    }
}

/// <summary>Pacchetti di Velopack nella cartella dell'installazione: <c>&lt;id&gt;-&lt;versione&gt;-full|delta.nupkg</c>.</summary>
public static class PackageFiles
{
    /// <summary>Versione dal nome del file ("HuduLauncher-1.4.10-ci.3-full.nupkg" → "1.4.10-ci.3"), o null.</summary>
    public static string? VersionOf(string fileName)
    {
        if (!fileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = fileName[..^".nupkg".Length];
        var first = name.IndexOf('-', StringComparison.Ordinal);
        var last = name.LastIndexOf('-');
        if (first <= 0 || last <= first || name[(last + 1)..] is not ("full" or "delta"))
        {
            return null;
        }

        var version = name[(first + 1)..last];
        return AppVersion.TryParse(version, out _) ? version : null;
    }

    /// <summary>Pacchetto di una versione precedente a quella in uso (si può togliere).</summary>
    public static bool IsOlder(string fileName, string currentVersion) =>
        VersionOf(fileName) is { } version && AppVersion.Compare(version, currentVersion) < 0;
}

/// <summary>
/// Firma del feed degli aggiornamenti. La pipeline firma il feed (releases.win.json) con una chiave
/// ECDSA P-256 che sta solo nei segreti di GitHub; il Launcher contiene la chiave pubblica e accetta
/// solo feed firmati. Il feed contiene gli hash dei pacchetti, che Velopack verifica a ogni download:
/// il portale può distribuire gli aggiornamenti ma non crearne o modificarne.
/// </summary>
public static class UpdateSignature
{
    public static bool Verify(byte[] feed, byte[] signature, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || signature.Length == 0)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            return key.KeySize == 256 && key.VerifyData(feed, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
