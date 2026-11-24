using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pixela.Application.Chat;

/// <summary>Convierte las filas de un catálogo (JSON de PostgREST) en texto para el chatbot: una línea por producto, «columna: valor | …».</summary>
public static partial class CatalogText
{
    public const int MaxRows = 2000;
    private const int MaxValueChars = 400;

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]{0,62})\}")]
    private static partial Regex Placeholder();

    /// <summary>Columnas técnicas que no aportan al visitante cuando el negocio no eligió columnas.</summary>
    private static bool IsTechnical(string name) =>
        name.Equals("id", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || name is "created_at" or "updated_at" or "deleted_at" or "inserted_at";

    /// <summary>Una dirección https (sin datos de acceso) con al menos una columna entre llaves.</summary>
    public static bool IsValidLinkTemplate(string template) =>
        template.Length <= 500
        && Uri.TryCreate(template.Replace('{', 'x').Replace('}', 'x'), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && Placeholder().IsMatch(template);

    /// <summary>Columnas que usa la plantilla del enlace.</summary>
    public static IReadOnlyList<string> LinkColumns(string? template) =>
        string.IsNullOrEmpty(template) ? [] : Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <param name="chosenColumns">Columnas que el negocio eligió mostrar; null = todas las útiles.</param>
    /// <param name="linkTemplate">Si hay, cada producto lleva «enlace: …» armado con sus columnas.</param>
    /// <param name="imageColumn">Si hay, cada producto lleva «foto: …» con la primera imagen.</param>
    /// <exception cref="InvalidOperationException">El JSON no es un arreglo de filas, está vacío o excede los topes.</exception>
    public static string Render(string title, string json, IReadOnlySet<string>? chosenColumns = null, string? linkTemplate = null, string? imageColumn = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Supabase respondió algo que no es una lista de filas.");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Supabase respondió algo que no es una lista de filas.");
            }

            var count = doc.RootElement.GetArrayLength();
            if (count == 0)
            {
                throw new InvalidOperationException("La tabla no devolvió filas (¿está vacía o la política de acceso no deja leerla?).");
            }

            if (count > MaxRows)
            {
                throw new InvalidOperationException($"La tabla tiene más de {MaxRows:N0} filas: usa una vista con los productos que el bot debe conocer.");
            }

            var sb = new StringBuilder().Append("Catálogo «").Append(title).Append("» (").Append(count).Append(" ítems):\n");
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var parts = new List<string>();
                foreach (var p in row.EnumerateObject())
                {
                    var shown = chosenColumns is null ? !IsTechnical(p.Name) : chosenColumns.Contains(p.Name);
                    if (!shown || p.Name.Equals(imageColumn, StringComparison.OrdinalIgnoreCase) || Format(p.Value) is not { Length: > 0 } text)
                    {
                        continue;
                    }

                    parts.Add($"{p.Name}: {text}");
                }

                if (imageColumn is not null && FirstImage(row, imageColumn) is { } photo)
                {
                    parts.Add($"foto: {photo}");
                }

                if (linkTemplate is not null && BuildLink(row, linkTemplate) is { } link)
                {
                    parts.Add($"enlace: {link}");
                }

                if (parts.Count > 0)
                {
                    sb.Append(string.Join(" | ", parts)).Append('\n');
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>Primera URL https de la columna (un texto o un arreglo de textos); null si no hay.</summary>
    private static string? FirstImage(JsonElement row, string column)
    {
        foreach (var p in row.EnumerateObject())
        {
            if (!p.Name.Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Array => p.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                _ => null,
            };
            return IsHttpsUrl(candidate) ? candidate!.Trim() : null;
        }

        return null;
    }

    /// <summary>Reemplaza cada {columna} por su valor (codificado para URL). Si falta alguno no hay enlace: nunca se inventa uno a medias.</summary>
    private static string? BuildLink(JsonElement row, string template)
    {
        var missing = false;
        var link = Placeholder().Replace(template, m =>
        {
            foreach (var p in row.EnumerateObject())
            {
                if (p.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && Format(p.Value) is { Length: > 0 } v)
                {
                    return Uri.EscapeDataString(v);
                }
            }

            missing = true;
            return "";
        });
        return missing || !IsHttpsUrl(link) ? null : link;
    }

    private static bool IsHttpsUrl(string? value) =>
        value is { Length: > 0 and <= 2000 } && !value.Any(char.IsWhiteSpace) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo);

    private static string? Format(JsonElement v)
    {
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "sí",
            JsonValueKind.False => "no",
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Format).Where(x => !string.IsNullOrEmpty(x))),
            _ => null,
        };
        text = text?.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text is { Length: > MaxValueChars } ? text[..MaxValueChars] + "…" : text;
    }
}

/// <summary>Ajustes al texto de las respuestas según el canal.</summary>
public static partial class ReplyFormat
{
    [GeneratedRegex(@"^[ \t]*Foto:[ \t]*https://\S+[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PhotoLine();

    [GeneratedRegex(@"\*\*([^*\n]+?)\*\*")]
    private static partial Regex Bold();

    /// <summary>Las líneas «Foto: https://…» son para que el widget web dibuje una miniatura; en WhatsApp, Messenger e Instagram se quitan.</summary>
    public static string WithoutPhotoLines(string reply) => PhotoLine().Replace(reply, "").TrimEnd();

    /// <summary>
    /// Adapta la respuesta (el modelo escribe **negrita** de Markdown) a lo que entiende cada canal: WhatsApp usa *negrita*;
    /// Messenger e Instagram no tienen formato, así que se deja el texto sin asteriscos. Además quita las líneas de foto.
    /// </summary>
    public static string ForChannel(string reply, Domain.Chat.ChannelKind kind) =>
        Bold().Replace(kind == Domain.Chat.ChannelKind.WhatsApp ? reply : WithoutPhotoLines(reply), m => kind == Domain.Chat.ChannelKind.WhatsApp ? "*" + m.Groups[1].Value + "*" : m.Groups[1].Value);

    public const int MaxPhotosPerReply = 3;

    /// <summary>Las fotos («Foto: https://…») de una respuesta, sin repetir y hasta <see cref="MaxPhotosPerReply"/>: WhatsApp las envía como imágenes.</summary>
    public static IReadOnlyList<string> PhotoUrls(string reply) =>
        PhotoUrl().Matches(reply).Select(m => m.Groups[1].Value).Where(u => u.Length <= 2000)
            .Distinct(StringComparer.Ordinal).Take(MaxPhotosPerReply).ToList();

    [GeneratedRegex(@"^[ \t]*Foto:[ \t]*(https://\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PhotoUrl();
}
