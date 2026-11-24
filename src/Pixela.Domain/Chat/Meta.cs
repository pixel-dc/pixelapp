namespace Pixela.Domain.Chat;

public enum ChannelKind
{
    WhatsApp,
    Messenger,
    Instagram,
}

public enum ChannelStatus
{
    Active,
    Disabled,

    /// <summary>Meta rechazó el token (venció o se revocó): el negocio debe reconectar el canal.</summary>
    TokenError,
}

/// <summary>Número de WhatsApp, página de Facebook o cuenta de Instagram conectada al chatbot de un negocio.</summary>
public class Channel
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }
    public ChannelKind Kind { get; set; }

    /// <summary>Id que Meta pone en cada evento (la app es la del bot, ver <c>BotSecrets</c>): <c>phone_number_id</c>, id de la página o id de la cuenta de Instagram. Único por tipo.</summary>
    public required string ExternalId { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Token de acceso cifrado con <c>ISecretProtector</c>. Nunca se devuelve por la API.</summary>
    public required string AccessTokenEncrypted { get; set; }

    public ChannelStatus Status { get; set; } = ChannelStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum InboundStatus
{
    Pending,
    Processing,
    Done,
    Failed,
}

/// <summary>
/// Mensaje recibido por un canal de Meta, en cola hasta que un trabajador lo atienda. Meta exige responder el webhook en segundos y reintenta
/// los que fallan: guardarlo primero (con el id del mensaje único) evita perderlo y responderlo dos veces.
/// </summary>
public class ChannelInbound
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ChannelId { get; set; }

    /// <summary>Id del mensaje en Meta (<c>wamid</c>, <c>mid</c>): con el canal forma una clave única que descarta reintentos.</summary>
    public required string ExternalMessageId { get; set; }

    /// <summary>Teléfono, PSID o IGSID de quien escribe. Dato personal.</summary>
    public required string ContactId { get; set; }

    public string? ContactName { get; set; }

    /// <summary>Texto recibido; null si no era texto (audio, imagen…). Se borra al terminar.</summary>
    public string? Text { get; set; }

    /// <summary>Adjuntos recibidos (JSON con los ids o enlaces de Meta). Se borra al terminar.</summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>Respuesta ya generada, para reenviarla sin volver a preguntar al modelo si el envío falló a medias. Se borra al terminar.</summary>
    public string? ReplyText { get; set; }

    /// <summary>Partes de la respuesta que ya se enviaron (una respuesta larga se parte según el canal).</summary>
    public int SentParts { get; set; }

    public InboundStatus Status { get; set; } = InboundStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Motivo corto del último fallo o descarte. Nunca lleva contenido de mensajes.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// Archivo que un contacto envió por un canal de Meta. El contenido vive en el disco del servidor (no en la base) y se borra pasados unos días;
/// la fila queda hasta entonces para mostrarlo en la conversación.
/// </summary>
public class ChatAttachment
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>Mensaje de la conversación al que pertenece (el del contacto).</summary>
    public Guid? MessageId { get; set; }

    /// <summary>image, document, audio, video o sticker.</summary>
    public required string Kind { get; set; }

    public string? MimeType { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>false si no se pudo descargar de Meta (el contacto lo envió, pero no lo tenemos).</summary>
    public bool Stored { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Pasada esta fecha el archivo se borra del disco.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
