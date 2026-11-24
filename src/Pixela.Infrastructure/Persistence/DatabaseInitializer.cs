using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Domain.Agency;

namespace Pixela.Infrastructure.Persistence;

/// <summary>Aplica las migraciones pendientes y crea la fila de configuración de la agencia si aún no existe.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agency_settings ("Id", "Name", "Texts", "DefaultModel", "Effort", "InputUsdPerMillion", "OutputUsdPerMillion", "ChatRetentionDays", "UpdatedAt")
            VALUES ({AgencySettings.SingletonId}, 'Pixel DC', jsonb_build_object(), 'claude-haiku-5-5', 'low', 0.10, 0.50, 90, {now})
            ON CONFLICT ("Id") DO NOTHING
            """,
            ct);
    }
}
