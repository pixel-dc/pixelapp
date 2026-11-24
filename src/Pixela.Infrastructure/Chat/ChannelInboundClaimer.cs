using Pixela.Domain.Chat;
using Pixela.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Infrastructure.Chat;

/// <summary>
/// Reclama mensajes de canales de Meta listos para atender. <c>FOR UPDATE SKIP LOCKED</c> permite varios trabajadores sin que dos tomen el mismo
/// mensaje; también recupera los que quedaron en <c>Processing</c> cuando un trabajador murió a mitad de camino (su bloqueo venció).
/// </summary>
public sealed class ChannelInboundClaimer(AppDbContext db, TimeProvider clock)
{
    public async Task<List<Guid>> ClaimAsync(int batchSize, TimeSpan lockDuration, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.ChannelInbounds
            .FromSql($"""
                SELECT * FROM channel_inbound
                WHERE "Status" IN ('Pending', 'Processing')
                  AND "NextAttemptAt" <= {now}
                  AND ("LockedUntil" IS NULL OR "LockedUntil" < {now})
                ORDER BY "NextAttemptAt"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var row in batch)
        {
            row.Status = InboundStatus.Processing;
            row.LockedUntil = now + lockDuration;
            row.Attempts++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.ConvertAll(r => r.Id);
    }
}
