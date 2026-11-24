using Pixela.Application.Abstractions;
using Pixela.Application.Chat;

namespace Pixela.Workers;

/// <summary>Lee en segundo plano las páginas web (y catálogos de Supabase) que se añaden como conocimiento de su chatbot.</summary>
internal sealed class KnowledgeIndexWorker(IServiceScopeFactory scopes, IWebPageFetcher fetcher, ICatalogFetcher catalogs, ILogger<KnowledgeIndexWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexador de conocimiento iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool worked;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
                    var knowledge = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
                    await knowledge.QueueDueCatalogRefreshesAsync(stoppingToken);
                    worked = await knowledge.IndexNextPendingUrlAsync(fetcher, stoppingToken)
                        || await knowledge.IndexNextPendingCatalogAsync(catalogs, stoppingToken);
                }

                if (!worked)
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
                logger.LogError(ex, "Error al indexar conocimiento");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
