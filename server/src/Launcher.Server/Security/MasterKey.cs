using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Launcher.Server.Security;

/// <summary>
/// Chiave master che cifra le chiavi di Data Protection su disco. Senza di essa il database
/// e la cartella dati non bastano a leggere i segreti (chiave Hudu, token RustDesk, ecc.).
/// </summary>
public sealed class MasterKey
{
    public const int Length = 32;

    private readonly byte[] _key;

    public MasterKey(byte[] key)
    {
        if (key.Length != Length)
        {
            throw new InvalidOperationException($"La chiave master deve essere di {Length} byte (base64 di 32 byte casuali).");
        }

        _key = key;
    }

    public static MasterKey Load(LauncherOptions options, bool isDevelopment)
    {
        string? encoded = null;
        if (!string.IsNullOrWhiteSpace(options.MasterKeyFile))
        {
            if (!File.Exists(options.MasterKeyFile))
            {
                throw new InvalidOperationException($"File della chiave master non trovato: {options.MasterKeyFile}");
            }

            try
            {
                encoded = File.ReadAllText(options.MasterKeyFile).Trim();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"Il file della chiave master {options.MasterKeyFile} non è leggibile dall'utente del container (uid 1654). " +
                    "Sul server: chown 1654:1654 <file> && chmod 400 <file>.", ex);
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.MasterKey))
        {
            encoded = options.MasterKey.Trim();
        }
        else if (isDevelopment)
        {
            var devFile = Path.Combine(options.DataDirectory, "dev-master.key");
            if (!File.Exists(devFile))
            {
                File.WriteAllText(devFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(Length)));
            }

            encoded = File.ReadAllText(devFile).Trim();
        }

        if (encoded is null)
        {
            throw new InvalidOperationException(
                "Chiave master mancante. Impostare Launcher__MasterKeyFile (consigliato, segreto Docker) " +
                "o Launcher__MasterKey con 32 byte casuali in base64, es. 'openssl rand -base64 32'.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("La chiave master non è base64 valido.", ex);
        }

        return new MasterKey(key);
    }

    public byte[] Encrypt(byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return [.. nonce, .. tag, .. ciphertext];
    }

    public byte[] Decrypt(byte[] payload, byte[] associatedData)
    {
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (payload.Length < nonceSize + tagSize)
        {
            throw new CryptographicException("Payload cifrato troppo corto.");
        }

        var nonce = payload.AsSpan(0, nonceSize);
        var tag = payload.AsSpan(nonceSize, tagSize);
        var ciphertext = payload.AsSpan(nonceSize + tagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}

/// <summary>Cifra con la chiave master le chiavi di Data Protection salvate su disco.</summary>
public sealed class MasterKeyXmlEncryptor(MasterKey masterKey) : IXmlEncryptor
{
    internal static readonly byte[] AssociatedData = "Launcher.DataProtection.KeyRing.v1"u8.ToArray();

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var payload = masterKey.Encrypt(plaintext, AssociatedData);
        var element = new XElement("encryptedKey",
            new XComment(" Cifrata con la chiave master del Launcher (AES-256-GCM) "),
            new XElement("value", Convert.ToBase64String(payload)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Decifra le chiavi di Data Protection. Istanziata da Data Protection tramite IServiceProvider.</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var masterKey = services.GetRequiredService<MasterKey>();
        var value = encryptedElement.Element("value")?.Value
            ?? throw new CryptographicException("Elemento cifrato senza valore.");
        var plaintext = masterKey.Decrypt(Convert.FromBase64String(value), MasterKeyXmlEncryptor.AssociatedData);
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
    }
}
