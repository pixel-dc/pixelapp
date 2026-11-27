using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;

namespace Pixela.Infrastructure.Push;

/// <summary>
/// Cola en memoria de avisos y su envío en segundo plano, en el mismo proceso que guardó el mensaje (la API para el chat web, los procesos de
/// fondo para los canales de Meta). Si el proceso se reinicia con avisos en cola, esos se pierden: el mensaje sigue en la bandeja.
/// </summary>
public sealed class ConversationAlertSender(IServiceScopeFactory scopes, ILogger<ConversationAlertSender> logger) : BackgroundService, IConversationAlerts
{
    private readonly Channel<ConversationAlert> queue = Channel.CreateBounded<ConversationAlert>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Publish(ConversationAlert alert) => queue.Writer.TryWrite(alert);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
                await scope.ServiceProvider.GetRequiredService<ConversationAlertDelivery>().DeliverAsync(alert, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "No se pudo enviar el aviso de la conversación {Conversation}", alert.ConversationId);
            }
        }
    }
}
