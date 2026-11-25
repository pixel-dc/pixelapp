using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Api.Tests;

/// <summary>
/// Diagnóstico y reparación de la conexión de un bot con Meta (token, webhook de la app, suscripción al WABA o a la página, token de página).
/// Meta se simula con un <see cref="FakeAdmin"/> que guarda el estado, para comprobar que reparar de verdad deja todo en regla.
/// </summary>
[Collection(ApiTestGroup.Name)]
public class MetaConnectionTests(ApiFixture api)
{
    private const string BaseUrl = "https://pixela.test";
    private const string AppSecret = "secreto-de-prueba-de-la-app";
    private const string AppId = "999";

    /// <summary>Meta de mentira con estado: lo que se lee es lo que se corrigió antes.</summary>
    private sealed class FakeAdmin : IMetaAdmin
    {
        public Dictionary<string, MetaTokenInfo> Tokens { get; } = [];
        public List<MetaAppSubscription> Subs { get; } = [];
        public Dictionary<string, MetaPhoneInfo> Phones { get; } = [];
        public Dictionary<string, List<MetaSubscribedApp>> Subscribed { get; } = [];
        public Dictionary<string, string> PageTokens { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<MetaTokenInfo> DebugTokenAsync(string token, CancellationToken ct) =>
            Task.FromResult(Tokens.TryGetValue(token, out var t) ? t : new MetaTokenInfo(false, null, null, null));

        public Task<IReadOnlyList<MetaAppSubscription>> GetAppSubscriptionsAsync(string appId, string appSecret, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MetaAppSubscription>>(Subs.ToList());

        public Task SetAppSubscriptionAsync(string appId, string appSecret, string metaObject, string callbackUrl, string verifyToken, IReadOnlyList<string> fields, CancellationToken ct)
        {
            Calls.Add("set-app-webhook:" + metaObject);
            Subs.RemoveAll(s => s.Topic == metaObject);
            Subs.Add(new MetaAppSubscription(metaObject, callbackUrl, true, fields.ToList()));
            return Task.CompletedTask;
        }

        public Task<MetaPhoneInfo> GetPhoneAsync(string phoneNumberId, string token, CancellationToken ct) =>
            Task.FromResult(Phones.TryGetValue(phoneNumberId, out var p) ? p : throw new MetaAdminException("(#100) número desconocido", 100));

        public Task SetPhoneWebhookAsync(string phoneNumberId, string token, string callbackUrl, string verifyToken, CancellationToken ct)
        {
            Calls.Add("set-phone-webhook");
            Phones[phoneNumberId] = Phones[phoneNumberId] with { Overrides = new Dictionary<string, string> { ["phone_number"] = callbackUrl } };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetaSubscribedApp>> GetSubscribedAppsAsync(string accountId, string token, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MetaSubscribedApp>>(Subscribed.TryGetValue(accountId, out var l) ? l.ToList() : []);

        public Task SubscribeAppAsync(string accountId, string token, IReadOnlyList<string>? fields, CancellationToken ct)
        {
            Calls.Add("subscribe:" + accountId);
            Subscribed[accountId] = [new MetaSubscribedApp(AppId, fields?.ToList() ?? [])];
            return Task.CompletedTask;
        }

        public Task<string?> GetPageAccessTokenAsync(string pageId, string token, CancellationToken ct) =>
            PageTokens.TryGetValue(pageId, out var t) ? Task.FromResult<string?>(t) : throw new MetaAdminException("(#10) sin permiso sobre la página", 10);
    }

    private sealed record Seeded(Guid BotId, string PublicKey, string WebhookUrl, Dictionary<ChannelKind, (Guid Id, string ExternalId, string Token)> Channels);

    private WebApplicationFactory<Program> Factory(FakeAdmin admin) =>
        api.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IMetaAdmin>(admin)));

    private async Task<Seeded> SeedAsync(string? appSecret = AppSecret, string? wabaId = "waba1", params ChannelKind[] kinds)
    {
        var clientId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var botId = Guid.CreateVersion7();
        var publicKey = "bt_" + Guid.NewGuid().ToString("N");
        var protector = api.Services.GetRequiredService<ISecretProtector>();
        var channels = new Dictionary<ChannelKind, (Guid, string, string)>();
        await api.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Clients.Add(new Client { Id = clientId, Name = "Negocio", Slug = "n-" + clientId.ToString("N")[..20], CreatedAt = now });
            db.Assets.Add(new Asset { Id = assetId, ClientId = clientId, Name = "Sede", CreatedAt = now });
            db.Bots.Add(new Bot { Id = botId, ClientId = clientId, AssetId = assetId, PublicKey = publicKey, Name = "Bot", CreatedAt = now });
            db.BotSecrets.Add(new BotSecrets
            {
                BotId = botId,
                ClientId = clientId,
                MetaVerifyTokenEncrypted = protector.Protect(BotService.NewVerifyToken()),
                MetaAppSecretEncrypted = appSecret is null ? null : protector.Protect(appSecret),
                UpdatedAt = now,
            });
            foreach (var kind in kinds)
            {
                var id = Guid.CreateVersion7();
                var external = "ext" + Guid.NewGuid().ToString("N")[..12];
                var token = "tok-" + Guid.NewGuid().ToString("N");
                channels[kind] = (id, external, token);
                db.Channels.Add(new Channel
                {
                    Id = id,
                    ClientId = clientId,
                    BotId = botId,
                    Kind = kind,
                    ExternalId = external,
                    DisplayName = kind.ToString(),
                    AccessTokenEncrypted = protector.Protect(token),
                    ProviderAccountId = kind == ChannelKind.WhatsApp ? wabaId : null,
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });
        return new Seeded(botId, publicKey, BaseUrl + "/meta/webhook/" + publicKey, channels);
    }

    /// <summary>El servicio necesita la dirección pública de la agencia: se pone para la prueba y se devuelve a como estaba.</summary>
    private async Task<T> WithBaseUrlAsync<T>(string? url, Func<Task<T>> action)
    {
        var before = await api.WithDbAsync(async db => (await db.AgencySettings.SingleAsync()).PublicBaseUrl);
        await SetBaseUrlAsync(url);
        try
        {
            return await action();
        }
        finally
        {
            await SetBaseUrlAsync(before);
        }
    }

    private Task<int> SetBaseUrlAsync(string? url) => api.WithDbAsync(async db =>
    {
        (await db.AgencySettings.SingleAsync()).PublicBaseUrl = url;
        return await db.SaveChangesAsync();
    });

    private static async Task<T> AsAgencyAsync<T>(WebApplicationFactory<Program> factory, Func<MetaConnectionService, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        return await action(scope.ServiceProvider.GetRequiredService<MetaConnectionService>());
    }

    private static FakeAdmin Healthy(Seeded s, bool messenger = false)
    {
        var admin = new FakeAdmin();
        var (_, waExternal, waToken) = s.Channels[ChannelKind.WhatsApp];
        admin.Tokens[waToken] = new MetaTokenInfo(true, AppId, "SYSTEM_USER", null);
        admin.Phones[waExternal] = new MetaPhoneInfo("+57 300 0000000", "CONNECTED", new Dictionary<string, string>());
        admin.Subscribed["waba1"] = [new MetaSubscribedApp(AppId, [])];
        admin.Subs.Add(new MetaAppSubscription("whatsapp_business_account", s.WebhookUrl, true, ["messages", "account_update"]));
        if (messenger)
        {
            var (_, page, pageToken) = s.Channels[ChannelKind.Messenger];
            admin.Tokens[pageToken] = new MetaTokenInfo(true, AppId, "PAGE", null);
            admin.Subscribed[page] = [new MetaSubscribedApp(AppId, ["messages"])];
            admin.Subs.Add(new MetaAppSubscription("page", s.WebhookUrl, true, ["messages"]));
        }

        return admin;
    }

    private static ConnectionCheck Check(ConnectionReport r, string id) => r.Checks.Single(c => c.Id == id);

    [Fact]
    public async Task A_correct_connection_reports_everything_in_order()
    {
        var s = await SeedAsync(kinds: [ChannelKind.WhatsApp, ChannelKind.Messenger]);
        var admin = Healthy(s, messenger: true);

        var report = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.DiagnoseAsync(s.BotId, CancellationToken.None))).Value!);

        Assert.True(report.Healthy, string.Join(" | ", report.Checks.Where(c => c.Status != "ok").Select(c => c.Title + ": " + c.Detail)));
        Assert.False(report.CanRepair);
        Assert.Equal(s.WebhookUrl, report.WebhookUrl);
        Assert.Empty(admin.Calls); // diagnosticar nunca modifica nada en Meta
    }

    [Fact]
    public async Task Repair_subscribes_the_app_and_fixes_the_webhook_and_the_result_is_read_back_from_Meta()
    {
        var s = await SeedAsync(kinds: ChannelKind.WhatsApp);
        var admin = Healthy(s);
        admin.Subscribed.Remove("waba1"); // la app no está suscrita al WABA: Meta no envía nada
        admin.Subs.Clear();
        admin.Subs.Add(new MetaAppSubscription("whatsapp_business_account", "https://facilcorreo.example/meta/webhook", true, ["account_update"])); // apunta a otro lado y sin «messages»

        var factory = Factory(admin);
        var (before, repaired) = await WithBaseUrlAsync(BaseUrl, async () =>
        {
            var b = (await AsAgencyAsync(factory, svc => svc.DiagnoseAsync(s.BotId, CancellationToken.None))).Value!;
            var r = (await AsAgencyAsync(factory, svc => svc.RepairAsync(s.BotId, CancellationToken.None))).Value!;
            return (b, r);
        });

        Assert.False(before.Healthy);
        Assert.True(before.CanRepair);
        Assert.Equal("error", Check(before, "waba").Status);
        Assert.True(Check(before, "waba").Fixable);
        Assert.Equal("error", Check(before, "app_webhook").Status);

        Assert.Equal(2, repaired.Applied.Count);
        Assert.All(repaired.Applied, a => Assert.True(a.Success, a.Message));
        Assert.Contains("subscribe:waba1", admin.Calls);
        Assert.Contains("set-app-webhook:whatsapp_business_account", admin.Calls);
        Assert.True(repaired.Report.Healthy);

        // El webhook conserva los campos que ya tenía la app y añade «messages».
        var sub = Assert.Single(admin.Subs);
        Assert.Equal(s.WebhookUrl, sub.CallbackUrl);
        Assert.Contains("messages", sub.Fields);
        Assert.Contains("account_update", sub.Fields);
    }

    [Fact]
    public async Task Repair_swaps_a_system_user_token_for_the_page_token_on_Messenger()
    {
        var s = await SeedAsync(kinds: [ChannelKind.WhatsApp, ChannelKind.Messenger]);
        var admin = Healthy(s);
        var (channelId, page, systemToken) = s.Channels[ChannelKind.Messenger];
        admin.Tokens[systemToken] = new MetaTokenInfo(true, AppId, "SYSTEM_USER", null);
        admin.PageTokens[page] = "page-token-de-prueba-largo";
        admin.Subs.Add(new MetaAppSubscription("page", s.WebhookUrl, true, ["messages"]));

        var repaired = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.RepairAsync(s.BotId, CancellationToken.None))).Value!);

        Assert.Contains(repaired.Applied, a => a.Title.Contains("token de la página", StringComparison.Ordinal) && a.Success);
        Assert.Contains("subscribe:" + page, admin.Calls);

        // El canal queda guardado (cifrado) con el token de la página y activo.
        var stored = await api.WithDbAsync(db => db.Channels.SingleAsync(c => c.Id == channelId));
        Assert.Equal("page-token-de-prueba-largo", api.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.AccessTokenEncrypted));
        Assert.Equal(ChannelStatus.Active, stored.Status);
    }

    [Fact]
    public async Task A_system_user_without_the_page_assigned_gets_a_manual_instruction_not_a_fix()
    {
        var s = await SeedAsync(kinds: [ChannelKind.WhatsApp, ChannelKind.Messenger]);
        var admin = Healthy(s);
        admin.Tokens[s.Channels[ChannelKind.Messenger].Token] = new MetaTokenInfo(true, AppId, "SYSTEM_USER", null); // sin token de página disponible

        var report = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.DiagnoseAsync(s.BotId, CancellationToken.None))).Value!);

        var check = Check(report, "page_token");
        Assert.Equal("error", check.Status);
        Assert.False(check.Fixable);
        Assert.Contains("asigna la página", check.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sin permiso", check.Detail, StringComparison.Ordinal); // el motivo que dio Meta
    }

    [Fact]
    public async Task A_rejected_token_blocks_the_rest_for_that_channel_with_a_clear_message()
    {
        var s = await SeedAsync(kinds: ChannelKind.WhatsApp);
        var admin = Healthy(s);
        admin.Tokens.Clear(); // Meta ya no reconoce el token

        var report = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.DiagnoseAsync(s.BotId, CancellationToken.None))).Value!);

        var token = Check(report, "token");
        Assert.Equal("error", token.Status);
        Assert.False(token.Fixable);
        Assert.DoesNotContain(report.Checks, c => c.Id is "phone" or "waba"); // sin token válido no se pregunta más a Meta por ese canal
    }

    [Fact]
    public async Task A_missing_WABA_id_is_asked_for_not_guessed()
    {
        var s = await SeedAsync(wabaId: null, kinds: ChannelKind.WhatsApp);
        var admin = Healthy(s);

        var report = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.DiagnoseAsync(s.BotId, CancellationToken.None))).Value!);

        var waba = Check(report, "waba");
        Assert.Equal("warning", waba.Status);
        Assert.False(waba.Fixable);
        Assert.Empty(admin.Calls);
    }

    [Fact]
    public async Task Without_the_app_secret_or_the_public_address_nothing_is_changed_in_Meta()
    {
        var noSecret = await SeedAsync(appSecret: null, kinds: ChannelKind.WhatsApp);
        var admin = Healthy(noSecret);
        admin.Subs.Clear();

        var withoutSecret = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(Factory(admin), svc => svc.RepairAsync(noSecret.BotId, CancellationToken.None))).Value!);
        Assert.Equal("error", Check(withoutSecret.Report, "app_secret").Status);
        Assert.DoesNotContain("set-app-webhook:whatsapp_business_account", admin.Calls); // sin clave de la app no se toca el webhook

        var seeded = await SeedAsync(kinds: ChannelKind.WhatsApp);
        var withoutUrl = await WithBaseUrlAsync(null, async () => (await AsAgencyAsync(Factory(Healthy(seeded)), svc => svc.DiagnoseAsync(seeded.BotId, CancellationToken.None))).Value!);
        Assert.Equal("error", Check(withoutUrl, "public_url").Status);
        Assert.Single(withoutUrl.Checks);
    }

    [Fact]
    public async Task A_second_repair_changes_nothing()
    {
        var s = await SeedAsync(kinds: ChannelKind.WhatsApp);
        var admin = Healthy(s);
        admin.Subscribed.Clear();
        var factory = Factory(admin);

        await WithBaseUrlAsync(BaseUrl, async () => await AsAgencyAsync(factory, svc => svc.RepairAsync(s.BotId, CancellationToken.None)));
        var calls = admin.Calls.Count;
        var second = await WithBaseUrlAsync(BaseUrl, async () => (await AsAgencyAsync(factory, svc => svc.RepairAsync(s.BotId, CancellationToken.None))).Value!);

        Assert.Empty(second.Applied);
        Assert.Equal(calls, admin.Calls.Count);
    }

    [Fact]
    public async Task An_unknown_bot_is_not_found()
    {
        var result = await AsAgencyAsync(Factory(new FakeAdmin()), svc => svc.DiagnoseAsync(Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal("not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Only_the_agency_can_use_the_endpoints_and_the_result_is_exposed_as_JSON()
    {
        var s = await SeedAsync(kinds: ChannelKind.WhatsApp);
        var agencyEmail = $"agencia-{Guid.NewGuid():N}@x.test";
        var adminEmail = $"admin-{Guid.NewGuid():N}@x.test";
        await api.CreateUserAsync(agencyEmail, UserRole.AgencyAdmin, null);
        var clientId = await api.WithDbAsync(db => db.Bots.Where(b => b.Id == s.BotId).Select(b => b.ClientId).SingleAsync());
        await api.CreateUserAsync(adminEmail, UserRole.ClientAdmin, clientId);

        // El administrador del cliente no entra (las integraciones de Meta son de la agencia).
        using var clientAdmin = await api.LoginAsync(adminEmail, clientId);
        Assert.Equal(HttpStatusCode.Forbidden, (await clientAdmin.GetAsync($"/panel/bots/{s.BotId}/meta/connection")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clientAdmin.PostAsync($"/panel/bots/{s.BotId}/meta/connection/repair", null)).StatusCode);

        var factory = Factory(Healthy(s));
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        Assert.True((await http.PostAsJsonAsync("/panel/auth/login", new { email = agencyEmail, password = ApiFixture.Password })).IsSuccessStatusCode);

        var response = await WithBaseUrlAsync(BaseUrl, () => http.GetAsync($"/panel/bots/{s.BotId}/meta/connection"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var connection = doc.RootElement.GetProperty("connection");
        Assert.True(connection.GetProperty("healthy").GetBoolean());
        Assert.Equal(s.WebhookUrl, connection.GetProperty("webhook_url").GetString());
        Assert.Equal("ok", connection.GetProperty("checks")[0].GetProperty("status").GetString());
    }
}
