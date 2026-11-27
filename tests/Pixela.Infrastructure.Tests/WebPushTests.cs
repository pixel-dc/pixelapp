using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pixela.Application.Clients;
using Pixela.Application.Http;
using Pixela.Application.Users;
using Pixela.Infrastructure.Push;
using Pixela.Infrastructure.Security;

namespace Pixela.Infrastructure.Tests;

/// <summary>Web Push: el dispositivo puede descifrar lo enviado (RFC 8291) y el servicio puede verificar la firma del servidor (VAPID).</summary>
public sealed class WebPushTests
{
    private static AesGcmSecretProtector Secrets(byte seed = 7) =>
        new(Options.Create(new SecretsOptions { MasterKey = Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()) }));

    private static WebPushClient Client(HttpMessageHandler handler, byte seed = 7) =>
        new(Secrets(seed), Options.Create(new ClientAppsOptions { Domain = "apps.pixela.test" }), Options.Create(new OutboundHttpOptions()), TimeProvider.System,
            NullLogger<WebPushClient>.Instance, handler);

    [Fact]
    public void El_dispositivo_descifra_lo_enviado()
    {
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        byte[] uaPublic = [0x04, .. q.X!, .. q.Y!];
        var auth = RandomNumberGenerator.GetBytes(16);
        var message = "{\"title\":\"Nueva conversación\",\"body\":\"¿Hacen domicilios?\"}"u8.ToArray();

        var body = WebPushClient.Encrypt(uaPublic, auth, message);

        Assert.Equal(message, Decrypt(device, uaPublic, auth, body));
        // Cada envío usa otra sal y otra llave de un solo uso: el mismo texto nunca se cifra igual.
        Assert.NotEqual(body, WebPushClient.Encrypt(uaPublic, auth, message));
    }

    [Fact]
    public async Task Firma_la_peticion_con_la_llave_del_servidor()
    {
        var handler = new CaptureHandler(HttpStatusCode.Created);
        using var push = Client(handler);
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        var target = new PushTarget("https://fcm.googleapis.com/fcm/send/abc", B64([0x04, .. q.X!, .. q.Y!]), B64(RandomNumberGenerator.GetBytes(16)));

        Assert.Equal(PushOutcome.Sent, await push.SendAsync(target, "{\"title\":\"x\"}", "topic1", default));

        var request = handler.Requests.Single();
        Assert.Equal("aes128gcm", request.ContentEncoding);
        Assert.Equal("86400", request.Ttl);
        Assert.Equal("topic1", request.Topic);
        var auth = request.Authorization!;
        Assert.StartsWith("vapid t=", auth, StringComparison.Ordinal);
        var token = auth["vapid t=".Length..auth.IndexOf(',', StringComparison.Ordinal)];
        var key = auth[(auth.IndexOf("k=", StringComparison.Ordinal) + 2)..];
        Assert.Equal(push.PublicKey, key);

        // El servicio verifica el JWT con la llave pública anunciada.
        var parts = token.Split('.');
        var publicKey = PushService.Base64Url(key)!;
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), PushService.Base64Url(parts[2])!, HashAlgorithmName.SHA256));
        var claims = JsonDocument.Parse(PushService.Base64Url(parts[1])!).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("https://apps.pixela.test", claims.GetProperty("sub").GetString());

        // La llave sale de la maestra: estable entre reinicios, distinta con otra maestra.
        using var again = Client(handler);
        using var other = Client(handler, seed: 8);
        Assert.Equal(push.PublicKey, again.PublicKey);
        Assert.NotEqual(push.PublicKey, other.PublicKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushOutcome.Failed)]
    public async Task Interpreta_la_respuesta_del_servicio(HttpStatusCode status, PushOutcome expected)
    {
        using var push = Client(new CaptureHandler(status));
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        var target = new PushTarget("https://fcm.googleapis.com/fcm/send/abc", B64([0x04, .. q.X!, .. q.Y!]), B64(RandomNumberGenerator.GetBytes(16)));
        Assert.Equal(expected, await push.SendAsync(target, "{}", null, default));
    }

    /// <summary>Lo que haría el navegador al recibir (RFC 8291), escrito aparte del código que cifra.</summary>
    private static byte[] Decrypt(ECDiffieHellman device, byte[] uaPublic, byte[] auth, byte[] body)
    {
        var salt = body[..16];
        var idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        var payload = body[(21 + idLength)..];

        using var sender = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var shared = device.DeriveRawSecretAgreement(sender.PublicKey);
        var prkKey = HMACSHA256.HashData(auth, shared);
        byte[] keyInfo = [.. Encoding.ASCII.GetBytes("WebPush: info"), 0, .. uaPublic, .. asPublic, 1];
        var ikm = HMACSHA256.HashData(prkKey, keyInfo);
        var prk = HMACSHA256.HashData(salt, ikm);
        byte[] cekInfo = [.. Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm"), 0, 1];
        byte[] nonceInfo = [.. Encoding.ASCII.GetBytes("Content-Encoding: nonce"), 0, 1];
        var cek = HMACSHA256.HashData(prk, cekInfo)[..16];
        var nonce = HMACSHA256.HashData(prk, nonceInfo)[..12];

        var plain = new byte[payload.Length - 16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Decrypt(nonce, payload[..^16], payload[^16..], plain);
        Assert.Equal(2, plain[^1]); // delimitador de último registro
        return plain[..^1];
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Captured(string? ContentEncoding, string? Ttl, string? Topic, string? Authorization);

    private sealed class CaptureHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? One(System.Net.Http.Headers.HttpHeaders h, string name) => h.TryGetValues(name, out var v) ? string.Join(",", v) : null;
            Requests.Add(new Captured(string.Join(",", request.Content!.Headers.ContentEncoding), One(request.Headers, "TTL"), One(request.Headers, "Topic"), One(request.Headers, "Authorization")));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
