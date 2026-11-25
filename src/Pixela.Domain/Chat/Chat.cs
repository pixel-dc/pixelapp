namespace Pixela.Domain.Chat;

/// <summary>Chatbot de un asset de un cliente. Se instala en su web con la clave pública y responde por los canales de Meta conectados.</summary>
public class Bot
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid AssetId { get; set; }

    /// <summary>Identificador público que va en el código de instalación (nunca es un secreto ni una API key).</summary>
    public required string PublicKey { get; set; }

    public required string Name { get; set; }

    /// <summary>Instrucciones del negocio para el bot (qué vende, cómo habla). Se añaden a las reglas fijas de la plataforma.</summary>
    public string? SystemPrompt { get; set; }

    public string? Tone { get; set; }
    public string? WelcomeMessage { get; set; }

    /// <summary>Despedida del negocio: el bot la usa cuando la conversación termina.</summary>
    public string? Farewell { get; set; }

    /// <summary>Lo que el bot dice cuando pasa la conversación a una persona del negocio (canales de Meta).</summary>
    public string? HandoffMessage { get; set; }

    /// <summary>Horas sin actividad de una persona tras las cuales el bot retoma la conversación. Null = el valor por defecto de la plataforma; 0 = nunca (solo manual).</summary>
    public int? HandoffReturnHours { get; set; }

    /// <summary>Cuándo se cambió el icono; null = no tiene. Sirve de versión para el caché del navegador.</summary>
    public DateTimeOffset? IconUpdatedAt { get; set; }

    /// <summary>Nombre con el que este chatbot se presenta en las respuestas rápidas ({establecimiento}). Null = el nombre del cliente.</summary>
    public string? BusinessName { get; set; }

    /// <summary>Respuestas rápidas de quien atiende (con {operador} y {establecimiento}). Null = el texto por defecto.</summary>
    public string? QuickGreeting { get; set; }

    public string? QuickFarewell { get; set; }
    public string? QuickClosing { get; set; }

    /// <summary>Llamado a la acción (reservar, visitar, escribir…): el bot lo propone cuando es natural.</summary>
    public string? CallToAction { get; set; }

    /// <summary>Dirección del negocio: con ella el bot arma el enlace de Google Maps.</summary>
    public string? Address { get; set; }

    /// <summary>Georreferencia (opcional): con ella el enlace de Maps apunta al punto exacto y el widget muestra el mapa.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>Número de WhatsApp del negocio, solo dígitos con indicativo de país: con él el bot da el enlace wa.me.</summary>
    public string? WhatsAppNumber { get; set; }

    /// <summary>Color principal del widget (#rrggbb).</summary>
    public string? Color { get; set; }

    /// <summary>Política de tratamiento de datos del negocio (Ley 1581): se enlaza en el chat antes de capturar datos.</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>Sitios donde se puede incrustar el widget (origen completo, p. ej. https://minegocio.com). Vacío = cualquiera.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>Modelo de Claude para este bot; null = el de la agencia.</summary>
    public string? Model { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Secretos de un bot, cifrados con la llave maestra (<c>ISecretProtector</c>). Viven aparte de <see cref="Bot"/> para que ninguna consulta
/// del bot los arrastre por accidente; nunca se devuelven en claro por la API, salvo el token de verificación de Meta (que no es secreto
/// frente a quien configura la app, y debe copiarse en Meta).
/// </summary>
public class BotSecrets
{
    public Guid BotId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Llave de Claude de este bot. Sin ella el bot responde en modo extractivo (sin modelo). Una llave por bot permite auditar el consumo en la consola de Anthropic.</summary>
    public string? ClaudeApiKeyEncrypted { get; set; }

    /// <summary>Últimos caracteres de la llave, para reconocerla en pantalla.</summary>
    public string? ClaudeApiKeyHint { get; set; }

    public DateTimeOffset? ClaudeApiKeySetAt { get; set; }

    /// <summary>Clave secreta de la app de Meta de este bot: valida la firma (<c>X-Hub-Signature-256</c>) de cada webhook.</summary>
    public string? MetaAppSecretEncrypted { get; set; }

    public string? MetaAppSecretHint { get; set; }

    /// <summary>Token de verificación del webhook de Meta, generado al crear el bot (distinto en cada bot). Cifrado en reposo.</summary>
    public required string MetaVerifyTokenEncrypted { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Icono del chatbot: se ve en el chat web y en el panel.</summary>
public class BotIcon
{
    public Guid BotId { get; set; }
    public Guid ClientId { get; set; }
    public required string MimeType { get; set; }
    public required byte[] Content { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum KnowledgeKind
{
    Faq,
    Text,
    Url,
    Pdf,

    /// <summary>Catálogo leído de una tabla de Supabase con una llave de solo lectura.</summary>
    Supabase,

    /// <summary>Productos o platos cargados a mano: nombre, precio, descripción y foto.</summary>
    Products,
}

public enum KnowledgeStatus
{
    Pending,
    Indexed,
    Failed,
}

/// <summary>Origen de información del bot (una FAQ, un texto, una página web o un PDF).</summary>
public class KnowledgeSource
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }
    public KnowledgeKind Kind { get; set; }
    public required string Title { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>Supabase: tabla o vista que se lee.</summary>
    public string? SourceTable { get; set; }

    /// <summary>Supabase: columnas a incluir separadas por coma; null = todas las útiles.</summary>
    public string? SourceColumns { get; set; }

    /// <summary>Supabase: llave de solo lectura, cifrada. Nunca sale del servidor.</summary>
    public string? SecretEncrypted { get; set; }

    /// <summary>Supabase: plantilla del enlace de cada producto, con columnas entre llaves (https://tienda.com/producto/{slug}).</summary>
    public string? LinkTemplate { get; set; }

    /// <summary>Supabase: columna con la foto del producto (una URL o un arreglo de URL; se usa la primera).</summary>
    public string? ImageColumn { get; set; }

    /// <summary>Supabase: cada cuántos minutos se vuelve a leer; null = solo a mano.</summary>
    public int? RefreshMinutes { get; set; }

    /// <summary>Supabase: última lectura correcta.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>Lo que se cargó a mano, tal como se escribió (texto, PDF ya extraído, preguntas o productos en JSON), para poder editarlo. Null en las fuentes anteriores a la edición y en las que se leen de fuera.</summary>
    public string? Content { get; set; }

    public KnowledgeStatus Status { get; set; } = KnowledgeStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Fragmento de una fuente, buscable con texto completo (columna <c>Tsv</c> generada en la base).</summary>
public class KnowledgeChunk
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }
    public Guid SourceId { get; set; }
    public required string Text { get; set; }
}

public enum ConversationStatus
{
    Bot,
    Closed,
}

public class Conversation
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }

    /// <summary>Identificador anónimo del visitante (lo genera el widget); no es un dato personal por sí solo.</summary>
    public string? VisitorId { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Bot;
    public string? Origin { get; set; }

    /// <summary>Canal de Meta por el que escribe el contacto (WhatsApp, Messenger o Instagram). Null = chat web.</summary>
    public Guid? ChannelId { get; set; }

    /// <summary>Id del contacto en el canal (teléfono, PSID o IGSID). Es un dato personal: se borra con la conversación.</summary>
    public string? ContactId { get; set; }

    public string? ContactName { get; set; }

    /// <summary>El bot no supo responder y una persona del negocio debería mirar la conversación (solo canales de Meta).</summary>
    public bool NeedsAttention { get; set; }

    /// <summary>Una persona del negocio tomó la conversación: el bot calla y los mensajes del contacto solo se guardan (solo canales de Meta).</summary>
    public bool HumanActive { get; set; }

    /// <summary>Última vez que una persona del negocio tomó o escribió en la conversación: de aquí cuenta el plazo para devolverla al bot.</summary>
    public DateTimeOffset? HumanActivityAt { get; set; }

    public int MessageCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
}

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary><c>user</c> (el contacto), <c>assistant</c> (el bot) o <c>agent</c> (una persona del negocio).</summary>
    public required string Role { get; set; }

    public required string Content { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }

    /// <summary>Costo de la respuesta en millonésimas de dólar (evita decimales).</summary>
    public long CostMicros { get; set; }

    public string? Model { get; set; }

    /// <summary>Id del mensaje en Meta, solo en los que escribió el negocio desde la app del teléfono (WhatsApp Business): evita guardarlos dos veces si Meta reintenta.</summary>
    public string? ExternalId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Datos que el visitante dejó en el chat, con su consentimiento.</summary>
public class Lead
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }
    public Guid? ConversationId { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Interest { get; set; }
    public DateTimeOffset ConsentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Consumo de un bot en un mes (UTC); sobrevive a la purga de conversaciones. El del cliente es la suma de sus bots.</summary>
public class ChatUsage
{
    public Guid BotId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Mes en formato aaaamm.</summary>
    public int Period { get; set; }

    public int Conversations { get; set; }

    /// <summary>Parte de <see cref="Conversations"/> que llegó por cada canal de Meta; el resto es del chat web.</summary>
    public int ConversationsWhatsApp { get; set; }
    public int ConversationsMessenger { get; set; }
    public int ConversationsInstagram { get; set; }

    /// <summary>Mensajes que el negocio envió por WhatsApp en el mes (del bot o de una persona). Meta los cobra a partir del 1.001 mensual por número; se cuentan para que el cliente lo vea.</summary>
    public int WhatsAppMessagesSent { get; set; }

    public int Messages { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public long CostMicros { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
