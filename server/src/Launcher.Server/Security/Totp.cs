using System.Security.Cryptography;
using System.Web;

namespace Launcher.Server.Security;

/// <summary>
/// Codici OTP a tempo (TOTP, RFC 6238) calcolati dal segreto salvato in Hudu. Il segreto non esce mai dal
/// portale: al Launcher arriva solo il codice del momento, valido per pochi secondi.
/// </summary>
public static class Totp
{
    /// <summary>
    /// Codice attuale e secondi di validità che restano; null se il segreto non è valido. Accetta il segreto
    /// in base32 (anche con spazi o trattini) o un indirizzo otpauth:// con algoritmo, cifre e periodo.
    /// </summary>
    public static (string Code, int SecondsLeft)? Compute(string? secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var text = secret.Trim();
        var digits = 6;
        var period = 30;
        var algorithm = HashAlgorithmName.SHA1;
        if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            text = query["secret"] ?? "";
            digits = int.TryParse(query["digits"], out var d) && d is >= 6 and <= 8 ? d : 6;
            period = int.TryParse(query["period"], out var p) && p is >= 15 and <= 120 ? p : 30;
            algorithm = (query["algorithm"] ?? "SHA1").ToUpperInvariant() switch
            {
                "SHA256" => HashAlgorithmName.SHA256,
                "SHA512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA1,
            };
        }

        if (Base32(text) is not { Length: > 0 } key)
        {
            return null;
        }

        var seconds = now.ToUnixTimeSeconds();
        var counter = seconds / period;
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        // HMAC-SHA1 è quello di RFC 6238 e delle app di autenticazione (quindi dei segreti in Hudu); in HMAC le
        // collisioni di SHA1 non contano.
#pragma warning disable CA5350
        var hash = algorithm == HashAlgorithmName.SHA256 ? HMACSHA256.HashData(key, message)
            : algorithm == HashAlgorithmName.SHA512 ? HMACSHA512.HashData(key, message)
            : HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = (binary % (int)Math.Pow(10, digits)).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        return (code, (int)(period - seconds % period));
    }

    private static byte[]? Base32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new string([.. value.ToUpperInvariant().Where(c => c is not (' ' or '-' or '='))]);
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
