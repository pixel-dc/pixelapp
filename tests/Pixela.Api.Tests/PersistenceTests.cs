using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pixela.Domain.Agency;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class PersistenceTests(ApiFixture api)
{
    [Fact]
    public async Task Arranca_con_la_base_migrada_y_la_agencia_creada()
    {
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/health", UriKind.Relative))).StatusCode);

        var (scope, db) = api.Database();
        await using (scope)
        {
            var agency = await db.AgencySettings.SingleAsync();
            Assert.Equal(AgencySettings.SingletonId, agency.Id);
            Assert.Equal("Pixel DC", agency.Name);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
    }

    [Fact]
    public async Task Cada_cliente_solo_ve_lo_suyo_y_la_agencia_ve_todo()
    {
        var a = await SeedClientWithBotAsync("a");
        var b = await SeedClientWithBotAsync("b");

        var (scopeA, dbA) = api.Database(a.ClientId);
        await using (scopeA)
        {
            Assert.Equal([a.ClientId], await dbA.Clients.Select(c => c.Id).ToListAsync());
            Assert.Equal([a.BotId], await dbA.Bots.Select(x => x.Id).ToListAsync());
            Assert.Equal([a.BotId], await dbA.BotSecrets.Select(x => x.BotId).ToListAsync());
            Assert.All(await dbA.Assets.Select(x => x.ClientId).ToListAsync(), id => Assert.Equal(a.ClientId, id));
            Assert.Null(await dbA.Bots.FirstOrDefaultAsync(x => x.Id == b.BotId));
        }

        var (scopeAgency, agency) = api.Database();
        await using (scopeAgency)
        {
            var bots = await agency.Bots.Select(x => x.Id).ToListAsync();
            Assert.Contains(a.BotId, bots);
            Assert.Contains(b.BotId, bots);
        }

        // Sin alcance (petición anónima sin resolver) no se ve nada.
        var (scopeNone, none) = api.Database(unset: true);
        await using (scopeNone)
        {
            Assert.Empty(await none.Bots.ToListAsync());
            Assert.Empty(await none.Clients.ToListAsync());
        }
    }

    [Fact]
    public async Task Los_usuarios_de_la_agencia_no_aparecen_para_un_cliente()
    {
        var client = await SeedClientWithBotAsync("u");
        var (scope, db) = api.Database();
        await using (scope)
        {
            db.Users.Add(NewUser($"agencia-{Guid.NewGuid():N}@pixeldc.test", UserRole.AgencyAdmin, null));
            db.Users.Add(NewUser($"admin-{Guid.NewGuid():N}@cliente.test", UserRole.ClientAdmin, client.ClientId));
            await db.SaveChangesAsync();
        }

        var (clientScope, clientDb) = api.Database(client.ClientId);
        await using (clientScope)
        {
            Assert.Equal([UserRole.ClientAdmin], await clientDb.Users.Select(x => x.Role).ToListAsync());
        }
    }

    [Theory]
    [InlineData(UserRole.AgencyAdmin, true)]
    [InlineData(UserRole.Operator, false)]
    public async Task La_base_rechaza_roles_en_el_lugar_equivocado(UserRole role, bool withClient)
    {
        var client = await SeedClientWithBotAsync("r");
        var (scope, db) = api.Database();
        await using (scope)
        {
            db.Users.Add(NewUser($"x-{Guid.NewGuid():N}@test.test", role, withClient ? client.ClientId : null));

            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("ck_users_role_client", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        }
    }

    [Fact]
    public async Task El_consumo_se_acumula_por_bot_y_mes()
    {
        var c = await SeedClientWithBotAsync("uso");
        var (scope, db) = api.Database();
        await using (scope)
        {
            var now = DateTimeOffset.UtcNow;
            await db.RecordChatUsageAsync(c.BotId, c.ClientId, 202611, 1, 2, 100, 50, 7, now, default, ChannelKind.WhatsApp);
            await db.RecordChatUsageAsync(c.BotId, c.ClientId, 202611, 0, 2, 10, 5, 1, now, default);
            await db.RecordWhatsAppSentAsync(c.BotId, c.ClientId, 202611, 3, now, default);

            var usage = await db.ChatUsages.AsNoTracking().SingleAsync(x => x.BotId == c.BotId);
            Assert.Equal(
                (1, 1, 4, 110L, 55L, 8L, 3),
                (usage.Conversations, usage.ConversationsWhatsApp, usage.Messages, usage.TokensIn, usage.TokensOut, usage.CostMicros, usage.WhatsAppMessagesSent));
        }
    }

    [Fact]
    public async Task Borrar_un_bot_conserva_su_consumo()
    {
        var c = await SeedClientWithBotAsync("del");
        var (scope, db) = api.Database();
        await using (scope)
        {
            await db.RecordChatUsageAsync(c.BotId, c.ClientId, 202611, 1, 1, 1, 1, 1, DateTimeOffset.UtcNow, default);

            await db.DeleteBotCascadeAsync(c.BotId, default);

            Assert.False(await db.Bots.AnyAsync(x => x.Id == c.BotId));
            Assert.False(await db.BotSecrets.AnyAsync(x => x.BotId == c.BotId));
            Assert.True(await db.ChatUsages.AnyAsync(x => x.BotId == c.BotId));
        }
    }

    private async Task<(Guid ClientId, Guid BotId)> SeedClientWithBotAsync(string prefix)
    {
        var (scope, db) = api.Database();
        await using (scope)
        {
            var now = DateTimeOffset.UtcNow;
            var client = new Client { Id = Guid.NewGuid(), Name = $"Cliente {prefix}", Slug = $"{prefix}-{Guid.NewGuid():N}"[..30], CreatedAt = now };
            var asset = new Asset { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Sede principal", CreatedAt = now };
            var bot = new Bot { Id = Guid.NewGuid(), ClientId = client.Id, AssetId = asset.Id, Name = "Asistente", PublicKey = Guid.NewGuid().ToString("N"), CreatedAt = now };
            db.Clients.Add(client);
            db.Assets.Add(asset);
            db.Bots.Add(bot);
            db.BotSecrets.Add(new BotSecrets { BotId = bot.Id, ClientId = client.Id, MetaVerifyTokenEncrypted = "v1.x", UpdatedAt = now });
            await db.SaveChangesAsync();
            return (client.Id, bot.Id);
        }
    }

    private static User NewUser(string email, UserRole role, Guid? clientId) =>
        new() { Id = Guid.NewGuid(), Email = email, PasswordHash = "x", Role = role, ClientId = clientId, CreatedAt = DateTimeOffset.UtcNow };
}
