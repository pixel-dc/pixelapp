using System.Text.Json;
using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Users;

public sealed record ProfileRequest(string? DisplayName);

/// <summary>Apariencia del panel elegida por cada usuario. Los valores se validan contra una lista fija: el navegador nunca recibe texto libre.</summary>
public sealed record PreferencesRequest(string? Accent, string? Background, string? Radius, string? TextSize, string? Theme);

/// <summary>Perfil del propio usuario: nombre, foto y apariencia del panel.</summary>
public sealed partial class ProfileService(IAppDbContext db, TimeProvider clock)
{
    public const int MaxNameChars = 80;
    public const int MaxPreferencesChars = 400;

    public static readonly string[] Backgrounds = ["plain", "soft", "gradient", "dots"];
    public static readonly string[] Radii = ["sm", "md", "lg"];
    public static readonly string[] TextSizes = ["sm", "md", "lg"];
    public static readonly string[] Themes = ["light", "dark", "system"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    public async Task<ServiceResult<bool>> UpdateProfileAsync(SessionPrincipal who, ProfileRequest request, CancellationToken ct)
    {
        var name = (request.DisplayName ?? "").Trim();
        if (name.Length > MaxNameChars || name.Any(char.IsControl))
        {
            return ServiceResult<bool>.Fail("validation_error", $"El nombre admite hasta {MaxNameChars} caracteres.", new Dictionary<string, string> { ["display_name"] = "Nombre no válido." });
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == who.UserId, ct);
        if (user is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Usuario no encontrado.");
        }

        user.DisplayName = name.Length == 0 ? null : name;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> SavePreferencesAsync(SessionPrincipal who, PreferencesRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        static bool Allowed(string? value, string[] list) => string.IsNullOrEmpty(value) || list.Contains(value, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(r.Accent) && !Hex().IsMatch(r.Accent))
        {
            errors["accent"] = "Elige un color de la paleta.";
        }

        if (!Allowed(r.Background, Backgrounds))
        {
            errors["background"] = "Fondo no válido.";
        }

        if (!Allowed(r.Radius, Radii))
        {
            errors["radius"] = "Esquinas no válidas.";
        }

        if (!Allowed(r.TextSize, TextSizes))
        {
            errors["text_size"] = "Tamaño de texto no válido.";
        }

        if (!Allowed(r.Theme, Themes))
        {
            errors["theme"] = "Tema no válido.";
        }

        if (errors.Count > 0)
        {
            return ServiceResult<bool>.Fail("validation_error", errors.Values.First(), errors);
        }

        var values = new Dictionary<string, string>();
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                values[key] = key == "accent" ? value.ToLowerInvariant() : value;
            }
        }

        Put("accent", r.Accent);
        Put("background", r.Background);
        Put("radius", r.Radius);
        Put("text_size", r.TextSize);
        Put("theme", r.Theme);

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == who.UserId, ct);
        if (user is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Usuario no encontrado.");
        }

        user.PreferencesJson = values.Count == 0 ? null : JsonSerializer.Serialize(values);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> SetAvatarAsync(SessionPrincipal who, byte[] content, CancellationToken ct)
    {
        var (mime, error) = ImageUpload.Check(content);
        if (mime is null)
        {
            return ServiceResult<bool>.Fail("validation_error", error!, new Dictionary<string, string> { ["file"] = error! });
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == who.UserId, ct);
        if (user is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Usuario no encontrado.");
        }

        var now = clock.GetUtcNow();
        var avatar = await db.UserAvatars.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.UserId == user.Id, ct);
        if (avatar is null)
        {
            db.UserAvatars.Add(new Domain.Users.UserAvatar { UserId = user.Id, MimeType = mime, Content = content, UpdatedAt = now });
        }
        else
        {
            avatar.MimeType = mime;
            avatar.Content = content;
            avatar.UpdatedAt = now;
        }

        user.AvatarUpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> RemoveAvatarAsync(SessionPrincipal who, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == who.UserId, ct);
        if (user is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Usuario no encontrado.");
        }

        await db.UserAvatars.IgnoreQueryFilters().Where(a => a.UserId == user.Id).ExecuteDeleteAsync(ct);
        user.AvatarUpdatedAt = null;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>La foto de un usuario del mismo negocio (el filtro por negocio del contexto impide ver la de otros).</summary>
    public async Task<StoredImage?> GetAvatarAsync(Guid userId, CancellationToken ct) =>
        // Solo de usuarios que la petición puede ver: el filtro por cliente de users decide (la agencia ve a todos; un cliente, a los suyos).
        await db.UserAvatars.AsNoTracking().Where(a => a.UserId == userId && db.Users.Any(u => u.Id == userId))
            .Select(a => new StoredImage(a.MimeType, a.Content, a.UpdatedAt)).FirstOrDefaultAsync(ct);
}
