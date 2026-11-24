using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Common;

namespace Pixela.Application.Chat;

/// <summary>
/// Lo que la agencia necesita para conectar la app de Meta de un bot: la URL del webhook y el token de verificación (se copian en Meta)
/// y si ya guardó la clave secreta de la app (nunca se devuelve: solo sus últimos caracteres).
/// </summary>
/// <param name="WebhookUrl">Dirección completa si la agencia configuró su dirección pública; si no, solo la ruta.</param>
public sealed record MetaAppView(string WebhookUrl, string VerifyToken, bool HasAppSecret, string? AppSecretHint, DateTimeOffset UpdatedAt);

public sealed record SetMetaAppSecretRequest(string? AppSecret);

/// <summary>App de Meta de cada bot (cada bot trae la suya): clave secreta y token de verificación del webhook, cifrados.</summary>
public sealed class BotMetaAppService(IAppDbContext db, ISecretProtector secrets, AgencySettingsReader agency, TimeProvider clock)
{
    public const string WebhookPath = "/meta/webhook/";

    public async Task<ServiceResult<MetaAppView>> GetAsync(Guid botId, CancellationToken ct)
    {
        var row = await db.Bots.AsNoTracking().Where(b => b.Id == botId)
            .Join(db.BotSecrets, b => b.Id, s => s.BotId, (b, s) => new { b.PublicKey, s.MetaAppSecretEncrypted, s.MetaAppSecretHint, s.MetaVerifyTokenEncrypted, s.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return ServiceResult<MetaAppView>.Fail("not_found", "Chatbot no encontrado.");
        }

        var baseUrl = (await agency.GetAsync(ct)).PublicBaseUrl?.TrimEnd('/');
        var has = row.MetaAppSecretEncrypted is { Length: > 0 };
        return ServiceResult<MetaAppView>.Ok(new MetaAppView(
            (baseUrl ?? "") + WebhookPath + row.PublicKey, secrets.Unprotect(row.MetaVerifyTokenEncrypted), has, has ? row.MetaAppSecretHint : null, row.UpdatedAt));
    }

    public async Task<ServiceResult<MetaAppView>> SetAppSecretAsync(Guid botId, SetMetaAppSecretRequest request, CancellationToken ct)
    {
        var secret = request.AppSecret?.Trim();
        if (secret is not { Length: >= 16 and <= 200 } || secret.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return ServiceResult<MetaAppView>.Fail(
                "validation_error", "Pega la clave secreta de la app de Meta completa (sin espacios).", new Dictionary<string, string> { ["app_secret"] = "Clave secreta no válida." });
        }

        var s = await db.BotSecrets.FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (s is null)
        {
            return ServiceResult<MetaAppView>.Fail("not_found", "Chatbot no encontrado.");
        }

        s.MetaAppSecretEncrypted = secrets.Protect(secret);
        s.MetaAppSecretHint = secret[^4..];
        s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }

    /// <summary>Quita la clave secreta: el webhook del bot deja de aceptar mensajes hasta que se ponga otra.</summary>
    public async Task<ServiceResult<MetaAppView>> ClearAppSecretAsync(Guid botId, CancellationToken ct)
    {
        var s = await db.BotSecrets.FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (s is null)
        {
            return ServiceResult<MetaAppView>.Fail("not_found", "Chatbot no encontrado.");
        }

        s.MetaAppSecretEncrypted = null;
        s.MetaAppSecretHint = null;
        s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }

    /// <summary>Genera un token de verificación nuevo (por ejemplo, si se filtró). Hay que copiarlo otra vez en Meta.</summary>
    public async Task<ServiceResult<MetaAppView>> RegenerateVerifyTokenAsync(Guid botId, CancellationToken ct)
    {
        var s = await db.BotSecrets.FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (s is null)
        {
            return ServiceResult<MetaAppView>.Fail("not_found", "Chatbot no encontrado.");
        }

        s.MetaVerifyTokenEncrypted = secrets.Protect(BotService.NewVerifyToken());
        s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }
}
