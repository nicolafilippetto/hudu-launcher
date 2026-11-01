using System.Text.Json;
using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Microsoft.AspNetCore.DataProtection;

namespace Launcher.Server.Connections;

/// <summary>Credenziale proponibile per l'accesso a una pagina web: solo nome e utente, mai la password.</summary>
public sealed record WebLoginCandidate(int Id, string Name, string? Username);

/// <summary>
/// Dati per compilare l'accesso di una pagina web nel Launcher: le credenziali candidate e
/// un'autorizzazione firmata, valida per quella scheda, con cui il Launcher chiede una sola
/// password quando il tecnico la sceglie.
/// </summary>
public sealed record WebLoginInfo(string Grant, IReadOnlyList<WebLoginCandidate> Candidates);

/// <summary>Contenuto dell'autorizzazione: chi, da dove, quale asset e connessione, quali credenziali.</summary>
public sealed record WebLoginGrant(string Email, string AuthMethod, string? ClientIp, int AssetId, string ConnectionKey, IReadOnlyList<int> CredentialIds);

/// <summary>
/// Compilazione dell'accesso alle pagine web. La password non arriva con la connessione: il Launcher
/// la chiede solo per la credenziale scelta, il portale la legge da Hudu in quel momento (la chiave di
/// Hudu non esce dal server) e ogni richiesta finisce nel registro.
/// </summary>
public sealed class WebLoginService(IDataProtectionProvider dataProtection, TimeProvider time)
{
    /// <summary>Quanto vale l'autorizzazione di una scheda: come una giornata di lavoro.</summary>
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(10);

    public const int MaxCandidates = 20;

    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector("Launcher.WebLogin.v1").ToTimeLimitedDataProtector();

    public string Protect(WebLoginGrant grant) =>
        Protector.Protect(JsonSerializer.Serialize(grant), time.GetUtcNow().Add(GrantLifetime));

    /// <summary>Autorizzazione valida e non scaduta, oppure null.</summary>
    public WebLoginGrant? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            return null;
        }

        try
        {
            var json = Protector.Unprotect(value, out var expiration);
            return expiration < time.GetUtcNow() ? null : JsonSerializer.Deserialize<WebLoginGrant>(json);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Credenziali proposte per una pagina web: solo quelle collegate all'asset in Hudu (assegnate o
    /// related), prima quella scelta per la connessione, poi le assegnate e le related. Le altre
    /// password del cliente non si propongono: i collegamenti si sistemano in Hudu.
    /// </summary>
    public static IReadOnlyList<CachedPassword> Candidates(ResolvedAsset asset, ResolvedConnection connection, IReadOnlyList<CachedPassword> companyPasswords)
    {
        var linked = ConnectionResolver.LinkedPasswords(asset.Asset, ConnectionResolver.IndexPasswordsByAsset(companyPasswords));
        return linked
            .OrderBy(p => p.Id == connection.PasswordId ? 0 : 1)
            .Take(MaxCandidates)
            .ToList();
    }
}
