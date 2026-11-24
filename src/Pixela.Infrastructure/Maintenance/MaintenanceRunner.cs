using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Domain.Agency;
using Pixela.Domain.Chat;
using Pixela.Infrastructure.Persistence;

namespace Pixela.Infrastructure.Maintenance;

public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";

    public int BatchSize { get; set; } = 5000;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Retención de datos del chat: conversaciones inactivas (según la agencia), archivos vencidos y registro de mensajes recibidos de Meta.
/// Los contactos (con consentimiento) y el consumo mensual por bot se conservan.
/// </summary>
public sealed class MaintenanceRunner(
    IServiceScopeFactory scopes,
    IOptions<MaintenanceOptions> options,
    IOptions<MetaOptions> metaOptions,
    IAttachmentStore attachmentStore,
    TimeProvider clock,
    ILogger<MaintenanceRunner> logger)
{
    /// <summary>Corre una pasada completa. Devuelve cuántas conversaciones borró.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Conversaciones inactivas más allá de la retención de la agencia: se borran con sus mensajes y archivos; el contacto queda sin enlace.
        var settings = await db.AgencySettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AgencySettings();
        var chatCutoff = now.AddDays(-Math.Max(1, settings.ChatRetentionDays));
        var conversations = await DeleteInBatchesAsync(
            () => db.Conversations.Where(c => c.LastMessageAt < chatCutoff).OrderBy(c => c.LastMessageAt).Select(c => c.Id).Take(o.BatchSize).ToListAsync(ct),
            async ids =>
            {
                foreach (var f in await db.ChatAttachments.Where(a => ids.Contains(a.ConversationId)).Select(a => new { a.ClientId, a.Id }).ToListAsync(ct))
                {
                    attachmentStore.Delete(f.ClientId, f.Id);
                }

                await db.ChatAttachments.Where(a => ids.Contains(a.ConversationId)).ExecuteDeleteAsync(ct);
                await db.ChatMessages.Where(m => ids.Contains(m.ConversationId)).ExecuteDeleteAsync(ct);
                await db.Leads.Where(l => l.ConversationId != null && ids.Contains(l.ConversationId.Value))
                    .ExecuteUpdateAsync(u => u.SetProperty(l => l.ConversationId, (Guid?)null), ct);
                return await db.Conversations.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
            },
            ct);

        // Archivos que enviaron los contactos: pasado el plazo se borra el contenido del disco y la fila queda como «vencido».
        var expired = await db.ChatAttachments.Where(a => a.Stored && a.ExpiresAt < now).Select(a => new { a.Id, a.ClientId }).Take(o.BatchSize).ToListAsync(ct);
        foreach (var f in expired)
        {
            attachmentStore.Delete(f.ClientId, f.Id);
        }

        if (expired.Count > 0)
        {
            var expiredIds = expired.Select(f => f.Id).ToList();
            await db.ChatAttachments.Where(a => expiredIds.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.Stored, false), ct);
        }

        // Barrido de archivos huérfanos (conversación o cliente borrados): nada se conserva más del plazo, con un margen.
        attachmentStore.SweepOlderThan(now.AddDays(-(Math.Max(1, metaOptions.Value.AttachmentRetentionDays) + 2)));

        // Registro de mensajes recibidos por Meta: se conserva un poco más que el plazo de reintentos de Meta (descarta repetidos).
        var inboundCutoff = now.AddDays(-Math.Max(1, metaOptions.Value.InboundRetentionDays));
        await db.ChannelInbounds.Where(i => i.CreatedAt < inboundCutoff && (i.Status == InboundStatus.Done || i.Status == InboundStatus.Failed)).ExecuteDeleteAsync(ct);

        if (conversations > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Retención: {Conversations} conversaciones inactivas eliminadas", conversations);
        }

        return conversations;
    }

    private static async Task<int> DeleteInBatchesAsync<TKey>(Func<Task<List<TKey>>> selectBatch, Func<List<TKey>, Task<int>> deleteBatch, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var ids = await selectBatch();
            if (ids.Count == 0)
            {
                break;
            }

            total += await deleteBatch(ids);
        }

        return total;
    }
}
