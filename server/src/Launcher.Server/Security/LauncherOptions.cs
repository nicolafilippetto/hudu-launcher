namespace Launcher.Server.Security;

/// <summary>
/// Configurazione di avvio, letta da variabili d'ambiente (es. <c>Launcher__DataDirectory</c>).
/// Tutto il resto si configura dalla sezione admin del portale.
/// </summary>
public sealed class LauncherOptions
{
    public const string Section = "Launcher";

    /// <summary>Cartella persistente: database e chiavi di Data Protection.</summary>
    public string DataDirectory { get; set; } = "/data";

    /// <summary>File con la chiave master (32 byte in base64), tipicamente un segreto Docker.</summary>
    public string? MasterKeyFile { get; set; }

    /// <summary>Chiave master in base64, alternativa al file. Sconsigliata in produzione.</summary>
    public string? MasterKey { get; set; }

    /// <summary>Proxy fidati (IP o CIDR separati da virgola), es. il WAF.</summary>
    public string? TrustedProxies { get; set; }

    /// <summary>Token per la configurazione iniziale. Se assente ne viene generato uno nei log.</summary>
    public string? SetupToken { get; set; }

    /// <summary>
    /// Solo per prove interne prima della pubblicazione: accetta HTTP senza proxy e usa cookie non
    /// "Secure". Il login con Microsoft 365 in HTTP non funziona (Entra lo consente solo su localhost).
    /// </summary>
    public bool AllowHttp { get; set; }
}
