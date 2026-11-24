using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

/// <summary>Consumo de IA de un periodo. El costo está en millonésimas de dólar y es una estimación con el precio por token configurado.</summary>
public sealed record BotUsageTotals(long Replies, long TokensIn, long TokensOut, long CostMicros);

public sealed record BotUsageChannel(string Channel, long Replies, long TokensIn, long TokensOut, long CostMicros);

public sealed record BotUsageDay(string Day, long Replies, long TokensIn, long TokensOut, long CostMicros);

/// <param name="Month">Mes en curso (UTC).</param>
/// <param name="Last30Days">Últimos 30 días.</param>
/// <param name="Retained">Todo lo que se conserva (los mensajes se purgan pasado el plazo de retención).</param>
/// <param name="InputUsdPerMillion">Precio por millón de tokens de entrada con el que se estima el costo.</param>
public sealed record BotUsageView(
    BotUsageTotals Month, [property: System.Text.Json.Serialization.JsonPropertyName("last_30_days")] BotUsageTotals Last30Days, BotUsageTotals Retained, int RetentionDays,
    IReadOnlyList<BotUsageChannel> ByChannel, IReadOnlyList<BotUsageDay> Days, decimal InputUsdPerMillion, decimal OutputUsdPerMillion);

/// <summary>
/// Cuánto consume cada chatbot, calculado de los mensajes del bot (tokens y costo se guardan en cada respuesta con IA).
/// Los totales por bot y mes viven aparte en <c>chat_usage</c> y sobreviven a la purga; este resumen por bot cubre el plazo de retención.
/// </summary>
public sealed class BotUsageService(IAppDbContext db, AgencySettingsReader agency, TimeProvider clock)
{
    public async Task<ServiceResult<BotUsageView>> GetAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<BotUsageView>.Fail("not_found", "Chatbot no encontrado.");
        }

        var rows = await db.ChatMessages.AsNoTracking()
            .Where(m => (m.TokensIn > 0 || m.TokensOut > 0))
            .Join(db.Conversations.Where(c => c.BotId == botId), m => m.ConversationId, c => c.Id,
                (m, c) => new { m.CreatedAt, m.TokensIn, m.TokensOut, m.CostMicros, c.ChannelId })
            .ToListAsync(ct);

        var channelIds = rows.Where(r => r.ChannelId != null).Select(r => r.ChannelId!.Value).Distinct().ToList();
        var kinds = await db.Channels.AsNoTracking().Where(ch => channelIds.Contains(ch.Id)).ToDictionaryAsync(ch => ch.Id, ch => ch.Kind, ct);

        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var since = now.AddDays(-30);

        static BotUsageTotals Sum(IEnumerable<(long In, long Out, long Cost)> items) =>
            new(items.Count(), items.Sum(i => i.In), items.Sum(i => i.Out), items.Sum(i => i.Cost));

        var items = rows.Select(r => (r.CreatedAt, Channel: r.ChannelId is { } id && kinds.TryGetValue(id, out var k) ? k.ToString().ToLowerInvariant() : "web", In: (long)r.TokensIn, Out: (long)r.TokensOut, Cost: r.CostMicros)).ToList();

        var byChannel = items.GroupBy(i => i.Channel)
            .Select(g => new BotUsageChannel(g.Key, g.Count(), g.Sum(i => i.In), g.Sum(i => i.Out), g.Sum(i => i.Cost)))
            .OrderByDescending(c => c.CostMicros).ToList();

        var days = items.Where(i => i.CreatedAt >= since.Date).GroupBy(i => i.CreatedAt.UtcDateTime.Date)
            .OrderBy(g => g.Key)
            .Select(g => new BotUsageDay(g.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), g.Count(), g.Sum(i => i.In), g.Sum(i => i.Out), g.Sum(i => i.Cost))).ToList();

        var pricing = await agency.GetAsync(ct);
        return ServiceResult<BotUsageView>.Ok(new BotUsageView(
            Sum(items.Where(i => i.CreatedAt >= monthStart).Select(i => (i.In, i.Out, i.Cost))),
            Sum(items.Where(i => i.CreatedAt >= since).Select(i => (i.In, i.Out, i.Cost))),
            Sum(items.Select(i => (i.In, i.Out, i.Cost))),
            Math.Max(1, pricing.ChatRetentionDays), byChannel, days, pricing.InputUsdPerMillion, pricing.OutputUsdPerMillion));
    }
}
