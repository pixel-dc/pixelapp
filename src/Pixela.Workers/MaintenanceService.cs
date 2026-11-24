using Pixela.Infrastructure.Maintenance;
using Microsoft.Extensions.Options;

namespace Pixela.Workers;

/// <summary>Aplica la política de retención de datos periódicamente.</summary>
internal sealed class MaintenanceService(MaintenanceRunner runner, IOptions<MaintenanceOptions> options, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Servicio de retención de datos iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunOnceAsync(stoppingToken);
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en el ciclo de retención");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
