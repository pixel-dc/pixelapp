namespace Pixela.Application.Chat;

/// <param name="IsValid">Meta reconoce el token y no está vencido ni revocado.</param>
/// <param name="AppId">App de Meta a la que pertenece el token.</param>
/// <param name="Type">PAGE, USER, SYSTEM_USER… Messenger e Instagram solo envían con el token de una página (PAGE).</param>
/// <param name="ExpiresAt">Null si no vence.</param>
public sealed record MetaTokenInfo(bool IsValid, string? AppId, string? Type, DateTimeOffset? ExpiresAt);

/// <summary>Suscripción de webhook de la app a un tipo de objeto de Meta (<paramref name="Topic"/>: whatsapp_business_account, page, instagram).</summary>
public sealed record MetaAppSubscription(string Topic, string? CallbackUrl, bool Active, IReadOnlyList<string> Fields);

/// <param name="Overrides">Direcciones de webhook propias de este número o de su WABA (nivel → URL). Si existen, mandan sobre la de la app.</param>
public sealed record MetaPhoneInfo(string? DisplayPhoneNumber, string? Status, IReadOnlyDictionary<string, string> Overrides);

/// <param name="Fields">Campos a los que está suscrita; vacío en un WABA (Meta no los lista ahí).</param>
public sealed record MetaSubscribedApp(string AppId, IReadOnlyList<string> Fields);

/// <summary>
/// Administración de la conexión con Meta (Graph): leer y corregir lo que antes se hacía a mano en Meta for Developers y Business Suite.
/// Cada método pide solo lo mínimo y nunca deja un secreto en la URL.
/// </summary>
public interface IMetaAdmin
{
    /// <exception cref="MetaAdminException">Meta no pudo responder (red, permisos o respuesta inválida).</exception>
    Task<MetaTokenInfo> DebugTokenAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<MetaAppSubscription>> GetAppSubscriptionsAsync(string appId, string appSecret, CancellationToken ct);

    /// <summary>Fija la dirección del webhook de la app para un tipo de objeto. Meta la verifica llamando a <paramref name="callbackUrl"/> con el token de verificación.</summary>
    Task SetAppSubscriptionAsync(string appId, string appSecret, string metaObject, string callbackUrl, string verifyToken, IReadOnlyList<string> fields, CancellationToken ct);

    Task<MetaPhoneInfo> GetPhoneAsync(string phoneNumberId, string token, CancellationToken ct);

    /// <summary>Fija la dirección de webhook propia del número (manda sobre la de la app).</summary>
    Task SetPhoneWebhookAsync(string phoneNumberId, string token, string callbackUrl, string verifyToken, CancellationToken ct);

    /// <summary>Apps suscritas a un WABA o a una página.</summary>
    Task<IReadOnlyList<MetaSubscribedApp>> GetSubscribedAppsAsync(string accountId, string token, CancellationToken ct);

    /// <summary>Suscribe la app del token a un WABA o a una página. <paramref name="fields"/> solo aplica a páginas.</summary>
    Task SubscribeAppAsync(string accountId, string token, IReadOnlyList<string>? fields, CancellationToken ct);

    /// <summary>Token de la página, a partir de un token de usuario o de usuario del sistema que tenga la página asignada. Null si Meta no lo da.</summary>
    Task<string?> GetPageAccessTokenAsync(string pageId, string token, CancellationToken ct);
}

/// <summary>Meta no aceptó una llamada de administración. El mensaje es el de Meta (permisos, parámetros…), sin datos de contactos.</summary>
/// <param name="Code">Código de error de Meta, si lo dio.</param>
public sealed class MetaAdminException(string message, int? code = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Code { get; } = code;
}
