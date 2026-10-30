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
    /// Credenziali proposte per una pagina web, in quest'ordine: quella scelta per la connessione,
    /// quelle collegate all'asset in Hudu, quelle del cliente con l'URL sullo stesso host della pagina.
    /// </summary>
    public static IReadOnlyList<CachedPassword> Candidates(ResolvedAsset asset, ResolvedConnection connection, IReadOnlyList<CachedPassword> companyPasswords)
    {
        var result = new List<CachedPassword>();
        void Add(CachedPassword? password)
        {
            if (password is not null && password.CompanyId == asset.Asset.CompanyId && result.All(p => p.Id != password.Id))
            {
                result.Add(password);
            }
        }

        Add(companyPasswords.FirstOrDefault(p => p.Id == connection.PasswordId));
        foreach (var password in companyPasswords.Where(p => p.AssetId == asset.Asset.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Add(password);
        }

        if (connection.Host is { } host)
        {
            foreach (var password in companyPasswords.Where(p => HostParser.ExtractHost(p.Url) is { } urlHost && SameHost(urlHost, host))
                         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Add(password);
            }
        }

        return result.Take(MaxCandidates).ToList();
    }

    private static bool SameHost(string a, string b) =>
        System.Net.IPAddress.TryParse(a, out var ipA) && System.Net.IPAddress.TryParse(b, out var ipB)
            ? ipA.Equals(ipB)
            : string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
