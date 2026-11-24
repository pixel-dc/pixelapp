using System.Security.Cryptography;
using System.Text;
using Pixela.Application.Users;
using Konscious.Security.Cryptography;

namespace Pixela.Infrastructure.Security;

/// <summary>Argon2id (parámetros mínimos recomendados por OWASP: 19 MiB, 2 iteraciones, 1 hilo). Formato PHC.</summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKiB = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "$argon2id$v=19$";

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism, HashSize);
        return $"{Prefix}m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        if (!TryParse(hash, out var m, out var t, out var p, out var salt, out var expected))
        {
            return false;
        }

        var actual = Compute(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string hash) =>
        !TryParse(hash, out var m, out var t, out var p, out _, out _) || m < MemoryKiB || t < Iterations || p != Parallelism;

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int size)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(size);
    }

    private static bool TryParse(string hash, out int m, out int t, out int p, out byte[] salt, out byte[] expected)
    {
        (m, t, p, salt, expected) = (0, 0, 0, [], []);
        if (!hash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = hash[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        var parameters = parts[0].Split(',');
        if (parameters.Length != 3
            || !int.TryParse(parameters[0].AsSpan(2), out m) || !parameters[0].StartsWith("m=", StringComparison.Ordinal)
            || !int.TryParse(parameters[1].AsSpan(2), out t) || !parameters[1].StartsWith("t=", StringComparison.Ordinal)
            || !int.TryParse(parameters[2].AsSpan(2), out p) || !parameters[2].StartsWith("p=", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        // Tope sobre parámetros leídos de la base: un hash manipulado no debe poder agotar la memoria.
        return m is >= 8 and <= 262144 && t is >= 1 and <= 10 && p is >= 1 and <= 8 && salt.Length >= 8 && expected.Length >= 16;
    }
}
