using Pixela.Application.Chat;
using Pixela.Application.Common;

namespace Pixela.Api.Endpoints;

public sealed record StartChatRequest(string? Key);

public sealed record SendChatRequest(string? Token, string? Text);

public sealed record PollChatRequest(string? Token, string? After);

/// <summary>
/// API pública del chat (sin sesión ni API key): la usa el widget incrustado. El acceso se limita por IP aquí y por los topes del cliente dentro del servicio.
/// No lleva CORS: el widget corre en un iframe servido por esta misma API.
/// </summary>
public static class ChatEndpoints
{
    public const string SessionRateLimit = "chat-session";
    public const string MessageRateLimit = "chat-message";
    public const string PollRateLimit = "chat-poll";

    public static void MapChat(this WebApplication app)
    {
        var chat = app.MapGroup("/chat/v1");

        // Solo esta lectura es pública entre sitios: el cargador del widget, que corre en la web del cliente, la usa para pintar su botón.
        // No lleva cookies ni da nada que la página del chat no muestre ya; las demás rutas siguen sin CORS.
        chat.MapGet("/{key}/config", async (string key, HttpContext http, ChatService s, CancellationToken ct) =>
            ToResult(await WithCors(http, s.GetConfigAsync(key, ct)), c => new { name = c.Name, welcome = c.Welcome, color = c.Color, privacy_url = c.PrivacyUrl, max_messages = c.MaxMessages, logo_url = c.LogoUrl, texts = c.Texts }))
            .RequireRateLimiting(SessionRateLimit);

        // El icono del chatbot lo pide la página del chat (mismo origen); la versión va en la dirección, así puede cachearse mucho tiempo.
        chat.MapGet("/{key}/icon", async (string key, HttpContext http, ChatService s, CancellationToken ct) =>
            PanelEndpoints.ServeImage(http, await s.GetBotIconAsync(key, ct), "public, max-age=86400"))
            .RequireRateLimiting(SessionRateLimit);

        chat.MapPost("/sessions", async (StartChatRequest r, ChatService s, CancellationToken ct) =>
            ToResult(await s.StartAsync(r.Key, ct), x => new
            {
                token = x.Token,
                name = x.Config.Name,
                welcome = x.Config.Welcome,
                color = x.Config.Color,
                privacy_url = x.Config.PrivacyUrl,
                max_messages = x.Config.MaxMessages,
                logo_url = x.Config.LogoUrl,
                texts = x.Config.Texts,
            })).RequireRateLimiting(SessionRateLimit);

        chat.MapPost("/messages", async (SendChatRequest r, ChatService s, CancellationToken ct) =>
            ToResult(await s.SendAsync(r.Token, r.Text, ct), x => new { reply = x.Reply, remaining_messages = x.RemainingMessages, offer_lead = x.OfferLead, with_person = x.WithPerson }))
            .RequireRateLimiting(MessageRateLimit);

        // El widget pregunta cada pocos segundos si una persona del negocio le escribió. No gasta IA: tiene su propio límite por IP.
        chat.MapPost("/poll", async (PollChatRequest r, ChatService s, CancellationToken ct) =>
            ToResult(await s.PollAsync(r.Token, r.After, ct), x => new { messages = x.Messages.Select(m => new { text = m.Text, at = m.At }), with_person = x.WithPerson }))
            .RequireRateLimiting(PollRateLimit);

        MapWidget(app);

        chat.MapPost("/lead", async (LeadRequest r, ChatService s, CancellationToken ct) =>
            ToResult(await s.CaptureLeadAsync(r, ct), _ => new { ok = true }, StatusCodes.Status201Created))
            .RequireRateLimiting(MessageRateLimit);
    }

    /// <summary>Cargador (<c>/widget.js</c>) y página del chat (<c>/widget/{clave}</c>). La página solo se puede incrustar en los sitios del bot.</summary>
    private static void MapWidget(WebApplication app)
    {
        app.MapGet("/widget.js", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Content(WidgetAssets.Loader, "text/javascript; charset=utf-8");
        });

        app.MapGet("/widget/{key}", async (string key, HttpContext http, ChatService s, CancellationToken ct) =>
        {
            var info = await s.GetWidgetAsync(key, ct);
            if (!info.IsSuccess)
            {
                return Notice(http, ChatService.Unavailable, StatusCodes.Status404NotFound);
            }

            var origins = info.Value!.AllowedOrigins;
            if (!ReferrerAllowed(http.Request.Headers.Referer.ToString(), origins))
            {
                return Notice(http, "Este chat no puede mostrarse en este sitio.", StatusCodes.Status403Forbidden);
            }

            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var ancestors = origins.Count == 0 ? "https: http:" : string.Join(' ', origins);
            http.Response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; img-src https:; frame-src https://www.google.com; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors {ancestors}";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers.CacheControl = "no-store";
            var html = WidgetAssets.Page.Replace("__NONCE__", nonce, StringComparison.Ordinal).Replace("__KEY__", System.Net.WebUtility.HtmlEncode(key), StringComparison.Ordinal);
            return Results.Content(html, "text/html; charset=utf-8");
        }).RequireRateLimiting(SessionRateLimit);
    }

    /// <summary>Página mínima con un aviso fijo (sin script y con CSP propia), para los casos en que el chat no se muestra.</summary>
    private static IResult Notice(HttpContext http, string message, int status)
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        http.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        var html = WidgetAssets.Notice.Replace("__NONCE__", nonce, StringComparison.Ordinal).Replace("__MESSAGE__", System.Net.WebUtility.HtmlEncode(message), StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private static async Task<T> WithCors<T>(HttpContext http, Task<T> work)
    {
        http.Response.Headers.AccessControlAllowOrigin = "*";
        return await work;
    }

    /// <summary>Sin sitios configurados vale cualquiera. Con sitios, si el navegador manda Referer debe ser de uno de ellos (sin Referer lo impone frame-ancestors).</summary>
    internal static bool ReferrerAllowed(string referer, IReadOnlyList<string> origins)
    {
        if (origins.Count == 0 || referer.Length == 0)
        {
            return true;
        }

        return Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            && origins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
    }

    private static IResult ToResult<T>(ServiceResult<T> result, Func<T, object> shape, int successStatus = StatusCodes.Status200OK)
    {
        if (result.IsSuccess)
        {
            return Results.Json(shape(result.Value!), statusCode: successStatus);
        }

        var status = result.Error!.Code switch
        {
            "not_found" => StatusCodes.Status404NotFound,
            "validation_error" => StatusCodes.Status422UnprocessableEntity,
            "invalid_session" => StatusCodes.Status401Unauthorized,
            "invalid_state" => StatusCodes.Status409Conflict,
            "limit_reached" => StatusCodes.Status429TooManyRequests,
            "unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new { error = new { code = result.Error.Code, message = result.Error.Message } }, statusCode: status);
    }
}
