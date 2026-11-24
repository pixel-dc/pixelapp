using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;

namespace Pixela.Application.Chat;

public enum AiKeyCheck
{
    Valid,
    Rejected,
    Unavailable,
}

/// <summary>Comprueba una llave de Claude con una llamada mínima.</summary>
public interface IAiKeyTester
{
    Task<AiKeyCheck> TestAsync(string key, CancellationToken ct);
}

/// <summary>Estado de la llave de Claude de un bot. La llave nunca se devuelve: solo sus últimos caracteres.</summary>
public sealed record AiKeyView(bool Configured, string? Hint, DateTimeOffset? SetAt);

public sealed record SetAiKeyRequest(string? Key);

public sealed record TestAiKeyRequest(string? Key);

/// <summary>
/// Llave de Claude de cada bot (una por bot, sin respaldo global): así Pixel DC ve el consumo de cada bot en la consola de Anthropic y lo
/// cuadra con el de Pixela. Sin llave, el bot responde en modo extractivo (el fragmento más relevante, sin modelo y sin costo).
/// Quién puede tocarla (solo la agencia) lo decide el endpoint; aquí rige el filtro por cliente.
/// </summary>
public sealed class BotKeyService(IAppDbContext db, ISecretProtector secrets, IAiKeyTester tester, TimeProvider clock)
{
    private const string Prefix = "sk-ant-";

    /// <summary>La llave del bot ya descifrada. Null = el bot no tiene llave y se atiende sin modelo.</summary>
    public async Task<string?> ResolveAsync(Guid botId, CancellationToken ct)
    {
        var stored = await db.BotSecrets.AsNoTracking().Where(s => s.BotId == botId).Select(s => s.ClaudeApiKeyEncrypted).FirstOrDefaultAsync(ct);
        return stored is { Length: > 0 } ? secrets.Unprotect(stored) : null;
    }

    public async Task<ServiceResult<AiKeyView>> GetAsync(Guid botId, CancellationToken ct)
    {
        var s = await db.BotSecrets.AsNoTracking().Where(x => x.BotId == botId)
            .Select(x => new { x.ClaudeApiKeyEncrypted, x.ClaudeApiKeyHint, x.ClaudeApiKeySetAt }).FirstOrDefaultAsync(ct);
        if (s is null)
        {
            return ServiceResult<AiKeyView>.Fail("not_found", "Chatbot no encontrado.");
        }

        var configured = s.ClaudeApiKeyEncrypted is { Length: > 0 };
        return ServiceResult<AiKeyView>.Ok(new AiKeyView(configured, configured ? s.ClaudeApiKeyHint : null, configured ? s.ClaudeApiKeySetAt : null));
    }

    public async Task<ServiceResult<AiKeyView>> SetAsync(Guid botId, SetAiKeyRequest request, CancellationToken ct)
    {
        var key = Normalize(request.Key);
        if (key is null)
        {
            return InvalidKey<AiKeyView>();
        }

        var s = await db.BotSecrets.FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (s is null)
        {
            return ServiceResult<AiKeyView>.Fail("not_found", "Chatbot no encontrado.");
        }

        s.ClaudeApiKeyEncrypted = secrets.Protect(key);
        s.ClaudeApiKeyHint = key[^4..];
        s.ClaudeApiKeySetAt = s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }

    public async Task<ServiceResult<AiKeyView>> ClearAsync(Guid botId, CancellationToken ct)
    {
        var s = await db.BotSecrets.FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (s is null)
        {
            return ServiceResult<AiKeyView>.Fail("not_found", "Chatbot no encontrado.");
        }

        s.ClaudeApiKeyEncrypted = null;
        s.ClaudeApiKeyHint = null;
        s.ClaudeApiKeySetAt = null;
        s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }

    /// <summary>Prueba la llave dada o, sin ella, la guardada en el bot.</summary>
    public async Task<ServiceResult<AiKeyCheck>> TestAsync(Guid botId, TestAiKeyRequest request, CancellationToken ct)
    {
        string? key;
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            key = await ResolveAsync(botId, ct);
            if (key is null)
            {
                return ServiceResult<AiKeyCheck>.Fail("invalid_state", "Este chatbot no tiene una llave guardada para probar.");
            }
        }
        else
        {
            key = Normalize(request.Key);
            if (key is null)
            {
                return InvalidKey<AiKeyCheck>();
            }
        }

        return ServiceResult<AiKeyCheck>.Ok(await tester.TestAsync(key, ct));
    }

    private static ServiceResult<T> InvalidKey<T>() =>
        ServiceResult<T>.Fail("validation_error", "La llave debe empezar por sk-ant- y no tener espacios.", new Dictionary<string, string> { ["key"] = "Llave de Claude no válida." });

    private static string? Normalize(string? raw)
    {
        var key = raw?.Trim();
        return key is { Length: >= 20 and <= 300 } && key.StartsWith(Prefix, StringComparison.Ordinal) && !key.Any(char.IsWhiteSpace) ? key : null;
    }
}
