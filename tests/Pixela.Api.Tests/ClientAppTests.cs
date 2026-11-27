using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Chat;
using Pixela.Application.Clients;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Api.Tests;

/// <summary>
/// El acceso propio de cada cliente ({slug}.dominio): quién entra, cómo se instala (manifiesto e icono) y a quién llegan las notificaciones.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ClientAppTests(ApiFixture api)
{
    private sealed record World(Guid ClientA, string SlugA, Guid ClientB, string SlugB, string PublicKeyA, Guid BotA, Guid BotB, string Agency, string AdminA, string AdminB);

    private async Task<World> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (slugA, slugB) = ("a-" + tag, "b-" + tag);
        Guid botA, botB;
        string keyA;
        var (scope, db) = api.Database();
        await using (scope)
        {
            var now = DateTimeOffset.UtcNow;
            db.Clients.Add(new Client { Id = a, Name = "Don Porko " + tag, Slug = slugA, CreatedAt = now });
            db.Clients.Add(new Client { Id = b, Name = "Otro " + tag, Slug = slugB, CreatedAt = now });
            var assetA = new Asset { Id = Guid.NewGuid(), ClientId = a, Name = "Sede A", CreatedAt = now };
            var assetB = new Asset { Id = Guid.NewGuid(), ClientId = b, Name = "Sede B", CreatedAt = now };
            db.Assets.AddRange(assetA, assetB);
            await db.SaveChangesAsync();

            var bots = scope.ServiceProvider.GetRequiredService<BotService>();
            var createdA = (await bots.CreateAsync(assetA.Id, new BotRequest("Bot A", null, null, null, null, null, null, null), default)).Value!;
            var createdB = (await bots.CreateAsync(assetB.Id, new BotRequest("Bot B", null, null, null, null, null, null, null), default)).Value!;
            (botA, botB, keyA) = (createdA.Id, createdB.Id, createdA.PublicKey);
        }

        var w = new World(a, slugA, b, slugB, keyA, botA, botB, $"agency-{tag}@pixeldc.test", $"admin-{tag}@a.test", $"admin-{tag}@b.test");
        await api.CreateUserAsync(w.Agency, UserRole.AgencyAdmin, null);
        await api.CreateUserAsync(w.AdminA, UserRole.ClientAdmin, a);
        await api.CreateUserAsync(w.AdminB, UserRole.ClientAdmin, b);
        return w;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<List<Guid>> BotIdsAsync(HttpClient http) =>
        (await JsonAsync(await http.GetAsync(new Uri("/panel/bots", UriKind.Relative)))).GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task En_el_acceso_de_un_cliente_solo_entran_sus_usuarios_y_la_agencia_ve_solo_ese_cliente()
    {
        var w = await SeedAsync();

        // El administrador de otro cliente no entra por el acceso de A: misma respuesta que una contraseña errónea.
        using (var other = api.CreateClientFor(w.SlugA))
        {
            var rejected = await other.PostAsJsonAsync("/panel/auth/login", new { email = w.AdminB, password = ApiFixture.Password });
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using var admin = await api.LoginAtAsync(w.SlugA, w.AdminA);
        Assert.Equal([w.BotA], await BotIdsAsync(admin));

        // La agencia entra, pero en ese acceso todo es de A: ni ve a B ni puede pedirlo con el selector.
        using var agency = await api.LoginAtAsync(w.SlugA, w.Agency);
        var bots = await BotIdsAsync(agency);
        Assert.Contains(w.BotA, bots);
        Assert.DoesNotContain(w.BotB, bots);
        using var asB = new HttpRequestMessage(HttpMethod.Get, "/panel/bots");
        asB.Headers.Add("X-Client-Id", w.ClientB.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await agency.SendAsync(asB)).StatusCode);

        // En el panel general todo sigue igual: el administrador de A entra, y la agencia ve a todos.
        using var main = await api.LoginAsync(w.AdminA);
        Assert.Equal([w.BotA], await BotIdsAsync(main));
        using var agencyMain = await api.LoginAsync(w.Agency);
        Assert.Contains(w.BotB, await BotIdsAsync(agencyMain));
    }

    [Fact]
    public async Task Un_acceso_inexistente_o_suspendido_no_sirve_nada()
    {
        var w = await SeedAsync();
        using var unknown = api.CreateClientFor("no-existe-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.NotFound, (await unknown.GetAsync(new Uri("/panel/branding", UriKind.Relative))).StatusCode);

        await api.WithDbAsync(async db =>
        {
            var c = await db.Clients.FindAsync(w.ClientB);
            c!.Status = ClientStatus.Suspended;
            return await db.SaveChangesAsync();
        });
        api.Services.CreateScope().ServiceProvider.GetRequiredService<ClientHostResolver>().Forget(w.SlugB);
        using var suspended = api.CreateClientFor(w.SlugB);
        Assert.Equal(HttpStatusCode.NotFound, (await suspended.PostAsJsonAsync("/panel/auth/login", new { email = w.AdminB, password = ApiFixture.Password })).StatusCode);

        // El proxy solo pide certificado para el panel y los accesos de clientes activos.
        await using var scope = api.Services.CreateAsyncScope();
        var hosts = scope.ServiceProvider.GetRequiredService<ClientHostResolver>();
        Assert.True(await hosts.AllowsCertificateAsync(ApiFixture.AppsDomain, default));
        Assert.True(await hosts.AllowsCertificateAsync($"{w.SlugA}.{ApiFixture.AppsDomain}", default));
        Assert.False(await hosts.AllowsCertificateAsync($"{w.SlugB}.{ApiFixture.AppsDomain}", default));
        Assert.False(await hosts.AllowsCertificateAsync($"x.{w.SlugA}.{ApiFixture.AppsDomain}", default));
        Assert.False(await hosts.AllowsCertificateAsync("otro-dominio.com", default));
    }

    [Fact]
    public async Task La_app_del_cliente_se_instala_con_su_nombre_y_su_icono()
    {
        var w = await SeedAsync();
        using var site = api.CreateClientFor(w.SlugA);

        var branding = await JsonAsync(await site.GetAsync(new Uri("/panel/branding", UriKind.Relative)));
        Assert.Equal(w.ClientA, branding.GetProperty("client_id").GetGuid());
        Assert.StartsWith("Don Porko", branding.GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal(ApiFixture.AppsDomain, branding.GetProperty("app_domain").GetString());

        var manifest = await JsonAsync(await site.GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative)));
        Assert.StartsWith("Don Porko", manifest.GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal("512x512", manifest.GetProperty("icons")[0].GetProperty("sizes").GetString()); // aún sin icono propio: el de la agencia

        // La agencia sube el icono: debe ser PNG cuadrado.
        using var agency = await api.LoginAsync(w.Agency);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadIconAsync(agency, w.ClientA, Png(512, 300))).StatusCode);
        var png = Png(512, 512);
        Assert.Equal(HttpStatusCode.OK, (await UploadIconAsync(agency, w.ClientA, png)).StatusCode);

        // Un administrador del cliente no cambia el icono de su app.
        using var admin = await api.LoginAsync(w.AdminA);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadIconAsync(admin, w.ClientA, png)).StatusCode);

        manifest = await JsonAsync(await site.GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative)));
        Assert.Equal("512x512", manifest.GetProperty("icons")[0].GetProperty("sizes").GetString());
        Assert.Equal(png, await site.GetByteArrayAsync(new Uri("/app-icon.png", UriKind.Relative)));
        Assert.Equal(png, await site.GetByteArrayAsync(new Uri("/apple-touch-icon.png", UriKind.Relative)));

        // El otro cliente sigue con el de la agencia.
        using var other = api.CreateClientFor(w.SlugB);
        manifest = await JsonAsync(await other.GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative)));
        Assert.Equal("512x512", manifest.GetProperty("icons")[0].GetProperty("sizes").GetString());
    }

    [Fact]
    public async Task Los_avisos_llegan_solo_a_quien_sigue_a_ese_cliente()
    {
        var w = await SeedAsync();
        using var adminA = await api.LoginAtAsync(w.SlugA, w.AdminA);
        using var adminB = await api.LoginAtAsync(w.SlugB, w.AdminB);
        using var agencyAll = await api.LoginAsync(w.Agency);
        using var agencyAtB = await api.LoginAtAsync(w.SlugB, w.Agency);

        var (endA, endB, endAll, endAgencyB) = (Endpoint(), Endpoint(), Endpoint(), Endpoint());
        foreach (var (http, endpoint) in new[] { (adminA, endA), (adminB, endB), (agencyAll, endAll), (agencyAtB, endAgencyB) })
        {
            var r = await http.PostAsJsonAsync("/panel/push/subscriptions", Subscription(endpoint));
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        }

        // Una dirección que no es de un servicio de notificaciones conocido no se acepta.
        var bad = await adminA.PostAsJsonAsync("/panel/push/subscriptions", Subscription("https://intranet.example.com/push/1"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // Un visitante abre una conversación con el bot de A.
        var (scope, _) = api.Database(unset: true);
        await using (scope)
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            var session = await chat.StartAsync(w.PublicKeyA, default);
            Assert.True(session.IsSuccess, session.Error?.Message);
            Assert.True((await chat.SendAsync(session.Value!.Token, "Hola, ¿hacen domicilios?", default)).IsSuccess);
        }

        var sent = await WaitForAsync(endA, endAll);
        var toA = Assert.Single(sent, s => s.Endpoint == endA);
        Assert.Single(sent, s => s.Endpoint == endAll);
        Assert.DoesNotContain(sent, s => s.Endpoint == endB || s.Endpoint == endAgencyB);

        var payload = JsonDocument.Parse(toA.Payload).RootElement;
        Assert.Equal("Nueva conversación por el chat web", payload.GetProperty("title").GetString());
        Assert.Contains("hacen domicilios", payload.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.StartsWith($"/bandeja?bot={w.BotA}&c=", payload.GetProperty("url").GetString(), StringComparison.Ordinal);

        // Tras desactivarlas en ese dispositivo, ya no le llega nada.
        Assert.True((await adminA.PostAsJsonAsync("/panel/push/subscriptions/remove", new { endpoint = endA })).IsSuccessStatusCode);
        Assert.Equal(0, await api.WithDbAsync(db => Task.FromResult(db.PushSubscriptions.Count(s => s.Endpoint == endA))));
    }

    [Fact]
    public async Task Una_suscripcion_vencida_se_borra_al_intentar_avisar()
    {
        var w = await SeedAsync();
        using var adminA = await api.LoginAtAsync(w.SlugA, w.AdminA);
        var endpoint = Endpoint();
        Assert.True((await adminA.PostAsJsonAsync("/panel/push/subscriptions", Subscription(endpoint))).IsSuccessStatusCode);
        lock (api.Push.Gone)
        {
            api.Push.Gone.Add(endpoint);
        }

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Application.Abstractions.ClientScope>().SetAllClients();
        var delivery = scope.ServiceProvider.GetRequiredService<ConversationAlertDelivery>();
        Assert.Equal(0, await delivery.DeliverAsync(new ConversationAlert(w.ClientA, w.BotA, Guid.NewGuid(), "t", "b"), default));
        Assert.Equal(0, await api.WithDbAsync(db => Task.FromResult(db.PushSubscriptions.Count(s => s.Endpoint == endpoint))));
    }

    private async Task<List<(string Endpoint, string Payload)>> WaitForAsync(params string[] endpoints)
    {
        for (var i = 0; i < 100; i++)
        {
            var sent = api.Push.Sent.ToList();
            if (endpoints.All(e => sent.Any(s => s.Endpoint == e)))
            {
                await Task.Delay(200); // por si llegara algo de más, que la prueba lo vea
                return api.Push.Sent.ToList();
            }

            await Task.Delay(50);
        }

        Assert.Fail("No llegaron los avisos esperados.");
        return [];
    }

    private static string Endpoint() => "https://fcm.googleapis.com/fcm/send/" + Guid.NewGuid().ToString("N");

    private static object Subscription(string endpoint)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        return new { endpoint, p256dh = B64([0x04, .. q.X!, .. q.Y!]), auth = B64(RandomNumberGenerator.GetBytes(16)) };
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Task<HttpResponseMessage> UploadIconAsync(HttpClient http, Guid clientId, byte[] png)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "icono.png");
        return http.PutAsync(new Uri($"/panel/clients/{clientId}/app-icon", UriKind.Relative), form);
    }

    /// <summary>Cabecera de un PNG con las medidas dadas (el servidor solo lee la firma y el IHDR).</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        header.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        RandomNumberGenerator.Fill(bytes.AsSpan(24));
        return bytes;
    }
}
