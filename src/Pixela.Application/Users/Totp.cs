using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pixela.Application.Users;

/// <summary>TOTP (RFC 6238): HMAC-SHA1, 30 s, 6 dígitos — compatible con Google Authenticator, Authy, 1Password, etc.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string ComputeCode(string base32Secret, long step)
    {
        var key = Base32Decode(base32Secret) ?? throw new ArgumentException("Secreto TOTP inválido.", nameof(base32Secret));
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // RFC 6238/4226: las apps autenticadoras exigen HMAC-SHA1; no es un uso de seguridad por colisiones.
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Acepta el paso actual y ±1 (desfase de reloj). Un paso ya usado (<paramref name="lastUsedStep"/>) se rechaza.
    /// Devuelve el paso aceptado, o null.
    /// </summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        if (code is null)
        {
            return null;
        }

        var clean = code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (clean.Length != Digits || !clean.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        long? accepted = null;

        // Se recorren siempre las tres ventanas, sin cortar al acertar, para no filtrar nada por tiempo.
        for (var step = current - 1; step <= current + 1; step++)
        {
            var expected = ComputeCode(base32Secret, step);
            var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(clean));
            if (match && (lastUsedStep is null || step > lastUsedStep))
            {
                accepted = step;
            }
        }

        return accepted;
    }

    public static string ProvisioningUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[]? Base32Decode(string text)
    {
        var clean = text.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
