using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pixela.Application.Chat;

namespace Pixela.Infrastructure.Chat;

/// <summary>
/// Administración de la conexión con Meta por la Graph API (ver <see cref="IMetaAdmin"/>). Los tokens van en la cabecera Authorization, nunca en
/// la URL, y los cuerpos de error se reducen al mensaje y código de Meta (sin eco de credenciales).
/// </summary>
public sealed class MetaAdminClient : IMetaAdmin, IDisposable
{
    private const int TokenErrorCode = 190;
    private readonly MetaOptions options;
    private readonly HttpClient http;

    public MetaAdminClient(IOptions<MetaOptions> options, HttpMessageHandler? handler = null)
    {
        this.options = options.Value;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(this.options.TimeoutSeconds);
    }

    public void Dispose() => http.Dispose();

    public async Task<MetaTokenInfo> DebugTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, "debug_token", token, new() { ["input_token"] = token }, null, ct);
            var data = json["data"] as JsonObject;
            var expires = data?["expires_at"]?.GetValue<long>() ?? 0;
            return new MetaTokenInfo(
                data?["is_valid"]?.GetValue<bool>() ?? false,
                data?["app_id"]?.ToString(),
                data?["type"]?.GetValue<string>(),
                expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires) : null);
        }
        catch (MetaAdminException ex) when (ex.Code == TokenErrorCode)
        {
            return new MetaTokenInfo(false, null, null, null); // Meta responde 190 si el token venció o se revocó
        }
    }

    public async Task<IReadOnlyList<MetaAppSubscription>> GetAppSubscriptionsAsync(string appId, string appSecret, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Id(appId)}/subscriptions", AppToken(appId, appSecret), null, null, ct);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new MetaAppSubscription(
            s["object"]?.GetValue<string>() ?? "",
            s["callback_url"]?.GetValue<string>(),
            s["active"]?.GetValue<bool>() ?? false,
            (s["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(f => f["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList())).ToList();
    }

    public Task SetAppSubscriptionAsync(string appId, string appSecret, string metaObject, string callbackUrl, string verifyToken, IReadOnlyList<string> fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Id(appId)}/subscriptions", AppToken(appId, appSecret), null, new()
        {
            ["object"] = metaObject,
            ["callback_url"] = callbackUrl,
            ["verify_token"] = verifyToken,
            ["fields"] = string.Join(',', fields),
        }, ct);

    public async Task<MetaPhoneInfo> GetPhoneAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, Id(phoneNumberId), token, new() { ["fields"] = "display_phone_number,status,webhook_configuration" }, null, ct);
        var overrides = new Dictionary<string, string>();
        if (json["webhook_configuration"] is JsonObject cfg)
        {
            // «application» es la dirección de la app (se revisa aparte); las demás son direcciones propias del número o de su WABA.
            foreach (var (level, url) in cfg.Where(p => p.Key != "application"))
            {
                if (url is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                {
                    overrides[level] = s;
                }
            }
        }

        return new MetaPhoneInfo(json["display_phone_number"]?.GetValue<string>(), json["status"]?.GetValue<string>(), overrides);
    }

    public Task SetPhoneWebhookAsync(string phoneNumberId, string token, string callbackUrl, string verifyToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Id(phoneNumberId), token, null, new()
        {
            ["webhook_configuration"] = new JsonObject { ["override_callback_uri"] = callbackUrl, ["verify_token"] = verifyToken }.ToJsonString(),
        }, ct);

    public async Task<IReadOnlyList<MetaSubscribedApp>> GetSubscribedAppsAsync(string accountId, string token, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Id(accountId)}/subscribed_apps", token, null, null, ct);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(a =>
        {
            // En una página el id viene en el objeto; en un WABA, dentro de whatsapp_business_api_data.
            var id = a["id"]?.ToString() ?? (a["whatsapp_business_api_data"] as JsonObject)?["id"]?.ToString() ?? "";
            var fields = (a["subscribed_fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>() ?? "").Where(f => f.Length > 0).ToList();
            return new MetaSubscribedApp(id, fields);
        }).Where(a => a.AppId.Length > 0).ToList();
    }

    public Task SubscribeAppAsync(string accountId, string token, IReadOnlyList<string>? fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Id(accountId)}/subscribed_apps", token, null,
            fields is { Count: > 0 } ? new() { ["subscribed_fields"] = string.Join(',', fields) } : [], ct);

    public async Task<string?> GetPageAccessTokenAsync(string pageId, string token, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, Id(pageId), token, new() { ["fields"] = "access_token" }, null, ct);
        return json["access_token"]?.GetValue<string>() is { Length: > 0 } t ? t : null;
    }

    private static string AppToken(string appId, string appSecret) => $"{appId}|{appSecret}";

    /// <summary>Los ids de Meta son numéricos: cualquier otra cosa no entra en la ruta (evita que un valor guardado cambie la dirección).</summary>
    private static string Id(string value) =>
        value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? value
            : throw new MetaAdminException("Identificador de Meta no válido.");

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, string bearer, Dictionary<string, string>? query, Dictionary<string, string>? form, CancellationToken ct)
    {
        var url = $"/{options.GraphVersion}/{path}";
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        try
        {
            using var request = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl), url));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = json?["error"] as JsonObject;
                var message = error?["message"]?.GetValue<string>() ?? $"Meta respondió {(int)response.StatusCode}.";
                throw new MetaAdminException(message.Length > 300 ? message[..300] : message, error?["code"]?.GetValue<int>());
            }

            return json ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            throw new MetaAdminException("No se pudo contactar con Meta.", inner: ex);
        }
    }
}
