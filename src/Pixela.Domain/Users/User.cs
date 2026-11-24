namespace Pixela.Domain.Users;

public enum UserRole
{
    /// <summary>Dueño de la agencia: todo, incluidos los usuarios de la agencia y la configuración general.</summary>
    AgencyOwner,

    /// <summary>Equipo de la agencia: crea clientes, assets y bots, configura secretos e integraciones y entra a cualquier cliente.</summary>
    AgencyAdmin,

    /// <summary>Administrador de un cliente: gestiona administradores y operadores de su cliente, alimenta el conocimiento y ve conversaciones y uso.</summary>
    ClientAdmin,

    /// <summary>Atiende conversaciones de los bots de su cliente: solo la bandeja (leer, tomar, responder, devolver al bot).</summary>
    Operator,
}

public static class UserRoles
{
    /// <summary>Los roles de agencia no pertenecen a ningún cliente; los de cliente siempre a uno.</summary>
    public static bool IsAgency(this UserRole role) => role is UserRole.AgencyOwner or UserRole.AgencyAdmin;
}

public class User
{
    public Guid Id { get; set; }

    /// <summary>Cliente al que pertenece. Null = usuario de la agencia (<see cref="UserRoles.IsAgency"/>).</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Siempre en minúsculas; único en toda la plataforma.</summary>
    public required string Email { get; set; }

    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.Operator;
    public bool IsActive { get; set; } = true;

    /// <summary>Secreto TOTP cifrado. Mientras <see cref="TotpEnabled"/> sea false es un alta pendiente de confirmar.</summary>
    public string? TotpSecretEncrypted { get; set; }

    public bool TotpEnabled { get; set; }

    /// <summary>Último paso de 30 s aceptado: impide reutilizar un código (replay).</summary>
    public long? TotpLastStep { get; set; }

    /// <summary>HMAC de los códigos de recuperación aún sin usar.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];

    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Nombre que se muestra en el panel; null = el correo.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Apariencia elegida (color, fondo, esquinas, tamaño del texto) como JSON validado por el servidor.</summary>
    public string? PreferencesJson { get; set; }

    /// <summary>Cuándo se cambió la foto; null = no tiene. Sirve de versión para el caché del navegador.</summary>
    public DateTimeOffset? AvatarUpdatedAt { get; set; }
}

/// <summary>Foto del usuario (reducida por el navegador y validada aquí): vive en la base para que se respalde.</summary>
public class UserAvatar
{
    public Guid UserId { get; set; }
    public required string MimeType { get; set; }
    public required byte[] Content { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Sesión del panel, guardada en el servidor: se puede revocar al instante.</summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Cliente del usuario (null = agencia). Copia de <see cref="User.ClientId"/> para filtrar sin unir tablas.</summary>
    public Guid? ClientId { get; set; }

    /// <summary>SHA-256 del token de la cookie (el token tiene 256 bits de entropía, no hace falta salarlo).</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>
/// Enlace de un solo uso para que una persona recupere el acceso (olvidó la contraseña o perdió el segundo factor).
/// Lo genera un administrador; en la base solo queda el hash del token.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Cliente del usuario (null = agencia).</summary>
    public Guid? ClientId { get; set; }

    public required byte[] TokenHash { get; set; }

    /// <summary>Si es true, al usarlo también se desactiva la verificación en dos pasos (perdió el teléfono y los códigos).</summary>
    public bool ResetTwoFactor { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Invitación pendiente. El token solo se entrega una vez; aquí queda su hash.</summary>
public class UserInvite
{
    public Guid Id { get; set; }

    /// <summary>Cliente al que se une (null = equipo de la agencia).</summary>
    public Guid? ClientId { get; set; }

    public required string Email { get; set; }
    public UserRole Role { get; set; }
    public required byte[] TokenHash { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}
