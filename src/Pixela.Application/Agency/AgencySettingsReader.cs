using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pixela.Application.Abstractions;
using Pixela.Domain.Agency;

namespace Pixela.Application.Agency;

/// <summary>Modelo, esfuerzo y precios con los que responde un bot: el modelo del bot manda sobre el de la agencia.</summary>
public sealed record ModelSettings(string Model, string Effort, decimal InputUsdPerMillion, decimal OutputUsdPerMillion)
{
    /// <summary>1 USD por millón equivale a 1 micro-USD por token: el costo en micros es tokens × precio.</summary>
    public long CostMicros(int tokensIn, int tokensOut) =>
        (long)Math.Ceiling((tokensIn * InputUsdPerMillion) + (tokensOut * OutputUsdPerMillion));
}

/// <summary>Lee la configuración de la agencia con un caché corto (cambia poco y se consulta en cada mensaje).</summary>
public sealed class AgencySettingsReader(IAppDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "agency-settings";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<AgencySettings> GetAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return await db.AgencySettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AgencySettings();
        }) ?? new AgencySettings();

    /// <summary>Tras guardar cambios en la configuración, para que se vean enseguida.</summary>
    public void Invalidate() => cache.Remove(CacheKey);

    public async Task<ModelSettings> ModelForAsync(string? botModel, CancellationToken ct)
    {
        var s = await GetAsync(ct);
        return new ModelSettings(string.IsNullOrWhiteSpace(botModel) ? s.DefaultModel : botModel, s.Effort, s.InputUsdPerMillion, s.OutputUsdPerMillion);
    }
}
