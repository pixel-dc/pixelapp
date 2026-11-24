using Microsoft.Extensions.Configuration;
using Pixela.Infrastructure.Security;

namespace Pixela.Infrastructure;

public static class StartupChecks
{
    /// <summary>
    /// Lo único que Pixela necesita fuera de la base: la llave maestra y la conexión. Sin ellas el servicio no arranca, también en
    /// desarrollo, para no guardar nunca un secreto cifrado con una llave que luego no exista.
    /// </summary>
    public static void Validate(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default")))
        {
            throw new InvalidOperationException("Configure ConnectionStrings__Default (conexión a PostgreSQL).");
        }

        if (!AesGcmSecretProtector.IsValidKey(configuration["Secrets:MasterKey"]))
        {
            throw new InvalidOperationException("Configure Secrets__MasterKey: 32 bytes en base64 (openssl rand -base64 32).");
        }
    }
}
