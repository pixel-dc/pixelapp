using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Application.Users;

/// <summary>Ajustes del panel (no secretos, con valores por defecto).</summary>
public sealed class PanelOptions
{
    public const string Section = "Panel";

    /// <summary>Cookie con atributo Secure. Solo se desactiva en desarrollo y pruebas (HTTP local).</summary>
    public bool SecureCookies { get; set; } = true;

    public int SessionAbsoluteDays { get; set; } = 7;
    public int SessionIdleHours { get; set; } = 12;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int LoginRateLimitPerMinute { get; set; } = 10;
    public string TotpIssuer { get; set; } = "Pixela";
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);

    /// <summary>true si el hash usa parámetros más débiles que los actuales y conviene recalcularlo.</summary>
    bool NeedsRehash(string hash);
}

public sealed record ClientInfo(string? Ip, string? UserAgent);

/// <summary>Quién está en el panel.</summary>
/// <param name="ClientId">Cliente del usuario; null = equipo de la agencia.</param>
/// <param name="ClientName">Nombre de su cliente (para el encabezado del panel); null en la agencia.</param>
public sealed record UserView(
    Guid Id, Guid? ClientId, string? ClientName, string Email, string Role, bool TwoFactorEnabled,
    string? DisplayName = null, long? AvatarVersion = null, System.Text.Json.JsonElement? Preferences = null);

public enum LoginKind
{
    Authenticated,
    TwoFactorRequired,
    Invalid,
}

public sealed record LoginResult(LoginKind Kind, UserView? User = null, string? SessionToken = null, DateTimeOffset? SessionExpiresAt = null, string? Challenge = null)
{
    public static readonly LoginResult Rejected = new(LoginKind.Invalid);
}

/// <summary>Sesión validada: el usuario, su cliente (null = agencia) y su rol.</summary>
public sealed record SessionPrincipal(Guid SessionId, Guid UserId, Guid? ClientId, UserRole Role)
{
    public bool IsAgency => Role.IsAgency();
}

public sealed record TotpSetup(string Secret, string Uri);

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "123456789012", "qwertyuiop12", "contrasena123", "pixela123456", "administrador",
    };

    public static string? Validate(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"La contraseña debe tener al menos {MinLength} caracteres.";
        }

        if (password.Length > MaxLength)
        {
            return $"La contraseña no puede superar {MaxLength} caracteres.";
        }

        if (Common.Contains(password) || string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
        {
            return "La contraseña es demasiado fácil de adivinar.";
        }

        return password.Distinct().Count() < 5 ? "La contraseña es demasiado repetitiva." : null;
    }
}

/// <summary>
/// Autenticación del panel: contraseña (Argon2id), 2FA opcional (TOTP + códigos de recuperación) y sesiones en servidor revocables.
/// Las firmas (reto de 2FA, códigos de recuperación) usan llaves derivadas de la maestra: no hace falta otra variable de entorno.
/// </summary>
public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ISecretProtector secrets,
    IOptions<PanelOptions> panel,
    TimeProvider clock)
{
    private const int RecoveryCodeCount = 10;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    // Hash de relleno: se verifica cuando el usuario no existe para que el tiempo de respuesta no delate si el correo está registrado.
    private string? _dummy;

    public async Task<LoginResult> LoginAsync(string? email, string? password, ClientInfo client, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        var user = normalized is null ? null : await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalized, ct);
        var now = clock.GetUtcNow();

        if (user is null || string.IsNullOrEmpty(password) || password.Length > PasswordPolicy.MaxLength * 2)
        {
            _ = hasher.Verify(password ?? string.Empty, _dummy ??= hasher.Hash(Guid.NewGuid().ToString("N")));
            return LoginResult.Rejected;
        }

        if (!user.IsActive || (user.LockedUntil is { } until && until > now) || !await ClientAllowsAsync(user, ct))
        {
            _ = hasher.Verify(password, user.PasswordHash);
            return LoginResult.Rejected;
        }

        if (!hasher.Verify(password, user.PasswordHash))
        {
            await RegisterFailureAsync(user, now, ct);
            return LoginResult.Rejected;
        }

        if (user.TotpEnabled)
        {
            // La contraseña ya es correcta; falta el segundo factor. El reto es corto, firmado y atado al usuario.
            return new LoginResult(LoginKind.TwoFactorRequired, Challenge: CreateChallenge(user.Id, now));
        }

        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = hasher.Hash(password);
        }

        return await StartSessionAsync(user, client, now, ct);
    }

    public async Task<LoginResult> CompleteTwoFactorAsync(string? challenge, string? code, string? recoveryCode, ClientInfo client, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var userId = ReadChallenge(challenge, now);
        var user = userId is null ? null : await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || !user.TotpEnabled || user.TotpSecretEncrypted is null || (user.LockedUntil is { } until && until > now)
            || !await ClientAllowsAsync(user, ct))
        {
            return LoginResult.Rejected;
        }

        var ok = false;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var step = Totp.Verify(secrets.Unprotect(user.TotpSecretEncrypted), code, now, user.TotpLastStep);
            if (step is { } accepted)
            {
                user.TotpLastStep = accepted;
                ok = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            ok = ConsumeRecoveryCode(user, recoveryCode);
        }

        if (!ok)
        {
            await RegisterFailureAsync(user, now, ct);
            return LoginResult.Rejected;
        }

        return await StartSessionAsync(user, client, now, ct);
    }

    /// <summary>Valida la cookie en cada petición: sesión vigente, usuario activo y, si es de un cliente, cliente activo.</summary>
    public async Task<SessionPrincipal?> ValidateSessionAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var session = await db.UserSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        var o = panel.Value;

        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now || now - session.LastSeenAt > TimeSpan.FromHours(o.SessionIdleHours))
        {
            return null;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == session.UserId, ct);
        if (user is null || !user.IsActive || !await ClientAllowsAsync(user, ct))
        {
            return null;
        }

        // Se actualiza la última actividad como mucho una vez por minuto (evita una escritura por petición).
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new SessionPrincipal(session.Id, user.Id, user.ClientId, user.Role);
    }

    /// <summary>¿Existe y está activo este cliente? La agencia lo usa para elegir con qué cliente trabaja (cabecera del selector).</summary>
    public Task<bool> ClientExistsAsync(Guid clientId, CancellationToken ct) =>
        db.Clients.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.Id == clientId, ct);

    public async Task LogoutAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.UserSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is { RevokedAt: null })
        {
            session.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<UserView?> GetUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return null;
        }

        var clientName = user.ClientId is { } clientId
            ? await db.Clients.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        return ToView(user, clientName);
    }

    public async Task<ServiceResult<bool>> ChangePasswordAsync(SessionPrincipal who, string? current, string? next, CancellationToken ct)
    {
        var user = await LoadAsync(who.UserId, ct);
        if (user is null || string.IsNullOrEmpty(current) || !hasher.Verify(current, user.PasswordHash))
        {
            return ServiceResult<bool>.Fail("invalid_credentials", "La contraseña actual no es correcta.");
        }

        if (PasswordPolicy.Validate(next, user.Email) is { } problem)
        {
            return ServiceResult<bool>.Fail("validation_error", problem, new Dictionary<string, string> { ["new_password"] = problem });
        }

        var now = clock.GetUtcNow();
        user.PasswordHash = hasher.Hash(next!);
        user.PasswordChangedAt = now;
        await RevokeOtherSessionsAsync(user.Id, who.SessionId, now, ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<TotpSetup>> BeginTotpSetupAsync(SessionPrincipal who, CancellationToken ct)
    {
        var user = await LoadAsync(who.UserId, ct);
        if (user is null)
        {
            return ServiceResult<TotpSetup>.Fail("not_found", "Usuario no encontrado.");
        }

        if (user.TotpEnabled)
        {
            return ServiceResult<TotpSetup>.Fail("already_enabled", "La verificación en dos pasos ya está activa.");
        }

        var secret = Totp.GenerateSecret();
        user.TotpSecretEncrypted = secrets.Protect(secret);
        user.TotpLastStep = null;
        await db.SaveChangesAsync(ct);
        return ServiceResult<TotpSetup>.Ok(new TotpSetup(secret, Totp.ProvisioningUri(panel.Value.TotpIssuer, user.Email, secret)));
    }

    /// <summary>Confirma el alta con un código válido y devuelve los códigos de recuperación (se muestran una sola vez).</summary>
    public async Task<ServiceResult<IReadOnlyList<string>>> EnableTotpAsync(SessionPrincipal who, string? code, CancellationToken ct)
    {
        var user = await LoadAsync(who.UserId, ct);
        if (user is null || user.TotpSecretEncrypted is null || user.TotpEnabled)
        {
            return ServiceResult<IReadOnlyList<string>>.Fail("invalid_state", "Inicia la configuración de la verificación en dos pasos primero.");
        }

        var now = clock.GetUtcNow();
        if (Totp.Verify(secrets.Unprotect(user.TotpSecretEncrypted), code, now, null) is not { } step)
        {
            return ServiceResult<IReadOnlyList<string>>.Fail("invalid_code", "El código no es correcto.");
        }

        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        user.TotpEnabled = true;
        user.TotpLastStep = step;
        user.RecoveryCodeHashes = codes.Select(HashRecoveryCode).ToList();
        await RevokeOtherSessionsAsync(user.Id, who.SessionId, now, ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult<IReadOnlyList<string>>.Ok(codes);
    }

    public async Task<ServiceResult<bool>> DisableTotpAsync(SessionPrincipal who, string? password, string? code, string? recoveryCode, CancellationToken ct)
    {
        var user = await LoadAsync(who.UserId, ct);
        var now = clock.GetUtcNow();
        if (user is null || !user.TotpEnabled || user.TotpSecretEncrypted is null)
        {
            return ServiceResult<bool>.Fail("invalid_state", "La verificación en dos pasos no está activa.");
        }

        // Desactivar exige contraseña Y un segundo factor: una sesión robada no basta.
        var second = false;
        if (!string.IsNullOrWhiteSpace(code))
        {
            second = Totp.Verify(secrets.Unprotect(user.TotpSecretEncrypted), code, now, user.TotpLastStep) is not null;
        }
        else if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            second = ConsumeRecoveryCode(user, recoveryCode);
        }

        if (string.IsNullOrEmpty(password) || !hasher.Verify(password, user.PasswordHash) || !second)
        {
            await RegisterFailureAsync(user, now, ct);
            return ServiceResult<bool>.Fail("invalid_credentials", "Contraseña o código incorrectos.");
        }

        user.TotpEnabled = false;
        user.TotpSecretEncrypted = null;
        user.TotpLastStep = null;
        user.RecoveryCodeHashes = [];
        await RevokeOtherSessionsAsync(user.Id, who.SessionId, now, ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Un usuario de un cliente suspendido no entra (la agencia sí, siempre).</summary>
    private async Task<bool> ClientAllowsAsync(User user, CancellationToken ct) =>
        user.ClientId is not { } clientId
        || await db.Clients.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.Id == clientId && c.Status == ClientStatus.Active, ct);

    private async Task<LoginResult> StartSessionAsync(User user, ClientInfo client, DateTimeOffset now, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = now.AddDays(panel.Value.SessionAbsoluteDays);

        db.UserSessions.Add(new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            ClientId = user.ClientId,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = expires,
            Ip = client.Ip,
            UserAgent = client.UserAgent is { Length: > 300 } ua ? ua[..300] : client.UserAgent,
        });

        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);
        return new LoginResult(LoginKind.Authenticated, await GetUserAsync(user.Id, ct), token, expires);
    }

    private async Task RegisterFailureAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        user.FailedLogins++;
        if (user.FailedLogins >= panel.Value.MaxFailedLogins)
        {
            user.LockedUntil = now.AddMinutes(panel.Value.LockoutMinutes);
            user.FailedLogins = 0;
        }

        await db.SaveChangesAsync(ct);
    }

    private Task<User?> LoadAsync(Guid userId, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);

    private async Task RevokeOtherSessionsAsync(Guid userId, Guid keepSessionId, DateTimeOffset now, CancellationToken ct)
    {
        var others = await db.UserSessions.IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.Id != keepSessionId && s.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var s in others)
        {
            s.RevokedAt = now;
        }
    }

    private bool ConsumeRecoveryCode(User user, string recoveryCode)
    {
        var hash = HashRecoveryCode(recoveryCode);
        var index = user.RecoveryCodeHashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (index < 0)
        {
            return false;
        }

        // Cada código sirve una sola vez. Se reasigna la lista para que EF detecte el cambio.
        user.RecoveryCodeHashes = [.. user.RecoveryCodeHashes.Where((_, i) => i != index)];
        return true;
    }

    private string HashRecoveryCode(string code)
    {
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return Convert.ToHexStringLower(HMACSHA256.HashData(secrets.DeriveKey("recovery-codes"), Encoding.UTF8.GetBytes(normalized)));
    }

    private static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 10);
        return new string(chars[..5]) + "-" + new string(chars[5..]);
    }

    private string CreateChallenge(Guid userId, DateTimeOffset now)
    {
        var payload = $"{userId:N}.{now.Add(ChallengeLifetime).ToUnixTimeSeconds()}";
        return payload + "." + SignChallenge(payload);
    }

    private Guid? ReadChallenge(string? challenge, DateTimeOffset now)
    {
        var parts = challenge?.Split('.');
        if (parts is not { Length: 3 } || challenge!.Length > 200)
        {
            return null;
        }

        var expected = SignChallenge($"{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
        {
            return null;
        }

        return long.TryParse(parts[1], out var exp) && exp > now.ToUnixTimeSeconds() && Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }

    private string SignChallenge(string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(secrets.DeriveKey("login-challenge"), Encoding.UTF8.GetBytes(payload)));

    internal static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        return e is { Length: > 3 and <= 254 } && e.Contains('@', StringComparison.Ordinal) && !e.Any(char.IsWhiteSpace) ? e : null;
    }

    public static string RoleName(UserRole role) => role switch
    {
        UserRole.AgencyOwner => "agency_owner",
        UserRole.AgencyAdmin => "agency_admin",
        UserRole.ClientAdmin => "client_admin",
        _ => "operator",
    };

    private static UserView ToView(User u, string? clientName) =>
        new(u.Id, u.ClientId, clientName, u.Email, RoleName(u.Role), u.TotpEnabled, u.DisplayName, u.AvatarUpdatedAt?.UtcTicks, ParsePreferences(u.PreferencesJson));

    private static System.Text.Json.JsonElement? ParsePreferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
