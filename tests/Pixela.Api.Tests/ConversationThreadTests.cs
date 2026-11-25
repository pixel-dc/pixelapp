using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;

namespace Pixela.Api.Tests;

/// <summary>
/// La bandeja muestra un solo hilo por contacto de un canal (como el historial de Messenger) aunque por debajo haya una conversación de 24 horas por
/// cada vez que volvió a escribir. Responder sigue permitido solo dentro de las 24 horas del último mensaje del contacto, como exige Meta.
/// </summary>
[Collection(ApiTestGroup.Name)]
public class ConversationThreadTests(ApiFixture api)
{
    private sealed record World(Guid ClientId, Guid BotId, Guid ChannelId, Guid[] ThreadA, Guid ThreadB, Guid Web);

    /// <summary>
    /// Contacto A: tres conversaciones (hace 5 días, hace 2 días y la última, de hoy). Contacto B: una. Más una del chat web.
    /// La última de A empieza con un mensaje del contacto hace <paramref name="lastUserAgo"/>.
    /// </summary>
    private async Task<World> SeedAsync(TimeSpan lastUserAgo, bool oldNeedsAttention = false)
    {
        var clientId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var botId = Guid.CreateVersion7();
        var channelId = Guid.CreateVersion7();
        var protector = api.Services.GetRequiredService<ISecretProtector>();
        var now = DateTimeOffset.UtcNow;
        var a = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var b = Guid.CreateVersion7();
        var web = Guid.CreateVersion7();

        await api.WithDbAsync(async db =>
        {
            db.Clients.Add(new Client { Id = clientId, Name = "Negocio", Slug = "t-" + clientId.ToString("N")[..20], CreatedAt = now });
            db.Assets.Add(new Asset { Id = assetId, ClientId = clientId, Name = "Sede", CreatedAt = now });
            db.Bots.Add(new Bot { Id = botId, ClientId = clientId, AssetId = assetId, PublicKey = "bt_" + Guid.NewGuid().ToString("N"), Name = "Bot", CreatedAt = now });
            db.Channels.Add(new Channel { Id = channelId, ClientId = clientId, BotId = botId, Kind = ChannelKind.WhatsApp, ExternalId = "ph" + Guid.NewGuid().ToString("N")[..10], DisplayName = "WA", AccessTokenEncrypted = protector.Protect("tok-largo-de-prueba-000"), CreatedAt = now });

            void Conversation(Guid id, Guid? channel, string? contact, DateTimeOffset created, bool attention, params (string Role, string Text, DateTimeOffset At)[] messages)
            {
                db.Conversations.Add(new Conversation
                {
                    Id = id,
                    ClientId = clientId,
                    BotId = botId,
                    ChannelId = channel,
                    ContactId = contact,
                    ContactName = contact == "573001112233" ? "Ana" : null,
                    CreatedAt = created,
                    LastMessageAt = messages.Max(m => m.At),
                    MessageCount = messages.Length,
                    NeedsAttention = attention,
                });
                foreach (var m in messages)
                {
                    db.ChatMessages.Add(new ChatMessage { Id = Guid.CreateVersion7(), ClientId = clientId, ConversationId = id, Role = m.Role, Content = m.Text, CreatedAt = m.At });
                }
            }

            var d5 = now.AddDays(-5);
            var d2 = now.AddDays(-2);
            var last = now - lastUserAgo;
            Conversation(a[0], channelId, "573001112233", d5, oldNeedsAttention, ("user", "Hola, ¿precios?", d5), ("assistant", "Claro, te cuento.", d5.AddMinutes(1)));
            Conversation(a[1], channelId, "573001112233", d2, false, ("user", "¿Y el horario?", d2), ("assistant", "De 8 a 18.", d2.AddMinutes(1)), ("agent", "Te escribo yo", d2.AddMinutes(5)));
            Conversation(a[2], channelId, "573001112233", last, false, ("user", "Buenas de nuevo", last), ("assistant", "¡Hola Ana!", last.AddMinutes(1)));
            Conversation(b, channelId, "573009998877", now.AddHours(-1), false, ("user", "Otra persona", now.AddHours(-1)));
            Conversation(web, null, null, now.AddHours(-3), false, ("user", "Desde la web", now.AddHours(-3)));
            await db.SaveChangesAsync();
            return 0;
        });
        return new World(clientId, botId, channelId, a, b, web);
    }

    private async Task<T> WithServiceAsync<T>(Func<ConversationService, Task<T>> action)
    {
        var (scope, service) = api.Service<ConversationService>();
        await using (scope)
        {
            return await action(service);
        }
    }

    [Fact]
    public async Task A_contact_with_several_conversations_appears_once_with_its_whole_history()
    {
        var w = await SeedAsync(TimeSpan.FromHours(2));

        var list = (await WithServiceAsync(s => s.ListConversationsAsync(w.BotId, CancellationToken.None))).Value!;

        Assert.Equal(3, list.Count); // contacto A, contacto B y el chat web (no 5)
        var ana = Assert.Single(list, c => c.Contact == "Ana (573001112233)");
        Assert.Equal(3, ana.Sessions);
        Assert.Equal(7, ana.Messages); // 2 + 3 + 2: todo el historial
        Assert.Equal(w.ThreadA[2], ana.Id); // se responde a la conversación más reciente
        Assert.True(ana.StartedAt < DateTimeOffset.UtcNow.AddDays(-4)); // el hilo empezó hace 5 días
        Assert.Equal(1, Assert.Single(list, c => c.Contact == "573009998877").Sessions);
        Assert.Null(Assert.Single(list, c => c.Channel is null).Contact); // el chat web sigue siendo uno por visitante
    }

    [Fact]
    public async Task The_transcript_is_one_chat_in_order_and_says_where_each_conversation_starts()
    {
        var w = await SeedAsync(TimeSpan.FromHours(2));

        // Es el mismo historial se pida por la conversación reciente o por una antigua.
        var fromLatest = (await WithServiceAsync(s => s.GetTranscriptAsync(w.BotId, w.ThreadA[2], CancellationToken.None))).Value!;
        var fromOldest = (await WithServiceAsync(s => s.GetTranscriptAsync(w.BotId, w.ThreadA[0], CancellationToken.None))).Value!;

        Assert.Equal(fromLatest.Select(m => m.Content), fromOldest.Select(m => m.Content));
        Assert.Equal(
            ["Hola, ¿precios?", "Claro, te cuento.", "¿Y el horario?", "De 8 a 18.", "Te escribo yo", "Buenas de nuevo", "¡Hola Ana!"],
            fromLatest.Select(m => m.Content));
        Assert.Equal([w.ThreadA[0], w.ThreadA[0], w.ThreadA[1], w.ThreadA[1], w.ThreadA[1], w.ThreadA[2], w.ThreadA[2]], fromLatest.Select(m => m.ConversationId!.Value));
        Assert.DoesNotContain(fromLatest, m => m.Content == "Otra persona"); // otro contacto no se mezcla
    }

    [Fact]
    public async Task The_customer_can_only_be_answered_inside_24_hours_of_their_last_message()
    {
        var fresh = await SeedAsync(TimeSpan.FromHours(2));
        var stale = await SeedAsync(TimeSpan.FromHours(30));

        var freshList = (await WithServiceAsync(s => s.ListConversationsAsync(fresh.BotId, CancellationToken.None))).Value!;
        var staleList = (await WithServiceAsync(s => s.ListConversationsAsync(stale.BotId, CancellationToken.None))).Value!;

        Assert.True(Assert.Single(freshList, c => c.Sessions == 3).CanReply);
        Assert.False(Assert.Single(staleList, c => c.Sessions == 3).CanReply); // se ve todo el historial, pero no se puede escribir hasta que el cliente vuelva a escribir

        var reply = await WithServiceAsync(s => s.ReplyAsync(stale.BotId, stale.ThreadA[2], new AgentReplyRequest("hola"), CancellationToken.None));
        Assert.Equal("invalid_state", reply.Error!.Code);
        Assert.Contains("24 horas", reply.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Needing_attention_in_any_conversation_marks_the_thread_and_giving_it_back_clears_it_everywhere()
    {
        var w = await SeedAsync(TimeSpan.FromHours(2), oldNeedsAttention: true);

        var before = (await WithServiceAsync(s => s.ListConversationsAsync(w.BotId, CancellationToken.None))).Value!;
        Assert.True(Assert.Single(before, c => c.Sessions == 3).NeedsAttention); // la marca estaba en la conversación antigua

        var released = await WithServiceAsync(s => s.ReleaseAsync(w.BotId, w.ThreadA[2], CancellationToken.None));
        Assert.True(released.IsSuccess);

        var after = (await WithServiceAsync(s => s.ListConversationsAsync(w.BotId, CancellationToken.None))).Value!;
        Assert.False(Assert.Single(after, c => c.Sessions == 3).NeedsAttention);
        Assert.DoesNotContain(await api.WithDbAsync(db => db.Conversations.Where(c => c.BotId == w.BotId).ToListAsync()), c => c.NeedsAttention);
    }

    [Fact]
    public async Task The_inbox_also_shows_one_thread_per_contact_with_the_ones_that_need_attention_first()
    {
        var w = await SeedAsync(TimeSpan.FromHours(2), oldNeedsAttention: true);

        var inbox = await WithServiceAsync(s => s.ListInboxAsync(onlyAttention: false, CancellationToken.None));

        var mine = inbox.Where(c => c.BotId == w.BotId).ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal(3, mine[0].Sessions); // el hilo que espera atención va primero
        Assert.True(mine[0].NeedsAttention);
        Assert.Equal("Bot", mine[0].BotName);
    }

    [Fact]
    public async Task Deleting_a_contact_deletes_its_whole_thread_and_nothing_else()
    {
        var w = await SeedAsync(TimeSpan.FromHours(2));

        Assert.True(await WithServiceAsync(s => s.DeleteConversationAsync(w.BotId, w.ThreadA[2], CancellationToken.None)));

        var left = await api.WithDbAsync(db => db.Conversations.Where(c => c.BotId == w.BotId).Select(c => c.Id).ToListAsync());
        Assert.Equivalent(new[] { w.ThreadB, w.Web }, left);
        Assert.Equal(2, await api.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ClientId == w.ClientId))); // solo los de B y el chat web
    }
}
