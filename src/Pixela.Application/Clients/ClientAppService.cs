using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Clients;

namespace Pixela.Application.Clients;

/// <summary>El icono de la app instalable de cada cliente: PNG cuadrado de 192 a 1024 píxeles, como lo piden iPhone y Android.</summary>
public sealed class ClientAppService(IAppDbContext db, ClientHostResolver hosts, TimeProvider clock)
{
    public const int MinSize = 192;
    public const int MaxSize = 1024;

    public async Task<ServiceResult<bool>> SetIconAsync(Guid clientId, byte[] content, CancellationToken ct)
    {
        if (content.Length == 0 || content.Length > ImageUpload.MaxBytes)
        {
            return Invalid($"El icono debe pesar menos de {ImageUpload.MaxBytes / 1024} KB.");
        }

        if (PngSize(content) is not { } size)
        {
            return Invalid("El icono debe ser una imagen PNG.");
        }

        if (size.Width != size.Height)
        {
            return Invalid($"El icono debe ser cuadrado (este mide {size.Width}×{size.Height}).");
        }

        if (size.Width is < MinSize or > MaxSize)
        {
            return Invalid($"El icono debe medir entre {MinSize} y {MaxSize} píxeles de lado (512×512 es lo ideal).");
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Cliente no encontrado.");
        }

        var now = clock.GetUtcNow();
        var icon = await db.ClientAppIcons.FirstOrDefaultAsync(i => i.ClientId == clientId, ct);
        if (icon is null)
        {
            db.ClientAppIcons.Add(new ClientAppIcon { ClientId = clientId, Content = content, Size = size.Width, UpdatedAt = now });
        }
        else
        {
            icon.Content = content;
            icon.Size = size.Width;
            icon.UpdatedAt = now;
        }

        client.AppIconUpdatedAt = now;
        await db.SaveChangesAsync(ct);
        hosts.Forget(client.Slug);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> RemoveIconAsync(Guid clientId, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Cliente no encontrado.");
        }

        await db.ClientAppIcons.Where(i => i.ClientId == clientId).ExecuteDeleteAsync(ct);
        client.AppIconUpdatedAt = null;
        await db.SaveChangesAsync(ct);
        hosts.Forget(client.Slug);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>El icono del cliente con su lado en píxeles; null si no tiene uno propio. Es público: lo pide el teléfono antes de iniciar sesión.</summary>
    public async Task<(StoredImage Image, int Size)?> GetIconAsync(Guid clientId, CancellationToken ct)
    {
        var icon = await db.ClientAppIcons.AsNoTracking().Where(i => i.ClientId == clientId).FirstOrDefaultAsync(ct);
        return icon is null ? null : (new StoredImage("image/png", icon.Content, icon.UpdatedAt), icon.Size);
    }

    /// <summary>Ancho y alto de un PNG según su cabecera (IHDR); null si no es un PNG.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || ImageUpload.Sniff(png) != "image/png" || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            return null;
        }

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static ServiceResult<bool> Invalid(string message) =>
        ServiceResult<bool>.Fail("validation_error", message, new Dictionary<string, string> { ["file"] = message });
}
