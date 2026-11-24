using Pixela.Application.Chat;
using Pixela.Infrastructure.Chat;

namespace Pixela.Workers;

/// <summary>Atiende en segundo plano los mensajes que llegan por WhatsApp, Messenger e Instagram: los pasa por el chatbot y envía la respuesta.</summary>
internal sealed class MetaInboxWorker(IServiceScopeFactory scopes, ILogger<MetaInboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Atención de canales de Meta iniciada");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Guid> ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    ids = await scope.ServiceProvider.GetRequiredService<ChannelInboundClaimer>().ClaimAsync(BatchSize, LockDuration, stoppingToken);
                }

                foreach (var id in ids)
                {
                    await ProcessOneAsync(id, stoppingToken);
                }

                if (ids.Count == 0)
                {
                    await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error al reclamar mensajes de canales");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        try
        {
            // Un ámbito por mensaje: cada uno fija el cliente de su bot.
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<MetaInboxProcessor>().ProcessAsync(id, ct);
            if (outcome is InboundOutcome.Failed or InboundOutcome.Retry)
            {
                logger.LogWarning("Mensaje de canal {Id}: {Outcome}", id, outcome);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sin contenido del mensaje en el registro: puede traer datos personales. El bloqueo vence y se reintenta.
            logger.LogError(ex, "Error al atender el mensaje de canal {Id}", id);
        }
    }
}
