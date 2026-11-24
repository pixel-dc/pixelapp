using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pixela.Infrastructure;
using Pixela.Infrastructure.Security;

namespace Pixela.Infrastructure.Tests;

public sealed class SecretProtectorTests
{
    private static AesGcmSecretProtector Create(byte seed = 1) =>
        new(Options.Create(new SecretsOptions { MasterKey = Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()) }));

    [Fact]
    public void Cifra_y_descifra()
    {
        var protector = Create();

        var stored = protector.Protect("sk-ant-secreto");

        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("secreto", stored, StringComparison.Ordinal);
        Assert.Equal("sk-ant-secreto", protector.Unprotect(stored));
    }

    [Fact]
    public void Otra_llave_no_descifra() =>
        Assert.ThrowsAny<CryptographicException>(() => Create(2).Unprotect(Create(1).Protect("token")));

    [Fact]
    public void Detecta_manipulacion()
    {
        var protector = Create();
        var stored = protector.Protect("token");
        var bytes = Convert.FromBase64String(stored[3..]);
        bytes[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1." + Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData(null, "Host=localhost")]
    [InlineData("corta", "Host=localhost")]
    [InlineData("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=", null)]
    public void Sin_llave_maestra_o_conexion_no_arranca(string? masterKey, string? connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:MasterKey"] = masterKey,
            ["ConnectionStrings:Default"] = connection,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => StartupChecks.Validate(configuration));
    }
}
