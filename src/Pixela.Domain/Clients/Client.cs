namespace Pixela.Domain.Clients;

public enum ClientStatus
{
    Active,

    /// <summary>Sus bots dejan de responder y sus usuarios no pueden entrar.</summary>
    Suspended,
}

/// <summary>Cliente de la agencia: dueño de sus assets, bots, conversaciones y usuarios. Es la unidad de aislamiento de datos.</summary>
public class Client
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Nombre corto único (minúsculas, números y guiones).</summary>
    public required string Slug { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.Active;

    /// <summary>Marca del widget para este cliente; lo vacío usa la de la agencia y el color de cada bot manda sobre todo.</summary>
    public string? LogoUrl { get; set; }

    public string? PrimaryColor { get; set; }
    public Dictionary<string, string> Texts { get; set; } = [];

    /// <summary>Precio que la agencia cobra por conversación. Null = no se cobra (ilimitado).</summary>
    public decimal? PricePerConversation { get; set; }

    /// <summary>Moneda del precio (ISO 4217).</summary>
    public string Currency { get; set; } = "COP";

    /// <summary>Máximo de conversaciones al mes entre todos sus bots. Null = ilimitado.</summary>
    public int? MonthlyConversationCap { get; set; }

    /// <summary>Máximo de mensajes por conversación. Null = ilimitado.</summary>
    public int? MessagesPerConversationCap { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Cuándo se subió el icono de su app instalable (sirve de versión de caché); null = usa el de la agencia.</summary>
    public DateTimeOffset? AppIconUpdatedAt { get; set; }

    public bool IsActive => Status == ClientStatus.Active;
}

/// <summary>
/// Icono de la app del cliente: lo que se ve en la pantalla de inicio del teléfono y en sus notificaciones al instalar su acceso
/// (<c>{slug}.dominio</c>). Siempre PNG cuadrado, para que el sistema no lo recorte ni lo rellene de negro.
/// </summary>
public class ClientAppIcon
{
    public Guid ClientId { get; set; }
    public required byte[] Content { get; set; }
    public int Size { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Agrupa los chatbots de un cliente (una marca, una sede, una línea de negocio).</summary>
public class Asset
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
