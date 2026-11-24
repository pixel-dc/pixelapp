using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Users;

namespace Pixela.Application.Users;

public sealed record InviteUserRequest(string? Email, string? Role);

public sealed record UpdateUserRequest(string? Role, bool? IsActive);

public sealed record AcceptInviteRequest(string? Token, string? Password);

public sealed record ResetLinkRequest(bool? ResetTwoFactor);

public sealed record ResetPasswordRequest(string? Token, string? Password);

public sealed record ResetLinkCreated(string Email, string Token, bool ResetTwoFactor, DateTimeOffset ExpiresAt);

public sealed record TeamMember(Guid Id, string Email, string Role, bool IsActive, bool TwoFactorEnabled, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, string? DisplayName = null, long? AvatarVersion = null);

public sealed record InviteCreated(Guid Id, string Email, string Role, string Token, DateTimeOffset ExpiresAt);

public sealed record PendingInvite(Guid Id, string Email, string Role, DateTimeOffset ExpiresAt);

/// <summary>
/// Equipos: el de la agencia (<c>team</c> = null) y el de cada cliente.
/// <list type="bullet">
/// <item>Agencia: solo su dueño la gestiona (dueños y administradores de agencia). Siempre queda al menos un dueño activo.</item>
/// <item>Cliente: lo gestionan la agencia y los administradores de ese cliente, que invitan y administran a otros administradores y a los operadores.</item>
/// </list>
/// Nadie se cambia el rol, se desactiva ni se borra a sí mismo.
/// </summary>
public sealed class UserService(IAppDbContext db, IPasswordHasher hasher, TimeProvider clock)
{
    public const int MaxPendingInvites = 50;
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(48);
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(2);

    public async Task<ServiceResult<IReadOnlyList<TeamMember>>> ListAsync(SessionPrincipal actor, Guid? team, CancellationToken ct)
    {
        if (!CanSee(actor, team))
        {
            return Forbidden<IReadOnlyList<TeamMember>>();
        }

        var users = await Members(team).AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(ct);
        return ServiceResult<IReadOnlyList<TeamMember>>.Ok(users.Select(ToMember).ToList());
    }

    public async Task<ServiceResult<IReadOnlyList<PendingInvite>>> ListInvitesAsync(SessionPrincipal actor, Guid? team, CancellationToken ct)
    {
        if (!CanSee(actor, team))
        {
            return Forbidden<IReadOnlyList<PendingInvite>>();
        }

        var now = clock.GetUtcNow();
        var invites = await db.UserInvites.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.ClientId == team && i.AcceptedAt == null && i.ExpiresAt > now).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        return ServiceResult<IReadOnlyList<PendingInvite>>.Ok(invites.Select(i => new PendingInvite(i.Id, i.Email, AuthService.RoleName(i.Role), i.ExpiresAt)).ToList());
    }

    public async Task<ServiceResult<InviteCreated>> InviteAsync(SessionPrincipal actor, Guid? team, InviteUserRequest request, CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(request.Email);
        if (email is null)
        {
            return ServiceResult<InviteCreated>.Fail("validation_error", "Correo inválido.", new Dictionary<string, string> { ["email"] = "Correo inválido." });
        }

        if (!TryParseRole(request.Role, out var role) || role.IsAgency() != (team is null))
        {
            var allowed = team is null ? "agency_owner, agency_admin" : "client_admin, operator";
            return ServiceResult<InviteCreated>.Fail("validation_error", "Rol inválido.", new Dictionary<string, string> { ["role"] = "Valores: " + allowed + "." });
        }

        if (!CanManage(actor, team, role))
        {
            return Forbidden<InviteCreated>();
        }

        if (team is { } clientId && !await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Id == clientId, ct))
        {
            return ServiceResult<InviteCreated>.Fail("not_found", "Cliente no encontrado.");
        }

        // Un correo pertenece a una sola cuenta en toda la plataforma.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return ServiceResult<InviteCreated>.Fail("user_exists", "Ese correo ya tiene una cuenta.");
        }

        var now = clock.GetUtcNow();
        if (await db.UserInvites.IgnoreQueryFilters().CountAsync(i => i.ClientId == team && i.AcceptedAt == null && i.ExpiresAt > now, ct) >= MaxPendingInvites)
        {
            return ServiceResult<InviteCreated>.Fail("limit_reached", "Demasiadas invitaciones pendientes.");
        }

        // Reinvitar al mismo correo reemplaza cualquier invitación anterior sin aceptar.
        db.UserInvites.RemoveRange(await db.UserInvites.IgnoreQueryFilters().Where(i => i.Email == email && i.AcceptedAt == null).ToListAsync(ct));

        var token = NewToken();
        var invite = new UserInvite
        {
            Id = Guid.CreateVersion7(),
            ClientId = team,
            Email = email,
            Role = role,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            CreatedBy = actor.UserId,
            CreatedAt = now,
            ExpiresAt = now.Add(InviteLifetime),
        };
        db.UserInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        // El token se entrega una sola vez; en la base solo queda su hash.
        return ServiceResult<InviteCreated>.Ok(new InviteCreated(invite.Id, email, AuthService.RoleName(role), token, invite.ExpiresAt));
    }

    public async Task<ServiceResult<bool>> RevokeInviteAsync(SessionPrincipal actor, Guid? team, Guid inviteId, CancellationToken ct)
    {
        var invite = await db.UserInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == inviteId && i.ClientId == team && i.AcceptedAt == null, ct);
        if (invite is null || !CanSee(actor, team))
        {
            return ServiceResult<bool>.Fail("not_found", "Invitación no encontrada.");
        }

        if (!CanManage(actor, team, invite.Role))
        {
            return Forbidden<bool>();
        }

        db.UserInvites.Remove(invite);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Acepta la invitación (sin sesión): crea el usuario con la contraseña elegida, en el equipo y con el rol de la invitación.</summary>
    public async Task<ServiceResult<TeamMember>> AcceptInviteAsync(AcceptInviteRequest request, CancellationToken ct)
    {
        var invalid = ServiceResult<TeamMember>.Fail("invalid_invite", "La invitación no es válida o venció.");
        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > 100)
        {
            return invalid;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Token));
        var now = clock.GetUtcNow();
        var invite = await db.UserInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null || invite.AcceptedAt is not null || invite.ExpiresAt <= now)
        {
            return invalid;
        }

        if (PasswordPolicy.Validate(request.Password, invite.Email) is { } problem)
        {
            return ServiceResult<TeamMember>.Fail("validation_error", problem, new Dictionary<string, string> { ["password"] = problem });
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == invite.Email, ct))
        {
            return invalid;
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            ClientId = invite.ClientId,
            Email = invite.Email,
            PasswordHash = hasher.Hash(request.Password!),
            Role = invite.Role,
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        db.Users.Add(user);
        invite.AcceptedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return invalid;
        }

        return ServiceResult<TeamMember>.Ok(ToMember(user));
    }

    public async Task<ServiceResult<TeamMember>> UpdateAsync(SessionPrincipal actor, Guid? team, Guid userId, UpdateUserRequest request, CancellationToken ct)
    {
        var target = await Members(team).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null || !CanSee(actor, team))
        {
            return ServiceResult<TeamMember>.Fail("not_found", "Usuario no encontrado.");
        }

        if (target.Id == actor.UserId)
        {
            return ServiceResult<TeamMember>.Fail("forbidden", "No puedes cambiar tu propio rol ni desactivarte.");
        }

        if (!CanManage(actor, team, target.Role))
        {
            return Forbidden<TeamMember>();
        }

        var newRole = target.Role;
        if (request.Role is not null && (!TryParseRole(request.Role, out newRole) || newRole.IsAgency() != (team is null)))
        {
            return ServiceResult<TeamMember>.Fail("validation_error", "Rol inválido.", new Dictionary<string, string> { ["role"] = "Rol inválido para este equipo." });
        }

        if (!CanManage(actor, team, newRole))
        {
            return Forbidden<TeamMember>();
        }

        var newActive = request.IsActive ?? target.IsActive;
        var losesOwnership = target.Role == UserRole.AgencyOwner && target.IsActive && (newRole != UserRole.AgencyOwner || !newActive);
        if (losesOwnership && !await HasAnotherOwnerAsync(target.Id, ct))
        {
            return ServiceResult<TeamMember>.Fail("last_owner", "Debe quedar al menos un dueño de la agencia activo.");
        }

        var now = clock.GetUtcNow();
        var accessChanged = newRole != target.Role || (target.IsActive && !newActive);
        target.Role = newRole;
        target.IsActive = newActive;

        // Si cambian sus permisos o se desactiva, sus sesiones abiertas se cierran de inmediato.
        if (accessChanged)
        {
            await RevokeSessionsAsync(target.Id, now, ct);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<TeamMember>.Ok(ToMember(target));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(SessionPrincipal actor, Guid? team, Guid userId, CancellationToken ct)
    {
        var target = await Members(team).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null || !CanSee(actor, team))
        {
            return ServiceResult<bool>.Fail("not_found", "Usuario no encontrado.");
        }

        if (target.Id == actor.UserId || !CanManage(actor, team, target.Role))
        {
            return ServiceResult<bool>.Fail("forbidden", "No puedes eliminar a este usuario.");
        }

        if (target.Role == UserRole.AgencyOwner && !await HasAnotherOwnerAsync(target.Id, ct))
        {
            return ServiceResult<bool>.Fail("last_owner", "Debe quedar al menos un dueño de la agencia activo.");
        }

        db.Users.Remove(target); // sesiones, foto y enlaces de recuperación se borran en cascada
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Enlace de recuperación para otra persona del equipo (no para uno mismo). Un enlace nuevo anula los anteriores.</summary>
    public async Task<ServiceResult<ResetLinkCreated>> CreateResetLinkAsync(SessionPrincipal actor, Guid? team, Guid userId, ResetLinkRequest request, CancellationToken ct)
    {
        var target = await Members(team).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null || !CanSee(actor, team))
        {
            return ServiceResult<ResetLinkCreated>.Fail("not_found", "Usuario no encontrado.");
        }

        if (target.Id == actor.UserId || !CanManage(actor, team, target.Role))
        {
            return ServiceResult<ResetLinkCreated>.Fail("forbidden", "No puedes restablecer el acceso de este usuario.");
        }

        if (!target.IsActive)
        {
            return ServiceResult<ResetLinkCreated>.Fail("user_inactive", "Activa al usuario antes de restablecer su acceso.");
        }

        var now = clock.GetUtcNow();
        db.PasswordResetTokens.RemoveRange(await db.PasswordResetTokens.IgnoreQueryFilters().Where(t => t.UserId == target.Id && t.UsedAt == null).ToListAsync(ct));

        var token = NewToken();
        var reset = new PasswordResetToken
        {
            Id = Guid.CreateVersion7(),
            UserId = target.Id,
            ClientId = target.ClientId,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            ResetTwoFactor = request.ResetTwoFactor == true && target.TotpEnabled,
            CreatedBy = actor.UserId,
            CreatedAt = now,
            ExpiresAt = now.Add(ResetLifetime),
        };
        db.PasswordResetTokens.Add(reset);
        await db.SaveChangesAsync(ct);
        return ServiceResult<ResetLinkCreated>.Ok(new ResetLinkCreated(target.Email, token, reset.ResetTwoFactor, reset.ExpiresAt));
    }

    /// <summary>Usa el enlace: fija la contraseña nueva, levanta el bloqueo y cierra todas las sesiones. El 2FA sigue salvo que se pidiera quitarlo.</summary>
    public async Task<ServiceResult<bool>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var invalid = ServiceResult<bool>.Fail("invalid_reset", "El enlace no es válido o venció.");
        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > 100)
        {
            return invalid;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Token));
        var now = clock.GetUtcNow();
        var reset = await db.PasswordResetTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || reset.UsedAt is not null || reset.ExpiresAt <= now)
        {
            return invalid;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == reset.UserId, ct);
        if (user is null || !user.IsActive)
        {
            return invalid;
        }

        if (PasswordPolicy.Validate(request.Password, user.Email) is { } problem)
        {
            return ServiceResult<bool>.Fail("validation_error", problem, new Dictionary<string, string> { ["password"] = problem });
        }

        user.PasswordHash = hasher.Hash(request.Password!);
        user.PasswordChangedAt = now;
        user.FailedLogins = 0;
        user.LockedUntil = null;
        if (reset.ResetTwoFactor)
        {
            user.TotpEnabled = false;
            user.TotpSecretEncrypted = null;
            user.TotpLastStep = null;
            user.RecoveryCodeHashes = [];
        }

        reset.UsedAt = now;
        await RevokeSessionsAsync(user.Id, now, ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Ver un equipo: la agencia ve todos; un administrador de cliente, solo el de su cliente. Un operador, ninguno.</summary>
    public static bool CanSee(SessionPrincipal actor, Guid? team) =>
        actor.IsAgency || (team is { } clientId && actor.Role == UserRole.ClientAdmin && actor.ClientId == clientId);

    /// <summary>Gestionar (invitar, cambiar, borrar) a alguien con <paramref name="role"/> en <paramref name="team"/>.</summary>
    public static bool CanManage(SessionPrincipal actor, Guid? team, UserRole role) => team is null
        ? actor.Role == UserRole.AgencyOwner && role.IsAgency()
        : !role.IsAgency() && CanSee(actor, team);

    private IQueryable<User> Members(Guid? team) => db.Users.IgnoreQueryFilters().Where(u => u.ClientId == team);

    private Task<bool> HasAnotherOwnerAsync(Guid exceptUserId, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == UserRole.AgencyOwner && u.IsActive && u.Id != exceptUserId, ct);

    private async Task RevokeSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in await db.UserSessions.IgnoreQueryFilters().Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct))
        {
            s.RevokedAt = now;
        }
    }

    private static ServiceResult<T> Forbidden<T>() => ServiceResult<T>.Fail("forbidden", "No puedes gestionar este equipo o este rol.");

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static bool TryParseRole(string? value, out UserRole role)
    {
        role = UserRole.Operator;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "agency_owner": role = UserRole.AgencyOwner; return true;
            case "agency_admin": role = UserRole.AgencyAdmin; return true;
            case "client_admin": role = UserRole.ClientAdmin; return true;
            case "operator": return true;
            default: return false;
        }
    }

    private static TeamMember ToMember(User u) =>
        new(u.Id, u.Email, AuthService.RoleName(u.Role), u.IsActive, u.TotpEnabled, u.CreatedAt, u.LastLoginAt, u.DisplayName, u.AvatarUpdatedAt?.UtcTicks);
}
