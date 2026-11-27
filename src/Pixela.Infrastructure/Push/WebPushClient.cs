using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pixela.Application.Abstractions;
using Pixela.Application.Clients;
using Pixela.Application.Http;
using Pixela.Application.Users;
using Pixela.Infrastructure.Http;

namespace Pixela.Infrastructure.Push;

/// <summary>
/// Web Push sin librerías externas: cifra el contenido para el dispositivo (RFC 8291, aes128gcm) y firma la petición con la llave del servidor
/// (VAPID, RFC 8292). La llave VAPID sale de la llave maestra: no hay otro secreto que guardar, y si la maestra cambia los dispositivos
/// deben volver a activar las notificaciones.
/// </summary>
public sealed class WebPushClient : IWebPush, IDisposable
{
    private const int RecordSize = 4096;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private readonly ECDsa vapid;
    private readonly HttpClient http;
    private readonly string subject;
    private readonly TimeProvider clock;
    private readonly ILogger<WebPushClient> logger;

    public WebPushClient(
        ISecretProtector secrets, IOptions<ClientAppsOptions> apps, IOptions<OutboundHttpOptions> outbound, TimeProvider clock, ILogger<WebPushClient> logger,
        HttpMessageHandler? handler = null)
    {
        this.clock = clock;
        this.logger = logger;
        vapid = CreateVapidKey(secrets.DeriveKey("web-push-vapid"));
        var q = vapid.ExportParameters(false).Q;
        PublicKey = Base64Url([0x04, .. q.X!, .. q.Y!]);
        subject = apps.Value.PushContact is { Length: > 0 } contact ? contact
            : apps.Value.Domain is { Length: > 0 } domain ? $"https://{domain}"
            : "https://pixelapp.pixeldc.com";

        var allowPrivate = outbound.Value.AllowPrivateNetworks;
        http = handler is null
            ? new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectCallback = (context, ct) => SafeConnect.ConnectAsync(context, allowPrivate, ct),
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    public string PublicKey { get; }

    public async Task<PushOutcome> SendAsync(PushTarget target, string payload, string? topic, CancellationToken ct)
    {
        if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return PushOutcome.Gone;
        }

        var uaPublic = PushService.Base64Url(target.P256dh);
        var auth = PushService.Base64Url(target.Auth);
        if (uaPublic is not { Length: 65 } || auth is not { Length: 16 })
        {
            return PushOutcome.Gone;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(Encrypt(uaPublic, auth, Encoding.UTF8.GetBytes(payload))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        if (!string.IsNullOrEmpty(topic))
        {
            request.Headers.TryAddWithoutValidation("Topic", topic);
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Token(endpoint)}, k={PublicKey}");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Sent;
            }

            // 404/410: la suscripción ya no existe. 403: se creó con otra llave del servidor (p. ej. cambió la maestra). Ninguna se recupera.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
            {
                return PushOutcome.Gone;
            }

            logger.LogWarning("Web Push respondió {Status} para {Host}", (int)response.StatusCode, endpoint.Host);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Web Push falló para {Host}: {Error}", endpoint.Host, ex.Message);
            return PushOutcome.Failed;
        }
    }

    /// <summary>JWT de VAPID (ES256) para el servicio de notificaciones de esa dirección.</summary>
    private string Token(Uri endpoint)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = clock.GetUtcNow().Add(TokenLifetime).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signature = vapid.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    /// <summary>
    /// Cifra el contenido para un dispositivo (RFC 8291): acuerdo ECDH con una llave de un solo uso, HKDF con el secreto del dispositivo y
    /// AES-128-GCM en un único registro. Devuelve el cuerpo completo: sal, tamaño de registro, llave pública de un solo uso y texto cifrado.
    /// </summary>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman? ephemeral = null, byte[]? salt = null)
    {
        using var owned = ephemeral is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var local = ephemeral ?? owned!;
        var lq = local.ExportParameters(false).Q;
        byte[] asPublic = [0x04, .. lq.X!, .. lq.Y!];

        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = local.DeriveRawSecretAgreement(ua.PublicKey);

        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        // Un solo registro: el contenido y el delimitador de último registro (0x02), sin relleno.
        byte[] record = [.. plaintext, 0x02];
        if (record.Length + 16 > RecordSize)
        {
            throw new ArgumentException("El contenido de la notificación es demasiado grande.", nameof(plaintext));
        }

        var cipher = new byte[record.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16))
        {
            gcm.Encrypt(nonce, record, cipher, tag);
        }

        var body = new byte[16 + 4 + 1 + asPublic.Length + cipher.Length + tag.Length];
        var span = body.AsSpan();
        salt.CopyTo(span);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span[16..20], RecordSize);
        span[20] = (byte)asPublic.Length;
        asPublic.CopyTo(span[21..]);
        cipher.CopyTo(span[(21 + asPublic.Length)..]);
        tag.CopyTo(span[(21 + asPublic.Length + cipher.Length)..]);
        return body;
    }

    /// <summary>La llave VAPID: un escalar de P-256 derivado de la maestra (si cayera fuera del rango válido, se vuelve a derivar).</summary>
    private static ECDsa CreateVapidKey(byte[] seed)
    {
        var d = seed;
        for (var i = 0; i < 8; i++)
        {
            try
            {
                return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            }
            catch (CryptographicException)
            {
                d = SHA256.HashData(d);
            }
        }

        throw new InvalidOperationException("No se pudo derivar la llave de las notificaciones.");
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        vapid.Dispose();
        http.Dispose();
    }
}
