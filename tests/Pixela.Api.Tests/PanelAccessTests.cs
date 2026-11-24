using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Api.Tests;

/// <summary>Sesiones, roles y alcance por cliente del panel, por HTTP de punta a punta.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PanelAccessTests(ApiFixture api)
{
    private sealed record World(Guid ClientA, Guid ClientB, Guid AssetA, Guid AssetB, Guid BotA, Guid BotB, string Agency, string Owner, string AdminA, string OperatorA, string AdminB);

    /// <summary>Dos clientes con un asset y un bot cada uno, la agencia (dueño y administrador) y el equipo de cada cliente.</summary>
    private async Task<World> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var w = new World(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            $"admin-{tag}@pixeldc.test", $"owner-{tag}@pixeldc.test", $"admin-{tag}@a.test", $"op-{tag}@a.test", $"admin-{tag}@b.test");
        await api.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Clients.Add(new Client { Id = w.ClientA, Name = "A " + tag, Slug = "a-" + tag, CreatedAt = now });
            db.Clients.Add(new Client { Id = w.ClientB, Name = "B " + tag, Slug = "b-" + tag, CreatedAt = now });
            db.Assets.Add(new Asset { Id = w.AssetA, ClientId = w.ClientA, Name = "Sede A", CreatedAt = now });
            db.Assets.Add(new Asset { Id = w.AssetB, ClientId = w.ClientB, Name = "Sede B", CreatedAt = now });
            foreach (var (bot, client, asset) in new[] { (w.BotA, w.ClientA, w.AssetA), (w.BotB, w.ClientB, w.AssetB) })
            {
                db.Bots.Add(new Bot { Id = bot, ClientId = client, AssetId = asset, Name = "Bot", PublicKey = "bt_" + Guid.NewGuid().ToString("N"), CreatedAt = now });
                db.BotSecrets.Add(new BotSecrets { BotId = bot, ClientId = client, MetaVerifyTokenEncrypted = "v1.x", UpdatedAt = now });
            }

            return await db.SaveChangesAsync();
        });
        await api.CreateUserAsync(w.Agency, UserRole.AgencyAdmin, null);
        await api.CreateUserAsync(w.Owner, UserRole.AgencyOwner, null);
        await api.CreateUserAsync(w.AdminA, UserRole.ClientAdmin, w.ClientA);
        await api.CreateUserAsync(w.OperatorA, UserRole.Operator, w.ClientA);
        await api.CreateUserAsync(w.AdminB, UserRole.ClientAdmin, w.ClientB);
        return w;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<List<Guid>> BotIdsAsync(HttpClient http) =>
        (await JsonAsync(await http.GetAsync(new Uri("/panel/bots", UriKind.Relative)))).GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Login_me_and_logout()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.AdminA);

        var me = (await JsonAsync(await http.GetAsync(new Uri("/panel/me", UriKind.Relative)))).GetProperty("user");
        Assert.Equal("client_admin", me.GetProperty("role").GetString());
        Assert.Equal(w.ClientA, me.GetProperty("client_id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(new Uri("/panel/auth/logout", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(new Uri("/panel/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_missing_csrf_header_are_rejected()
    {
        var w = await SeedAsync();
        using var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        var wrong = await anonymous.PostAsJsonAsync("/panel/auth/login", new { email = w.AdminA, password = "otra contraseña cualquiera" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var http = await api.LoginAsync(w.Owner);
        http.DefaultRequestHeaders.Remove("X-Requested-With");
        var created = await http.PostAsJsonAsync("/panel/clients", new { name = "Sin cabecera" });
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task A_client_admin_sees_only_its_client_and_cannot_configure_bots()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.AdminA);

        Assert.Equal([w.BotA], await BotIdsAsync(http));
        var clients = (await JsonAsync(await http.GetAsync(new Uri("/panel/clients", UriKind.Relative)))).GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([w.ClientA], clients);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri($"/panel/bots/{w.BotB}", UriKind.Relative))).StatusCode);

        // Lo que es de la agencia: crear clientes, configurar bots, llaves y apps de Meta.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/panel/clients", new { name = "Otro" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PatchAsJsonAsync($"/panel/bots/{w.BotA}", new { name = "Cambio" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri($"/panel/bots/{w.BotA}/claude-key", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri($"/panel/bots/{w.BotA}/meta-app", UriKind.Relative))).StatusCode);

        // Lo que sí: alimentar el conocimiento de su bot.
        var faq = await http.PostAsJsonAsync($"/panel/bots/{w.BotA}/sources/faq", new { title = "Horarios", items = new[] { new { question = "¿Horario?", answer = "De 8 a 18." } } });
        Assert.Equal(HttpStatusCode.Created, faq.StatusCode);
    }

    [Fact]
    public async Task A_client_user_cannot_pick_another_client_with_the_selector()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.AdminA, clientId: w.ClientB);

        var response = await http.GetAsync(new Uri("/panel/bots", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("client_scope_invalid", (await JsonAsync(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_operator_only_reaches_the_inbox()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.OperatorA);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/panel/bots/inbox", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri($"/panel/bots/{w.BotA}/conversations", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri($"/panel/bots/{w.BotB}/conversations", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri("/panel/bots", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri($"/panel/bots/{w.BotA}/sources", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri("/panel/users", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_agency_sees_every_client_or_only_the_one_picked_in_the_selector()
    {
        var w = await SeedAsync();
        using var all = await api.LoginAsync(w.Agency);
        var bots = await BotIdsAsync(all);
        Assert.Contains(w.BotA, bots);
        Assert.Contains(w.BotB, bots);

        using var onlyB = await api.LoginAsync(w.Agency, clientId: w.ClientB);
        Assert.Equal([w.BotB], await BotIdsAsync(onlyB));

        using var unknown = await api.LoginAsync(w.Agency, clientId: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.GetAsync(new Uri("/panel/bots", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_client_admin_manages_admins_and_operators_of_its_own_client_only()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.AdminA);
        var tag = Guid.NewGuid().ToString("N")[..8];

        var admin = await http.PostAsJsonAsync("/panel/users/invites", new { email = $"nuevo-admin-{tag}@a.test", role = "client_admin" });
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);
        var op = await http.PostAsJsonAsync("/panel/users/invites", new { email = $"nuevo-op-{tag}@a.test", role = "operator" });
        Assert.Equal(HttpStatusCode.Created, op.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.PostAsJsonAsync("/panel/users/invites", new { email = $"x-{tag}@a.test", role = "agency_admin" })).StatusCode);

        // La invitación crea un administrador del cliente A que puede entrar.
        var token = (await JsonAsync(admin)).GetProperty("token").GetString();
        using var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/panel/auth/accept-invite", new { token, password = ApiFixture.Password })).StatusCode);
        using var invited = await api.LoginAsync($"nuevo-admin-{tag}@a.test");
        var me = (await JsonAsync(await invited.GetAsync(new Uri("/panel/me", UriKind.Relative)))).GetProperty("user");
        Assert.Equal(w.ClientA, me.GetProperty("client_id").GetGuid());

        // Ve y gestiona a su equipo, nunca al de B ni a la agencia.
        var team = (await JsonAsync(await http.GetAsync(new Uri("/panel/users", UriKind.Relative)))).GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToList();
        Assert.Contains(w.OperatorA, team);
        Assert.DoesNotContain(w.AdminB, team);
        Assert.DoesNotContain(w.Agency, team);
        var operatorId = await api.WithDbAsync(db => db.Users.Where(u => u.Email == w.OperatorA).Select(u => u.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"/panel/users/{operatorId}", new { role = "client_admin" })).StatusCode);
        var otherId = await api.WithDbAsync(db => db.Users.Where(u => u.Email == w.AdminB).Select(u => u.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsJsonAsync($"/panel/users/{otherId}", new { is_active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri("/panel/agency/users", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Only_the_agency_owner_manages_the_agency_team()
    {
        var w = await SeedAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var admin = await api.LoginAsync(w.Agency);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/panel/agency/users", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/panel/agency/users/invites", new { email = $"a-{tag}@pixeldc.test", role = "agency_admin" })).StatusCode);

        using var owner = await api.LoginAsync(w.Owner);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/panel/agency/users/invites", new { email = $"b-{tag}@pixeldc.test", role = "agency_admin" })).StatusCode);

        // El equipo de un cliente lo maneja la agencia eligiendo ese cliente.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri("/panel/users", UriKind.Relative))).StatusCode);
        using var adminOnA = await api.LoginAsync(w.Agency, clientId: w.ClientA);
        Assert.Equal(HttpStatusCode.Created, (await adminOnA.PostAsJsonAsync("/panel/users/invites", new { email = $"c-{tag}@a.test", role = "operator" })).StatusCode);
    }

    [Fact]
    public async Task Users_of_a_suspended_client_cannot_log_in_and_their_sessions_stop()
    {
        var w = await SeedAsync();
        using var before = await api.LoginAsync(w.AdminA);
        await api.WithDbAsync(db => db.Clients.Where(c => c.Id == w.ClientA).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ClientStatus.Suspended)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await before.GetAsync(new Uri("/panel/me", UriKind.Relative))).StatusCode);
        using var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/panel/auth/login", new { email = w.AdminA, password = ApiFixture.Password })).StatusCode);
    }

    [Fact]
    public async Task The_agency_builds_a_client_asset_and_bot_and_configures_its_integrations()
    {
        var w = await SeedAsync();
        using var http = await api.LoginAsync(w.Agency);
        var tag = Guid.NewGuid().ToString("N")[..8];

        var client = (await JsonAsync(await http.PostAsJsonAsync("/panel/clients", new { name = "Don Porko " + tag, monthly_conversation_cap = 0 }))).GetProperty("client");
        Assert.Equal(JsonValueKind.Null, client.GetProperty("monthly_conversation_cap").ValueKind); // sin tope: ilimitado
        var clientId = client.GetProperty("id").GetGuid();
        var asset = (await JsonAsync(await http.PostAsJsonAsync("/panel/assets", new { client_id = clientId, name = "Restaurante" }))).GetProperty("asset");
        var created = await http.PostAsJsonAsync("/panel/bots", new { asset_id = asset.GetProperty("id").GetGuid(), bot = new { name = "Porky" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bot = (await JsonAsync(created)).GetProperty("bot");
        var botId = bot.GetProperty("id").GetGuid();

        var key = await http.PutAsJsonAsync($"/panel/bots/{botId}/claude-key", new { key = "sk-ant-api03-prueba-0123456789abcd" });
        Assert.Equal("abcd", (await JsonAsync(key)).GetProperty("key").GetProperty("hint").GetString());

        var meta = (await JsonAsync(await http.GetAsync(new Uri($"/panel/bots/{botId}/meta-app", UriKind.Relative)))).GetProperty("meta_app");
        Assert.StartsWith("pxv_", meta.GetProperty("verify_token").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/meta/webhook/" + bot.GetProperty("public_key").GetString(), meta.GetProperty("webhook_url").GetString(), StringComparison.Ordinal);
        var secret = await http.PutAsJsonAsync($"/panel/bots/{botId}/meta-app/secret", new { app_secret = "0123456789abcdef0123456789abcdef" });
        Assert.True((await JsonAsync(secret)).GetProperty("meta_app").GetProperty("has_app_secret").GetBoolean());

        // Nada de esto devuelve los secretos.
        var body = await (await http.GetAsync(new Uri($"/panel/bots/{botId}/claude-key", UriKind.Relative))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-ant", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_a_bot_to_another_client_takes_its_data_along()
    {
        var w = await SeedAsync();
        await api.WithDbAsync(async db =>
        {
            var conversation = new Conversation { Id = Guid.CreateVersion7(), ClientId = w.ClientA, BotId = w.BotA, CreatedAt = DateTimeOffset.UtcNow, LastMessageAt = DateTimeOffset.UtcNow };
            db.Conversations.Add(conversation);
            db.ChatMessages.Add(new ChatMessage { Id = Guid.CreateVersion7(), ClientId = w.ClientA, ConversationId = conversation.Id, Role = "user", Content = "hola", CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        using var http = await api.LoginAsync(w.Agency);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/panel/bots/{w.BotA}/move", new { asset_id = w.AssetB })).StatusCode);

        using var adminB = await api.LoginAsync(w.AdminB);
        Assert.Contains(w.BotA, await BotIdsAsync(adminB));
        Assert.True(await api.WithDbAsync(db => db.ChatMessages.AllAsync(m => m.ConversationId != Guid.Empty && (db.Conversations.Any(c => c.Id == m.ConversationId && c.BotId == w.BotA) ? m.ClientId == w.ClientB : true))));
        Assert.True(await api.WithDbAsync(db => db.BotSecrets.AnyAsync(s => s.BotId == w.BotA && s.ClientId == w.ClientB)));
    }

    [Fact]
    public async Task First_run_setup_is_closed_once_there_are_users()
    {
        await SeedAsync();
        using var http = api.CreateClient();
        http.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");

        Assert.False((await JsonAsync(await http.GetAsync(new Uri("/panel/setup", UriKind.Relative)))).GetProperty("pending").GetBoolean());
        var response = await http.PostAsJsonAsync("/panel/setup", new { email = "intruso@test.test", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
