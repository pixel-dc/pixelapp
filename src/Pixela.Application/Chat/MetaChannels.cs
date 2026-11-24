using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pixela.Domain.Chat;

namespace Pixela.Application.Chat;

public sealed class MetaOptions
{
    public const string Section = "Meta";

    // Sin secretos aquí: la clave de la app de Meta y el token de verificación son de cada bot (BotSecrets).

    public string GraphVersion { get; set; } = "v21.0";
    public string BaseUrl { get; set; } = "https://graph.facebook.com";
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMs { get; set; } = 700;

    /// <summary>Tope de mensajes por contacto y hora: corta bucles entre bots y abusos sin gastar IA.</summary>
    public int MaxMessagesPerContactPerHour { get; set; } = 30;

    /// <summary>Intentos antes de dar un mensaje por fallido.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Tamaño máximo del cuerpo de un webhook.</summary>
    public int MaxBodyBytes { get; set; } = 1_000_000;

    /// <summary>Días que se conserva el registro de mensajes recibidos (debe superar el plazo de reintentos de Meta).</summary>
    public int InboundRetentionDays { get; set; } = 8;

    /// <summary>Carpeta del servidor donde se guardan los archivos que envían los contactos.</summary>
    public string AttachmentsPath { get; set; } = "/var/lib/pixela/attachments";

    /// <summary>Días que se conserva un archivo recibido; pasado el plazo se borra del disco.</summary>
    public int AttachmentRetentionDays { get; set; } = 7;

    /// <summary>Tamaño máximo de un archivo recibido.</summary>
    public long AttachmentMaxBytes { get; set; } = 15_000_000;

    /// <summary>Archivos que se guardan de un mismo mensaje.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 5;
}

/// <summary>Un mensaje de un contacto, ya separado del formato propio de cada canal de Meta.</summary>
/// <param name="ExternalChannelId">Id del número, página o cuenta de Instagram que lo recibió.</param>
/// <param name="Text">Null si el mensaje no era texto.</param>
public sealed record InboundMessage(ChannelKind Kind, string ExternalChannelId, string ContactId, string? ContactName, string MessageId, string? Text, IReadOnlyList<InboundAttachment>? Attachments = null);

/// <summary>Lee los webhooks de Meta (WhatsApp, Messenger e Instagram) sin depender de ninguna librería de Meta.</summary>
public static class MetaWebhookParser
{
    private const int MaxMessagesPerPayload = 100;

    /// <summary>Devuelve los mensajes entrantes de un cuerpo de webhook. Ignora estados de entrega, lecturas, ecos de lo que envió el propio negocio y todo lo que no sea un mensaje.</summary>
    public static IReadOnlyList<InboundMessage> Parse(ReadOnlySpan<byte> body)
    {
        var messages = new List<InboundMessage>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return messages;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "object") is not { } kind || !root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return messages;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                switch (kind)
                {
                    case "whatsapp_business_account":
                        ParseWhatsApp(entry, messages);
                        break;
                    case "page":
                        ParseMessaging(entry, ChannelKind.Messenger, messages);
                        break;
                    case "instagram":
                        ParseMessaging(entry, ChannelKind.Instagram, messages);
                        break;
                }

                if (messages.Count >= MaxMessagesPerPayload)
                {
                    break;
                }
            }
        }

        return messages.Count > MaxMessagesPerPayload ? messages.Take(MaxMessagesPerPayload).ToList() : messages;
    }

    /// <summary>Resume la forma de un webhook (objeto, ids de entrada y nombres de sus campos) para diagnóstico. No incluye valores de mensajes.</summary>
    public static string Describe(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            var parts = new List<string> { $"object={Str(root, "object")}" };
            if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray().Take(5))
                {
                    var keys = entry.ValueKind == JsonValueKind.Object ? string.Join('+', entry.EnumerateObject().Select(p => p.Name)) : "?";
                    parts.Add($"entry={Str(entry, "id")}[{keys}]");
                    DescribeWhatsApp(entry, parts);
                }
            }

            return string.Join(' ', parts);
        }
        catch (JsonException)
        {
            return "cuerpo ilegible";
        }
    }

    /// <summary>
    /// Para un webhook de WhatsApp, indica qué trae cada mensaje: su tipo y los nombres de sus campos (nunca los valores), y cuántos estados de entrega vienen.
    /// Sirve para ver por qué un mensaje no se leyó (sin teléfono, tipo no soportado…) sin guardar datos personales.
    /// </summary>
    private static void DescribeWhatsApp(JsonElement entry, List<string> parts)
    {
        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var change in changes.EnumerateArray().Take(5))
        {
            if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            parts.Add($"field={Str(change, "field")} value[{string.Join('+', value.EnumerateObject().Select(p => p.Name))}]");
            if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            {
                parts.Add($"estados={statuses.GetArrayLength()}");
            }

            if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contacts.EnumerateArray().Take(3))
                {
                    parts.Add($"contacto[{(c.ValueKind == JsonValueKind.Object ? string.Join('+', c.EnumerateObject().Select(p => p.Name)) : "?")}]");
                }
            }

            if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in messages.EnumerateArray().Take(5))
                {
                    var type = Str(m, "type");
                    var safeType = type is { Length: <= 24 } && type.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') ? type : "?";
                    parts.Add($"mensaje(tipo={safeType})[{(m.ValueKind == JsonValueKind.Object ? string.Join('+', m.EnumerateObject().Select(p => p.Name)) : "?")}]");
                }
            }
        }
    }

    private static void ParseWhatsApp(JsonElement entry, List<InboundMessage> into)
    {
        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var change in changes.EnumerateArray())
        {
            if (Str(change, "field") != "messages" || !change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var phoneNumberId = value.TryGetProperty("metadata", out var meta) ? Str(meta, "phone_number_id") : null;
            if (phoneNumberId is null || !value.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var names = new Dictionary<string, string>();
            if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contacts.EnumerateArray())
                {
                    if (c.TryGetProperty("profile", out var profile) && Str(profile, "name") is { } name)
                    {
                        // El contacto se identifica por teléfono o, si la persona lo ocultó (usuario de WhatsApp), por su identificador de Meta.
                        if (Str(c, "wa_id") is { } waId)
                        {
                            names[waId] = name;
                        }

                        if (Str(c, "user_id") is { } userId)
                        {
                            names[userId] = name;
                        }
                    }
                }
            }

            foreach (var m in list.EnumerateArray())
            {
                // Con nombre de usuario activado, Meta omite el teléfono (from) y trae solo el identificador por negocio (from_user_id).
                if ((Str(m, "from") ?? Str(m, "from_user_id")) is not { } from || Str(m, "id") is not { } id)
                {
                    continue;
                }

                string? text = null;
                List<InboundAttachment>? files = null;
                var type = Str(m, "type");
                if (type == "text" && m.TryGetProperty("text", out var t))
                {
                    text = Str(t, "body");
                }
                else if (type is "image" or "document" or "audio" or "video" or "sticker" && m.TryGetProperty(type, out var media) && media.ValueKind == JsonValueKind.Object && Str(media, "id") is { } mediaId)
                {
                    files = [new InboundAttachment(type, mediaId, null, Str(media, "mime_type"), Str(media, "filename"))];
                    text = Str(media, "caption"); // la leyenda de una foto o documento es texto del contacto
                }

                into.Add(new InboundMessage(ChannelKind.WhatsApp, phoneNumberId, from, names.GetValueOrDefault(from), id, text, files));
            }
        }
    }

    /// <summary>Messenger e Instagram usan la misma estructura: <c>entry[].messaging[]</c>.</summary>
    private static void ParseMessaging(JsonElement entry, ChannelKind kind, List<InboundMessage> into)
    {
        var channelId = Str(entry, "id");
        if (channelId is null || !entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                continue; // entregas, lecturas, reacciones…
            }

            // El eco es lo que envió el propio negocio: responderlo crearía un bucle.
            if (message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var sender = e.TryGetProperty("sender", out var s) ? Str(s, "id") : null;
            var mid = Str(message, "mid");
            if (sender is null || mid is null || sender == channelId)
            {
                continue;
            }

            List<InboundAttachment>? files = null;
            if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in attachments.EnumerateArray().Take(10))
                {
                    var type = Str(a, "type") switch { "image" => "image", "audio" => "audio", "video" => "video", "file" => "document", _ => null };
                    if (type is not null && a.TryGetProperty("payload", out var payload) && Str(payload, "url") is { } url)
                    {
                        (files ??= []).Add(new InboundAttachment(type, null, url, null, null));
                    }
                }
            }

            into.Add(new InboundMessage(kind, channelId, sender, null, mid, Str(message, "text"), files));
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Firma de los webhooks de Meta: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>, HMAC-SHA256 del cuerpo crudo con el secreto de la app.</summary>
public static class MetaSignature
{
    public static bool IsValid(string appSecret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(header.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    /// <summary>Calcula la cabecera de firma (para pruebas y herramientas).</summary>
    public static string Sign(string appSecret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));
}

/// <summary>Largo máximo de un mensaje por canal y corte de respuestas largas sin partir palabras.</summary>
public static class ChannelText
{
    public static int MaxLength(ChannelKind kind) => kind switch
    {
        ChannelKind.WhatsApp => 4096,
        ChannelKind.Messenger => 2000,
        _ => 1000,
    };

    public static IReadOnlyList<string> Split(string text, int max)
    {
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            // Corta en el último salto de línea o espacio dentro del límite; si no hay, corta duro.
            var cut = rest.LastIndexOf('\n', max - 1);
            if (cut < max / 2)
            {
                cut = rest.LastIndexOf(' ', max - 1);
            }

            if (cut < max / 2)
            {
                cut = max;
            }

            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            parts.Add(rest);
        }

        return parts;
    }
}

/// <summary>Envío de mensajes de texto por la API de Meta.</summary>
public interface IMetaGraph
{
    /// <exception cref="MetaSendException">Meta no aceptó el envío.</exception>
    Task SendTextAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string text, CancellationToken ct);

    /// <summary>Envía una imagen por su dirección https pública (WhatsApp la descarga desde ahí).</summary>
    /// <exception cref="MetaSendException">Meta no aceptó el envío (p. ej. la dirección no abre o no es una imagen).</exception>
    Task SendImageAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string imageUrl, CancellationToken ct);

    /// <summary>Descarga un archivo que envió el contacto. Solo habla con servidores de Meta y corta si pasa de <paramref name="maxBytes"/>.</summary>
    /// <exception cref="MetaSendException">No se pudo descargar (caducó, es muy grande o no es una dirección de Meta).</exception>
    Task<MediaDownload> DownloadMediaAsync(ChannelKind kind, string accessToken, InboundAttachment attachment, long maxBytes, CancellationToken ct);
}

/// <param name="TokenRejected">Meta dijo que el token no sirve (venció o se revocó): reintentar no ayuda.</param>
/// <param name="Permanent">Error del destinatario o de la petición (no se arregla reintentando).</param>
public sealed class MetaSendException(string message, bool tokenRejected = false, bool permanent = false, Exception? inner = null) : Exception(message, inner)
{
    public bool TokenRejected { get; } = tokenRejected;
    public bool Permanent { get; } = permanent;
}
