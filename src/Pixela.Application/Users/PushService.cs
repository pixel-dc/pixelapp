using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Users;

namespace Pixela.Application.Users;

/// <summary>Un dispositivo al que se envía: su dirección en el servicio de notificaciones y sus llaves (base64url).</summary>
public sealed record PushTarget(string Endpoint, string P256dh, string Auth);

public enum PushOutcome
{
    Sent,

    /// <summary>El servicio dice que la suscripción ya no existe (el usuario quitó el permiso o desinstaló la app): se borra.</summary>
    Gone,

    /// <summary>Falló por ahora (red, límite del servicio): se reintenta con el siguiente aviso.</summary>
    Failed,
}

/// <summary>Envío Web Push (RFC 8030) cifrado para el dispositivo (RFC 8291) y firmado por el servidor (VAPID, RFC 8292).</summary>
public interface IWebPush
{
    /// <summary>Llave pública del servidor (base64url): el navegador la necesita para suscribirse.</summary>
    string PublicKey { get; }

    /// <param name="topic">Avisos con el mismo tema se reemplazan en el servicio si el teléfono aún no recibió el anterior.</param>
    Task<PushOutcome> SendAsync(PushTarget target, string payload, string? topic, CancellationToken ct);
}

public sealed record PushSubscribeRequest(string? Endpoint, string? P256dh, string? Auth);

/// <summary>
/// Suscripciones a notificaciones de cada usuario. El cliente de la suscripción decide qué avisos le llegan: el del propio usuario si es de un
/// cliente; si es de la agencia, el del acceso desde el que la activó (o todos, desde el panel general).
/// </summary>
public sealed class PushService(IAppDbContext db, IWebPush push, TimeProvider clock)
{
    /// <summary>
    /// Servicios de notificaciones de los navegadores. La dirección la da el navegador, pero llega en una petición del usuario: solo se aceptan
    /// estos dominios para que nadie use el servidor para llamar a otras direcciones.
    /// </summary>
    private static readonly string[] PushHosts = ["fcm.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public const int MaxPerUser = 20;

    public string PublicKey => push.PublicKey;

    public async Task<ServiceResult<bool>> SubscribeAsync(SessionPrincipal who, Guid? hostClientId, PushSubscribeRequest request, string? userAgent, CancellationToken ct)
    {
        if (Validate(request) is { } error)
        {
            return ServiceResult<bool>.Fail("validation_error", error);
        }

        // Un usuario de un cliente siempre recibe los de su cliente; la agencia, los del acceso donde está (o todos en el panel general).
        var clientId = who.ClientId ?? hostClientId;
        var now = clock.GetUtcNow();
        var endpoint = request.Endpoint!.Trim();
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.UserId == who.UserId, ct) >= MaxPerUser)
            {
                // Tope de dispositivos: se va el más antiguo (suele ser un navegador que ya no se usa).
                var oldest = await db.PushSubscriptions.Where(s => s.UserId == who.UserId).OrderBy(s => s.CreatedAt).FirstAsync(ct);
                db.PushSubscriptions.Remove(oldest);
            }

            db.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.CreateVersion7(),
                UserId = who.UserId,
                ClientId = clientId,
                Endpoint = endpoint,
                P256dh = request.P256dh!.Trim(),
                Auth = request.Auth!.Trim(),
                UserAgent = Trim(userAgent, 300),
                CreatedAt = now,
            });
        }
        else
        {
            // El mismo navegador con otra sesión (otro usuario entró en ese teléfono): ahora avisa a quien está.
            existing.UserId = who.UserId;
            existing.ClientId = clientId;
            existing.P256dh = request.P256dh!.Trim();
            existing.Auth = request.Auth!.Trim();
            existing.UserAgent = Trim(userAgent, 300);
            existing.Failures = 0;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> UnsubscribeAsync(SessionPrincipal who, string? endpoint, CancellationToken ct)
    {
        var value = endpoint?.Trim() ?? "";
        await db.PushSubscriptions.Where(s => s.UserId == who.UserId && s.Endpoint == value).ExecuteDeleteAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Envía una notificación de prueba al dispositivo indicado (debe ser del usuario).</summary>
    public async Task<ServiceResult<bool>> SendTestAsync(SessionPrincipal who, string? endpoint, CancellationToken ct)
    {
        var value = endpoint?.Trim() ?? "";
        var target = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.UserId == who.UserId && s.Endpoint == value, ct);
        if (target is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Este dispositivo no tiene las notificaciones activas.");
        }

        var payload = Payload("Notificaciones activas", "Así te avisaremos cuando un cliente escriba.", "pixela-test", "/bandeja");
        var outcome = await push.SendAsync(new PushTarget(target.Endpoint, target.P256dh, target.Auth), payload, "pixela-test", ct);
        if (outcome == PushOutcome.Gone)
        {
            db.PushSubscriptions.Remove(target);
            await db.SaveChangesAsync(ct);
        }

        return outcome == PushOutcome.Sent
            ? ServiceResult<bool>.Ok(true)
            : ServiceResult<bool>.Fail("unavailable", "No se pudo enviar la notificación de prueba. Vuelve a activarlas en este dispositivo.");
    }

    /// <summary>Contenido de una notificación, tal como lo lee el service worker del panel.</summary>
    public static string Payload(string title, string body, string tag, string url) =>
        JsonSerializer.Serialize(new { title, body, tag, url, icon = "/app-icon.png" });

    internal static string? Validate(PushSubscribeRequest r)
    {
        if (!Uri.TryCreate(r.Endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || r.Endpoint!.Length > 1000 || !uri.IsDefaultPort)
        {
            return "La dirección de la suscripción no es válida.";
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!PushHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
        {
            return "Ese servicio de notificaciones no está admitido.";
        }

        var key = Base64Url(r.P256dh);
        if (key is not { Length: 65 } || key[0] != 0x04)
        {
            return "La llave de la suscripción no es válida.";
        }

        return Base64Url(r.Auth) is { Length: 16 } ? null : "El secreto de la suscripción no es válido.";
    }

    public static byte[]? Base64Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return null;
        }

        var s = value.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Trim(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;
}
