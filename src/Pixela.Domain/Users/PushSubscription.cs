namespace Pixela.Domain.Users;

/// <summary>
/// Un navegador o teléfono que aceptó recibir notificaciones de un usuario. La crea el propio navegador (Web Push): la dirección es del
/// servicio de notificaciones del fabricante (Google, Apple, Mozilla, Microsoft) y las llaves cifran el contenido para que solo ese
/// dispositivo lo lea.
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// De qué cliente avisa. Un usuario de un cliente, siempre del suyo. Para la agencia depende de dónde lo activó: en el acceso de un
    /// cliente, de ese cliente; en el panel general (null), de todos.
    /// </summary>
    public Guid? ClientId { get; set; }

    public required string Endpoint { get; set; }

    /// <summary>Llave pública del dispositivo (P-256, base64url).</summary>
    public required string P256dh { get; set; }

    /// <summary>Secreto de autenticación del dispositivo (base64url).</summary>
    public required string Auth { get; set; }

    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Envíos fallidos seguidos (sin contar los que el servicio declara vencidos, que se borran al momento).</summary>
    public int Failures { get; set; }
}
