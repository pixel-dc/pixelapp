using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pixela.Application.Chat;
using Pixela.Domain.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Chat;

/// <summary>
/// Envía mensajes de texto por la API de Meta (Graph): WhatsApp por el id del número; Messenger e Instagram por <c>/me/messages</c> con el token de la página.
/// Reintenta ante 429 y errores del servidor. El cuerpo de un error no se propaga: puede traer datos del contacto o de la cuenta.
/// </summary>
public sealed class MetaGraphClient : IMetaGraph, IDisposable
{
    private readonly MetaOptions options;
    private readonly HttpClient http;

    public MetaGraphClient(IOptions<MetaOptions> options, HttpMessageHandler? handler = null)
    {
        this.options = options.Value;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(this.options.TimeoutSeconds);
    }

    public void Dispose() => http.Dispose();

    public Task SendTextAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string text, CancellationToken ct) =>
        PostAsync(Build(kind, externalChannelId, contactId, text), accessToken, ct);

    public Task SendImageAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string imageUrl, CancellationToken ct) =>
        PostAsync(BuildImage(kind, externalChannelId, contactId, imageUrl), accessToken, ct);

    private async Task PostAsync((string Path, JsonObject Body) message, string accessToken, CancellationToken ct)
    {
        var (path, body) = message;
        var url = new Uri(new Uri(options.BaseUrl), $"/{options.GraphVersion}/{path}");
        var json = body.ToJsonString();

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var status = response.StatusCode;
                var tokenRejected = status == HttpStatusCode.Unauthorized || await IsTokenErrorAsync(response, ct);
                if (tokenRejected)
                {
                    throw new MetaSendException("Meta rechazó el token del canal.", tokenRejected: true);
                }

                if (!IsRetryable(status))
                {
                    throw new MetaSendException($"Meta respondió {(int)status}.", permanent: true);
                }

                if (attempt >= options.MaxRetries)
                {
                    throw new MetaSendException($"Meta respondió {(int)status}.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= options.MaxRetries)
                {
                    throw new MetaSendException("No se pudo contactar con Meta.", inner: ex);
                }
            }
            finally
            {
                response?.Dispose();
            }

            await Task.Delay(options.RetryDelayMs * (attempt + 1), ct);
        }
    }

    private static readonly string[] AllowedMediaHosts = ["fbcdn.net", "fbsbx.com", "cdninstagram.com", "whatsapp.net", "whatsapp.com", "facebook.com"];

    /// <summary>Solo https y solo servidores de Meta (o el de la API configurada): nunca se sigue una dirección que ponga un tercero.</summary>
    private bool IsAllowedMediaUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, new Uri(options.BaseUrl).Host, StringComparison.OrdinalIgnoreCase)
            || AllowedMediaHosts.Any(h => string.Equals(host, h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<MediaDownload> DownloadMediaAsync(ChannelKind kind, string accessToken, InboundAttachment attachment, long maxBytes, CancellationToken ct)
    {
        try
        {
            Uri target;
            var mime = attachment.MimeType;
            var authenticate = false;
            if (kind == ChannelKind.WhatsApp)
            {
                // WhatsApp: el mensaje trae un id; con él se pide la dirección temporal y luego el archivo, ambos con el token del canal.
                if (attachment.MediaId is not { Length: > 0 and <= 100 } mediaId || !mediaId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    throw new MetaSendException("Adjunto sin identificador válido.", permanent: true);
                }

                using var lookup = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.BaseUrl), $"/{options.GraphVersion}/{mediaId}"));
                lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var lookupResponse = await http.SendAsync(lookup, ct);
                if (!lookupResponse.IsSuccessStatusCode)
                {
                    throw new MetaSendException($"Meta respondió {(int)lookupResponse.StatusCode}.", tokenRejected: lookupResponse.StatusCode == HttpStatusCode.Unauthorized, permanent: true);
                }

                var info = await lookupResponse.Content.ReadFromJsonAsync<JsonObject>(ct);
                if (info?["file_size"] is { } size && size.GetValue<long>() > maxBytes)
                {
                    throw new MetaSendException("El archivo es demasiado grande.", permanent: true);
                }

                mime = info?["mime_type"]?.GetValue<string>() ?? mime;
                target = new Uri(info?["url"]?.GetValue<string>() ?? throw new MetaSendException("Meta no dio la dirección del archivo.", permanent: true));
                authenticate = true;
            }
            else
            {
                // Messenger e Instagram: el enlace del adjunto es temporal y se descarga sin credenciales.
                target = Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url) ? url : throw new MetaSendException("Adjunto sin dirección.", permanent: true);
            }

            if (!IsAllowedMediaUrl(target))
            {
                throw new MetaSendException("Dirección de archivo no permitida.", permanent: true);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (authenticate)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new MetaSendException($"Meta respondió {(int)response.StatusCode}.", permanent: true);
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new MetaSendException("El archivo es demasiado grande.", permanent: true);
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new MetaSendException("El archivo es demasiado grande.", permanent: true);
                }

                buffer.Write(chunk, 0, read);
            }

            return new MediaDownload(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType ?? mime);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException or UriFormatException && !ct.IsCancellationRequested)
        {
            throw new MetaSendException("No se pudo descargar el archivo.", inner: ex);
        }
    }

    public async Task<string?> GetContactNameAsync(ChannelKind kind, string accessToken, string contactId, CancellationToken ct)
    {
        // WhatsApp ya trae el nombre en el webhook. El id de Messenger/Instagram es numérico: nada más entra en la ruta.
        if (kind == ChannelKind.WhatsApp || contactId.Length is 0 or > 100 || !contactId.All(char.IsAsciiDigit))
        {
            return null;
        }

        // Messenger da «name»; algunas apps solo permiten first_name/last_name; Instagram da «name» y «username».
        foreach (var fields in new[] { "name", "first_name,last_name", "username" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.BaseUrl), $"/{options.GraphVersion}/{contactId}?fields={fields}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue; // p. ej. el campo no está permitido: se prueba el siguiente
                }

                var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                var name = json?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = string.Join(' ', new[] { json?["first_name"]?.GetValue<string>(), json?["last_name"]?.GetValue<string>() }.Where(p => !string.IsNullOrWhiteSpace(p)));
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = json?["username"]?.GetValue<string>();
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new string(name.Trim().Where(ch => !char.IsControl(ch)).Take(120).ToArray());
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException && !ct.IsCancellationRequested)
            {
                return null; // sin red o respuesta rara: se queda con el id
            }
        }

        return null;
    }

    /// <summary>Un teléfono son solo dígitos; todo lo demás (p. ej. «CO.1349…») es el identificador por negocio de una persona que ocultó su número.</summary>
    internal static bool IsPhone(string contactId) => contactId.Length > 0 && contactId.All(char.IsAsciiDigit);

    internal static (string Path, JsonObject Body) Build(ChannelKind kind, string externalChannelId, string contactId, string text) => kind switch
    {
        ChannelKind.WhatsApp => ($"{externalChannelId}/messages", new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            [IsPhone(contactId) ? "to" : "recipient"] = contactId,
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = text },
        }),
        _ => ("me/messages", new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = contactId },
            ["messaging_type"] = "RESPONSE", // respuesta dentro de las 24 horas a un mensaje del contacto
            ["message"] = new JsonObject { ["text"] = text },
        }),
    };

    /// <summary>Solo WhatsApp envía imágenes por enlace; en Messenger e Instagram la foto no se manda.</summary>
    internal static (string Path, JsonObject Body) BuildImage(ChannelKind kind, string externalChannelId, string contactId, string imageUrl) =>
        kind == ChannelKind.WhatsApp
            ? ($"{externalChannelId}/messages", new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                [IsPhone(contactId) ? "to" : "recipient"] = contactId,
                ["type"] = "image",
                ["image"] = new JsonObject { ["link"] = imageUrl },
            })
            : throw new MetaSendException("Este canal no envía imágenes por enlace.", permanent: true);

    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>Meta responde 400 con el código 190 (token vencido o revocado) en la mayoría de sus APIs.</summary>
    private static async Task<bool> IsTokenErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            return json?["error"]?["code"]?.GetValue<int>() == 190;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return false;
        }
    }
}
