using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Users;

namespace Pixela.Application.Users;

public sealed record SetupRequest(string? Email, string? Password, string? DisplayName);

/// <summary>
/// Primer arranque: mientras no exista ningún usuario, el panel ofrece crear al dueño de la agencia. Así no hace falta poner usuario ni
/// contraseña en variables de entorno. En cuanto existe un usuario, la puerta se cierra para siempre.
/// </summary>
public sealed class SetupService(IAppDbContext db, IPasswordHasher hasher, TimeProvider clock)
{
    public async Task<bool> IsPendingAsync(CancellationToken ct) => !await db.Users.IgnoreQueryFilters().AnyAsync(ct);

    public async Task<ServiceResult<Guid>> CreateOwnerAsync(SetupRequest request, CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(request.Email);
        if (email is null)
        {
            return ServiceResult<Guid>.Fail("validation_error", "Correo inválido.", new Dictionary<string, string> { ["email"] = "Correo inválido." });
        }

        if (PasswordPolicy.Validate(request.Password, email) is { } problem)
        {
            return ServiceResult<Guid>.Fail("validation_error", problem, new Dictionary<string, string> { ["password"] = problem });
        }

        // Un candado de la base serializa dos primeros arranques simultáneos: solo el primero crea al dueño.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.AcquireSetupLockAsync(ct);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(ct))
        {
            return ServiceResult<Guid>.Fail("forbidden", "PixelApp ya está configurado. Entra con tu usuario.");
        }

        var now = clock.GetUtcNow();
        var name = request.DisplayName?.Trim();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password!),
            Role = UserRole.AgencyOwner,
            DisplayName = string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, 80)],
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult<Guid>.Ok(user.Id);
    }
}
