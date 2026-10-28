using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Launcher.Server.Security;

/// <summary>Certificato del portale per Entra: impronta e scadenza, per mostrarli agli admin.</summary>
public sealed record EntraCertificateInfo(string Thumbprint, DateTimeOffset NotAfter, byte[] RawData)
{
    public bool ExpiresSoon(TimeProvider time) => NotAfter - time.GetUtcNow() < TimeSpan.FromDays(30);
}

/// <summary>
/// Credenziale dell'app verso Entra ID con certificato invece del segreto: il portale genera una
/// coppia di chiavi, la chiave privata resta cifrata nel database e non esce mai; in Entra si carica
/// solo il certificato pubblico. Al login il portale si presenta con un'asserzione firmata (JWT
/// PS256 con l'impronta SHA-256 del certificato), valida pochi minuti.
/// </summary>
public static class EntraCredential
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Nuovo certificato autofirmato (RSA 3072, 2 anni): certificato e chiave PKCS#8 in Base64.</summary>
    public static (string Certificate, string PrivateKey) Generate(string subject, TimeProvider time)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + subject), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var now = time.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));
        return (Convert.ToBase64String(certificate.RawData), Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
    }

    public static EntraCertificateInfo? Describe(string? certificate)
    {
        if (string.IsNullOrEmpty(certificate))
        {
            return null;
        }

        try
        {
            var raw = Convert.FromBase64String(certificate);
            using var parsed = X509CertificateLoader.LoadCertificate(raw);
            return new EntraCertificateInfo(parsed.Thumbprint, parsed.NotAfter.ToUniversalTime(), raw);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Asserzione per l'endpoint dei token di Entra (al posto di client_secret).</summary>
    public static string CreateAssertion(string tenantId, string clientId, string certificate, string privateKey, TimeProvider time)
    {
        var raw = Convert.FromBase64String(certificate);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        var key = new RsaSecurityKey(rsa) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        var now = time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSsaPssSha256),
            AdditionalHeaderClaims = new Dictionary<string, object> { ["x5t#S256"] = Base64UrlEncoder.Encode(SHA256.HashData(raw)) },
        });
    }
}
