using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Chat;
using Pixela.Domain.Clients;

namespace Pixela.Api.Tests;

/// <summary>Motor del chat contra PostgreSQL real: bots por asset, secretos por bot, topes del cliente y consumo por bot.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ChatEngineTests(ApiFixture api)
{
    private const string Key = "sk-ant-api03-prueba-0123456789abcdef";

    [Fact]
    public async Task Cada_bot_nace_con_su_propio_token_de_verificacion_de_Meta()
    {
        var a = await SeedBotAsync();
        var b = await SeedBotAsync();

        var (scope, inbox) = api.Service<MetaInboxService>();
        await using (scope)
        {
            var appA = await inbox.ResolveBotAppAsync(a.PublicKey, default);
            var appB = await inbox.ResolveBotAppAsync(b.PublicKey, default);

            Assert.NotNull(appA);
            Assert.NotNull(appB);
            Assert.StartsWith("pxv_", appA.VerifyToken, StringComparison.Ordinal);
            Assert.NotEqual(appA.VerifyToken, appB.VerifyToken);
            Assert.Null(appA.AppSecret); // la app de Meta del bot se configura después
            Assert.Equal(a.ClientId, appA.ClientId);
            Assert.Null(await inbox.ResolveBotAppAsync("bt_no-existe", default));
        }

        // En la base el token queda cifrado.
        var (dbScope, db) = api.Database();
        await using (dbScope)
        {
            var stored = await db.BotSecrets.AsNoTracking().SingleAsync(s => s.BotId == a.BotId);
            Assert.StartsWith("v1.", stored.MetaVerifyTokenEncrypted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sin_llave_el_bot_responde_sin_modelo_y_sin_costo()
    {
        var bot = await SeedBotAsync(withFaq: true);
        var before = api.Ai.Requests.Count;

        var reply = await ChatAsync(bot.PublicKey, "¿A qué hora abren?");

        Assert.Equal("Abrimos de 8 a 18.", reply.Reply);
        Assert.Equal(before, api.Ai.Requests.Count);
        var usage = await UsageAsync(bot.BotId);
        Assert.Equal((1, 1, 0L, 0L), (usage.Conversations, usage.Messages, usage.TokensIn, usage.CostMicros));
    }

    [Fact]
    public async Task Con_llave_usa_la_del_bot_y_el_modelo_de_la_agencia_y_mide_el_costo()
    {
        var bot = await SeedBotAsync(withFaq: true);
        var (scope, keys) = api.Service<BotKeyService>();
        await using (scope)
        {
            var set = await keys.SetAsync(bot.BotId, new SetAiKeyRequest(Key), default);
            Assert.True(set.IsSuccess);
            Assert.Equal("cdef", set.Value!.Hint);
        }

        var reply = await ChatAsync(bot.PublicKey, "¿A qué hora abren?");

        Assert.StartsWith("Según el negocio:", reply.Reply, StringComparison.Ordinal);
        var request = api.Ai.Requests.Last(r => r.ApiKey == Key);
        Assert.Equal("claude-haiku-5-5", request.Model);
        Assert.Equal("low", request.Effort);

        // 1.000 tokens de entrada a 0,10 USD/M + 100 de salida a 0,50 USD/M = 150 micro-USD, en el consumo del bot.
        var usage = await UsageAsync(bot.BotId);
        Assert.Equal((1000L, 100L, 150L), (usage.TokensIn, usage.TokensOut, usage.CostMicros));
    }

    [Fact]
    public async Task Un_cliente_no_ve_la_llave_de_un_bot_de_otro_cliente()
    {
        var mine = await SeedBotAsync();
        var other = await SeedBotAsync();
        var (scope, keys) = api.Service<BotKeyService>(mine.ClientId);
        await using (scope)
        {
            Assert.True((await keys.GetAsync(mine.BotId, default)).IsSuccess);
            Assert.Equal("not_found", (await keys.GetAsync(other.BotId, default)).Error!.Code);
            Assert.Equal("not_found", (await keys.SetAsync(other.BotId, new SetAiKeyRequest(Key), default)).Error!.Code);
        }
    }

    [Fact]
    public async Task El_tope_mensual_del_cliente_cuenta_todos_sus_bots()
    {
        var first = await SeedBotAsync(withFaq: true, monthlyCap: 1);
        var second = await SeedBotAsync(withFaq: true, clientId: first.ClientId);

        await ChatAsync(first.PublicKey, "¿A qué hora abren?");

        var (scope, _) = api.Database(unset: true);
        await using (scope)
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            var start = await chat.StartAsync(second.PublicKey, default);
            Assert.Equal("limit_reached", start.Error!.Code);
        }
    }

    [Fact]
    public async Task Sin_topes_configurados_el_cliente_es_ilimitado()
    {
        var bot = await SeedBotAsync(withFaq: true);
        for (var i = 0; i < 3; i++)
        {
            await ChatAsync(bot.PublicKey, "¿A qué hora abren?");
        }

        Assert.Equal(3, (await UsageAsync(bot.BotId)).Conversations);
    }

    [Fact]
    public async Task Un_cliente_suspendido_no_atiende()
    {
        var bot = await SeedBotAsync(withFaq: true, status: ClientStatus.Suspended);
        var (scope, _) = api.Database(unset: true);
        await using (scope)
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            Assert.Equal("not_found", (await chat.StartAsync(bot.PublicKey, default)).Error!.Code);
        }
    }

    private async Task<ChatReply> ChatAsync(string publicKey, string text)
    {
        var (scope, _) = api.Database(unset: true);
        await using (scope)
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            var session = await chat.StartAsync(publicKey, default);
            Assert.True(session.IsSuccess, session.Error?.Message);
            var reply = await chat.SendAsync(session.Value!.Token, text, default);
            Assert.True(reply.IsSuccess, reply.Error?.Message);
            return reply.Value!;
        }
    }

    private async Task<Domain.Chat.ChatUsage> UsageAsync(Guid botId)
    {
        var (scope, db) = api.Database();
        await using (scope)
        {
            return await db.ChatUsages.AsNoTracking().SingleAsync(u => u.BotId == botId);
        }
    }

    private sealed record SeededBot(Guid ClientId, Guid BotId, string PublicKey);

    /// <summary>Crea (o reutiliza) un cliente con un asset y un bot hecho por <see cref="BotService"/>, como lo haría la agencia.</summary>
    private async Task<SeededBot> SeedBotAsync(bool withFaq = false, int? monthlyCap = null, ClientStatus status = ClientStatus.Active, Guid? clientId = null)
    {
        var (scope, db) = api.Database();
        await using (scope)
        {
            var now = DateTimeOffset.UtcNow;
            var id = clientId ?? Guid.NewGuid();
            if (clientId is null)
            {
                db.Clients.Add(new Client { Id = id, Name = "Cliente", Slug = $"c-{Guid.NewGuid():N}"[..30], Status = status, MonthlyConversationCap = monthlyCap, CreatedAt = now });
            }

            var asset = new Asset { Id = Guid.NewGuid(), ClientId = id, Name = "Sede", CreatedAt = now };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            var bots = scope.ServiceProvider.GetRequiredService<BotService>();
            var created = await bots.CreateAsync(asset.Id, new BotRequest("Asistente", null, null, null, null, null, null, null), default);
            Assert.True(created.IsSuccess, created.Error?.Message);
            var bot = created.Value!;
            Assert.Equal(asset.Id, bot.AssetId);

            if (withFaq)
            {
                var knowledge = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
                var faq = await knowledge.AddFaqAsync(bot.Id, new AddFaqRequest("Horarios", [new FaqItem("¿A qué hora abren?", "Abrimos de 8 a 18.")]), default);
                Assert.True(faq.IsSuccess, faq.Error?.Message);
            }

            return new SeededBot(id, bot.Id, bot.PublicKey);
        }
    }
}
