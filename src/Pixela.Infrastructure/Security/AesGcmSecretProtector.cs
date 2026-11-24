using System.Security.Cryptography;
using System.Text;
using Pixela.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Security;

public sealed class SecretsOptions
{
    public const string Section = "Secrets";

    /// <summary>Clave maestra AES-256 en base64 (32 bytes). Debe vivir fuera de la base de datos.</summary>
    public string MasterKey { get; set; } = string.Empty;
}

/// <summary>AES-256-GCM. Formato: v1.{base64(nonce[12] | tag[16] | ciphertext)}. Detecta manipulación.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretsOptions> options)
    {
        _key = ParseKey(options.Value.MasterKey);
    }

    public byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt: [], info: Encoding.UTF8.GetBytes("pixela|" + purpose));

    public static bool IsValidKey(string? base64) => TryParseKey(base64, out _);

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return Version + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Formato de secreto no reconocido.");
        }

        var payload = Convert.FromBase64String(protectedValue[Version.Length..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Secreto truncado.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ParseKey(string? base64) =>
        TryParseKey(base64, out var key)
            ? key
            : throw new InvalidOperationException("Secrets:MasterKey debe ser una clave de 32 bytes en base64.");

    private static bool TryParseKey(string? base64, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(base64);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
