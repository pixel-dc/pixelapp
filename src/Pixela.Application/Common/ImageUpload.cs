namespace Pixela.Application.Common;

/// <summary>Una imagen guardada: tipo real, bytes y cuándo se cambió (sirve de versión de caché).</summary>
public sealed record StoredImage(string MimeType, byte[] Content, DateTimeOffset UpdatedAt);

/// <summary>Imágenes que suben los usuarios (foto de perfil, icono del chatbot): solo PNG, JPEG o WebP, reconocidos por su contenido y de tamaño acotado. Nunca SVG.</summary>
public static class ImageUpload
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>El tipo real según los primeros bytes; null si no es una imagen admitida (lo que diga el nombre o el tipo declarado no cuenta).</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return "image/png";
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>Valida tamaño y tipo. Devuelve el tipo o un mensaje de error.</summary>
    public static (string? Mime, string? Error) Check(byte[] content)
    {
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            return (null, $"La imagen debe pesar menos de {MaxBytes / 1024} KB.");
        }

        return Sniff(content) is { } mime ? (mime, null) : (null, "Usa una imagen PNG, JPG o WebP.");
    }
}
