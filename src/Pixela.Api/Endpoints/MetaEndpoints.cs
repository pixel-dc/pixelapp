using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pixela.Application.Chat;

namespace Pixela.Api.Endpoints;

/// <summary>
/// Webhook de Meta (WhatsApp, Messenger e Instagram), uno por bot: <c>/meta/webhook/{clave pública del bot}</c>. Cada bot trae su app de Meta,
/// así que la verificación usa el token del bot y la firma, la clave secreta de su app. Es anónimo: la autenticidad la da la firma.
/// Responde de inmediato y deja los mensajes en cola; atenderlos lo hace un trabajador (Meta reintenta si tardamos).
/// </summary>
public static class MetaEndpoints
{
    public static void MapMeta(this WebApplication app)
    {
        var meta = app.MapGroup("/meta");

        // Verificación al registrar el webhook en la app de Meta del bot: devuelve el reto solo si el token coincide con el del bot.
        meta.MapGet("/webhook/{botKey}", async (string botKey, HttpRequest request, MetaInboxService inbox, CancellationToken ct) =>
        {
            var app = await inbox.ResolveBotAppAsync(botKey, ct);
            var token = request.Query["hub.verify_token"].ToString();
            var valid = app is not null
                && request.Query["hub.mode"] == "subscribe"
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(app.VerifyToken));
            return valid ? Results.Text(request.Query["hub.challenge"].ToString(), "text/plain") : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        meta.MapPost("/webhook/{botKey}", async (string botKey, HttpRequest request, MetaInboxService inbox, IOptions<MetaOptions> options, ILogger<MetaInboxService> logger, CancellationToken ct) =>
        {
            var o = options.Value;
            if (request.ContentLength > o.MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Cuerpo crudo: la firma se calcula sobre los bytes exactos que mandó Meta, no sobre su reserialización.
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > o.MaxBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            var body = buffer.ToArray();
            var parsed = MetaWebhookParser.Parse(body);
            // Diagnóstico sin datos personales: solo la forma del evento (objeto y campos), nunca el contenido.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Webhook de Meta: {Shape}, mensajes leídos: {Count}", MetaWebhookParser.Describe(body), parsed.Count);
            }
            if (parsed.Count == 0)
            {
                return Results.Ok(new { accepted = 0 }); // estados de entrega, lecturas…: no hay nada que guardar ni a quién autenticar
            }

            // Sin app de Meta configurada en el bot (o bot inexistente) nada se acepta: no hay secreto con qué validar la firma.
            var app = await inbox.ResolveBotAppAsync(botKey, ct);
            if (app?.AppSecret is not { Length: > 0 } secret || !MetaSignature.IsValid(secret, body, request.Headers["X-Hub-Signature-256"].ToString()))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var accepted = await inbox.AcceptAsync(app.BotId, parsed, ct);
            return Results.Ok(new { accepted });
        });
    }
}
