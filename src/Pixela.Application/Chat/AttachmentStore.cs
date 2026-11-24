namespace Pixela.Application.Chat;

/// <summary>Archivo ya descargado de Meta.</summary>
public sealed record MediaDownload(byte[] Content, string? MimeType);

/// <summary>Adjunto que el contacto envió, tal como lo describe Meta: un id de medio (WhatsApp) o un enlace temporal (Messenger e Instagram).</summary>
public sealed record InboundAttachment(string Kind, string? MediaId, string? Url, string? MimeType, string? FileName);

/// <summary>Lo que se guardó de un adjunto: va a la conversación para que el negocio lo vea.</summary>
public sealed record SavedAttachment(Guid Id, string Kind, string? MimeType, string? FileName, long SizeBytes, bool Stored);

/// <summary>
/// Contenido de los adjuntos en el disco del servidor (no en la base). Se guarda por negocio y se borra a los pocos días:
/// pasado el plazo solo queda la fila que dice que el archivo existió.
/// </summary>
public interface IAttachmentStore
{
    Task SaveAsync(Guid tenantId, Guid id, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Abre el archivo para leerlo; null si ya no está.</summary>
    Stream? OpenRead(Guid tenantId, Guid id);

    void Delete(Guid tenantId, Guid id);

    /// <summary>Pasa el archivo de un negocio a otro (al reasignar un chatbot). False si no estaba o no se pudo mover.</summary>
    bool Move(Guid fromTenantId, Guid toTenantId, Guid id);

    /// <summary>Borra los archivos más viejos que <paramref name="cutoff"/> (por fecha de modificación), existan o no en la base. Devuelve cuántos.</summary>
    int SweepOlderThan(DateTimeOffset cutoff);
}

/// <summary>Qué tipos de archivo se aceptan y cómo se muestran. Lo demás se guarda como descarga genérica, nunca como página.</summary>
public static class AttachmentTypes
{
    private static readonly HashSet<string> Inline = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp", "image/gif" };

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf",
        "audio/ogg", "audio/mpeg", "audio/mp4", "audio/aac", "audio/amr", "audio/webm",
        "video/mp4", "video/3gpp", "video/webm",
        "text/plain", "text/csv",
        "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    /// <summary>Tipo de contenido que se sirve: el de Meta si es conocido (sin parámetros), o descarga genérica.</summary>
    public static string Normalize(string? mime)
    {
        var bare = mime?.Split(';')[0].Trim();
        return bare is { Length: > 0 } && Known.Contains(bare) ? bare.ToLowerInvariant() : "application/octet-stream";
    }

    /// <summary>Solo las imágenes se muestran dentro de la página; todo lo demás se descarga.</summary>
    public static bool IsInline(string mime) => Inline.Contains(mime);

    /// <summary>Nombre de archivo seguro para mostrar y descargar: sin rutas ni caracteres de control, con tope de largo.</summary>
    public static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = new string(Path.GetFileName(name.Trim()).Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/' or ':')).ToArray());
        return cleaned.Length == 0 ? null : (cleaned.Length > 120 ? cleaned[..120] : cleaned);
    }

    public static string Label(string kind) => kind switch
    {
        "image" => "Imagen",
        "document" => "Documento",
        "audio" => "Audio",
        "video" => "Video",
        "sticker" => "Sticker",
        _ => "Archivo",
    };
}
