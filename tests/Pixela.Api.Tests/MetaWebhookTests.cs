using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Infrastructure.Chat;
using Pixela.Infrastructure.Maintenance;

namespace Pixela.Api.Tests;

/// <summary>
/// Webhook de Meta por bot (<c>/meta/webhook/{clave}</c>): verificación con el token del bot, firma con la app del bot, cola, respuesta por el
/// mismo canal, reintentos sin repetir y aislamiento entre bots y clientes.
/// </summary>
[Collection(ApiTestGroup.Name)]
public class MetaWebhookTests(ApiFixture api)
{
    private const string AppSecret = "secreto-de-prueba-de-la-app";
    private static readonly string AttachmentDir = Path.Combine(Path.GetTempPath(), "px-att-" + Guid.NewGuid().ToString("N"));

    /// <summary>Bot al que va cada cuerpo armado con <see cref="Payload"/>: así cada POST llega a la URL del bot dueño del canal.</summary>
    private static readonly ConcurrentDictionary<string, string> BotOfPayload = new();

    /// <summary>Cliente de Meta de mentira: anota lo que se envió y puede fallar en el envío indicado.</summary>
    private sealed class FakeGraph : IMetaGraph
    {
        public List<(ChannelKind Kind, string ChannelId, string Token, string Contact, string Text)> Sent { get; } = [];
        public Func<int, Exception?> FailOn { get; set; } = _ => null;
        public List<InboundAttachment> Downloads { get; } = [];
        public List<string> Images { get; } = [];
        public Exception? ImageFailure { get; set; }
        public Func<InboundAttachment, MediaDownload> Download { get; set; } = _ => new MediaDownload([0xFF, 0xD8, 0xFF, 1, 2, 3], "image/jpeg");

        public Task<MediaDownload> DownloadMediaAsync(ChannelKind kind, string accessToken, InboundAttachment attachment, long maxBytes, CancellationToken ct)
        {
            Downloads.Add(attachment);
            return Task.FromResult(Download(attachment));
        }

        public Task SendImageAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string imageUrl, CancellationToken ct)
        {
            if (ImageFailure is { } ex)
            {
                throw ex;
            }

            Images.Add(imageUrl);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(ChannelKind kind, string externalChannelId, string accessToken, string contactId, string text, CancellationToken ct)
        {
            if (FailOn(Sent.Count + failures) is { } ex)
            {
                failures++;
                throw ex;
            }

            Sent.Add((kind, externalChannelId, accessToken, contactId, text));
            return Task.CompletedTask;
        }

        private int failures;
    }

    private sealed record Seeded(Guid ClientId, Guid BotId, Guid ChannelId, string ExternalId, ChannelKind Kind, string PublicKey, string VerifyToken);

    private WebApplicationFactory<Program> Factory(FakeGraph graph, Action<MetaOptions>? configure = null) =>
        api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Configure<MetaOptions>(o =>
            {
                o.AttachmentsPath = AttachmentDir;
                configure?.Invoke(o);
            });
            s.AddSingleton<IMetaGraph>(graph);
        }));

    /// <summary>Cliente, asset y bot con su app de Meta (clave secreta <paramref name="appSecret"/>; null = sin app), conocimiento y un canal.</summary>
    private async Task<Seeded> SeedAsync(
        ChannelKind kind = ChannelKind.WhatsApp, string? faq = null, string? privacyUrl = null, ChannelStatus status = ChannelStatus.Active, string token = "token-del-cliente", string? appSecret = AppSecret, string? handoffMessage = null)
    {
        var clientId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var botId = Guid.CreateVersion7();
        var channelId = Guid.CreateVersion7();
        var externalId = "ext" + Guid.NewGuid().ToString("N")[..12];
        var publicKey = "bt_" + Guid.NewGuid().ToString("N");
        var verifyToken = BotService.NewVerifyToken();
        var protector = api.Services.GetRequiredService<ISecretProtector>();
        await api.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Clients.Add(new Client { Id = clientId, Name = "Negocio", Slug = "n-" + clientId.ToString("N")[..20], CreatedAt = now });
            db.Assets.Add(new Asset { Id = assetId, ClientId = clientId, Name = "Sede", CreatedAt = now });
            db.Bots.Add(new Bot { Id = botId, ClientId = clientId, AssetId = assetId, PublicKey = publicKey, Name = "Bot", PrivacyUrl = privacyUrl, HandoffMessage = handoffMessage, CreatedAt = now });
            db.BotSecrets.Add(new BotSecrets
            {
                BotId = botId,
                ClientId = clientId,
                MetaVerifyTokenEncrypted = protector.Protect(verifyToken),
                MetaAppSecretEncrypted = appSecret is null ? null : protector.Protect(appSecret),
                UpdatedAt = now,
            });
            var source = new KnowledgeSource { Id = Guid.CreateVersion7(), ClientId = clientId, BotId = botId, Kind = KnowledgeKind.Faq, Title = "FAQ", Status = KnowledgeStatus.Indexed, CreatedAt = now };
            db.KnowledgeSources.Add(source);
            db.KnowledgeChunks.Add(new KnowledgeChunk { Id = Guid.CreateVersion7(), ClientId = clientId, BotId = botId, SourceId = source.Id, Text = faq ?? "Pregunta: ¿Horario?\nRespuesta: De 8 a 18." });
            db.Channels.Add(new Channel { Id = channelId, ClientId = clientId, BotId = botId, Kind = kind, ExternalId = externalId, DisplayName = "Canal", AccessTokenEncrypted = protector.Protect(token), Status = status, CreatedAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        return new Seeded(clientId, botId, channelId, externalId, kind, publicKey, verifyToken);
    }

    private static string Payload(Seeded s, string contact, string messageId, string? text)
    {
        var escaped = text is null ? null : text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        string json;
        if (s.Kind == ChannelKind.WhatsApp)
        {
            var message = text is null
                ? $"{{\"from\":\"{contact}\",\"id\":\"{messageId}\",\"timestamp\":\"1\",\"type\":\"location\",\"location\":{{\"latitude\":1}}}}"
                : $"{{\"from\":\"{contact}\",\"id\":\"{messageId}\",\"timestamp\":\"1\",\"type\":\"text\",\"text\":{{\"body\":\"{escaped}\"}}}}";
            json = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"id\":\"W\",\"changes\":[{\"field\":\"messages\",\"value\":{\"messaging_product\":\"whatsapp\"," +
                $"\"metadata\":{{\"phone_number_id\":\"{s.ExternalId}\"}},\"contacts\":[{{\"profile\":{{\"name\":\"Ana\"}},\"wa_id\":\"{contact}\"}}],\"messages\":[{message}]}}}}]}}]}}";
        }
        else
        {
            var obj = s.Kind == ChannelKind.Messenger ? "page" : "instagram";
            var body = text is null ? "\"attachments\":[{\"type\":\"image\"}]" : $"\"text\":\"{escaped}\"";
            json = $"{{\"object\":\"{obj}\",\"entry\":[{{\"id\":\"{s.ExternalId}\",\"messaging\":[{{\"sender\":{{\"id\":\"{contact}\"}},\"recipient\":{{\"id\":\"{s.ExternalId}\"}},\"message\":{{\"mid\":\"{messageId}\",{body}}}}}]}}]}}";
        }

        BotOfPayload[json] = s.PublicKey;
        return json;
    }

    /// <summary>POST firmado (por defecto con <see cref="AppSecret"/>) a la URL del bot dueño del cuerpo, o a <paramref name="botKey"/>.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? signature = null, bool sign = true, string? botKey = null)
    {
        var key = botKey ?? (BotOfPayload.TryGetValue(json.TrimEnd(), out var k) ? k : "bt_desconocido");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/meta/webhook/" + key) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var bytes = Encoding.UTF8.GetBytes(json);
        if (sign || signature is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", signature ?? MetaSignature.Sign(AppSecret, bytes));
        }

        return await client.SendAsync(request);
    }

    private async Task<List<ChannelInbound>> InboundAsync(Seeded s) =>
        await api.WithDbAsync(db => db.ChannelInbounds.Where(i => i.ChannelId == s.ChannelId).OrderBy(i => i.CreatedAt).ToListAsync());

    /// <summary>Reclama y atiende lo que haya en cola (lo mismo que hace el trabajador), solo de los canales indicados.</summary>
    private static async Task<List<InboundOutcome>> DrainAsync(WebApplicationFactory<Program> factory, params Guid[] channelIds)
    {
        var outcomes = new List<InboundOutcome>();
        await using var claimScope = factory.Services.CreateAsyncScope();
        var ids = await claimScope.ServiceProvider.GetRequiredService<ChannelInboundClaimer>().ClaimAsync(50, TimeSpan.FromMinutes(2), CancellationToken.None);
        foreach (var id in ids)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var belongs = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().ChannelInbounds.IgnoreQueryFilters().AnyAsync(i => i.Id == id && channelIds.Contains(i.ChannelId));
            if (belongs)
            {
                outcomes.Add(await scope.ServiceProvider.GetRequiredService<MetaInboxProcessor>().ProcessAsync(id, CancellationToken.None));
            }
        }

        return outcomes;
    }

    [Fact]
    public async Task The_verification_answers_the_challenge_only_with_the_token_of_that_bot()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();

        var ok = await client.GetAsync(new Uri($"/meta/webhook/{a.PublicKey}?hub.mode=subscribe&hub.verify_token={a.VerifyToken}&hub.challenge=reto-123", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("reto-123", await ok.Content.ReadAsStringAsync());

        // El token de otro bot no sirve, ni el modo equivocado, ni un bot que no existe.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"/meta/webhook/{a.PublicKey}?hub.mode=subscribe&hub.verify_token={b.VerifyToken}&hub.challenge=x", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"/meta/webhook/{a.PublicKey}?hub.mode=unsubscribe&hub.verify_token={a.VerifyToken}&hub.challenge=x", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"/meta/webhook/bt_no_existe?hub.mode=subscribe&hub.verify_token={a.VerifyToken}&hub.challenge=x", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"/meta/webhook/{a.PublicKey}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_regenerated_verify_token_replaces_the_old_one()
    {
        var s = await SeedAsync();
        string fresh;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
            var view = await scope.ServiceProvider.GetRequiredService<BotMetaAppService>().RegenerateVerifyTokenAsync(s.BotId, default);
            fresh = view.Value!.VerifyToken;
            Assert.EndsWith("/meta/webhook/" + s.PublicKey, view.Value.WebhookUrl, StringComparison.Ordinal);
            Assert.True(view.Value.HasAppSecret);
        }

        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();
        Assert.NotEqual(s.VerifyToken, fresh);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"/meta/webhook/{s.PublicKey}?hub.mode=subscribe&hub.verify_token={s.VerifyToken}&hub.challenge=x", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"/meta/webhook/{s.PublicKey}?hub.mode=subscribe&hub.verify_token={fresh}&hub.challenge=x", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_missing_or_wrong_signature_is_rejected_and_nothing_is_queued()
    {
        var s = await SeedAsync();
        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();
        var json = Payload(s, "573001112233", "wamid.1", "¿Horario?");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json, MetaSignature.Sign("otro-secreto", Encoding.UTF8.GetBytes(json)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json, "sha256=zzzz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json + " ", MetaSignature.Sign(AppSecret, Encoding.UTF8.GetBytes(json)))).StatusCode); // cuerpo alterado

        Assert.Empty(await InboundAsync(s));
    }

    [Fact]
    public async Task Each_bot_is_verified_with_its_own_app_and_cannot_feed_the_channels_of_another_bot()
    {
        const string SecretA = "secreto-de-la-app-del-bot-A-0123456789";
        const string SecretB = "secreto-de-la-app-del-bot-B-0123456789";
        var a = await SeedAsync(appSecret: SecretA);
        var b = await SeedAsync(appSecret: SecretB);
        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();

        var jsonA = Payload(a, "573001112233", "wamid.a", "¿Horario?");
        // Firmado con la app de B: la URL de A no lo acepta.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, jsonA, MetaSignature.Sign(SecretB, Encoding.UTF8.GetBytes(jsonA)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, jsonA, MetaSignature.Sign(SecretA, Encoding.UTF8.GetBytes(jsonA)))).StatusCode);
        Assert.Single(await InboundAsync(a));

        // Un mensaje del canal de B, bien firmado con la app de A y enviado a la URL de A, no entra en la cola de B.
        var jsonB = Payload(b, "573009998877", "wamid.b", "¿Horario?");
        var response = await PostAsync(client, jsonB, MetaSignature.Sign(SecretA, Encoding.UTF8.GetBytes(jsonB)), botKey: a.PublicKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"accepted\":0", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await InboundAsync(b));
    }

    [Fact]
    public async Task Events_without_messages_such_as_delivery_statuses_are_acknowledged()
    {
        var s = await SeedAsync();
        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();
        var json = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"id\":\"W\",\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"x\"},\"statuses\":[{\"id\":\"a\",\"status\":\"read\"}]}}]}]}";

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, json, sign: false, botKey: s.PublicKey)).StatusCode);
    }

    [Fact]
    public async Task A_bot_without_meta_app_rejects_every_post()
    {
        var s = await SeedAsync(appSecret: null);
        using var factory = Factory(new FakeGraph());
        using var client = factory.CreateClient();
        var json = Payload(s, "573001112233", "wamid.1", "¿Horario?");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json, MetaSignature.Sign(string.Empty, Encoding.UTF8.GetBytes(json)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, json)).StatusCode);
        Assert.Empty(await InboundAsync(s));
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_anything_else()
    {
        using var factory = Factory(new FakeGraph(), o => o.MaxBodyBytes = 200);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "{\"object\":\"page\",\"entry\":[]," + new string(' ', 500) + "}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData(ChannelKind.WhatsApp)]
    [InlineData(ChannelKind.Messenger)]
    [InlineData(ChannelKind.Instagram)]
    public async Task A_signed_message_is_queued_answered_on_the_same_channel_and_its_content_is_wiped(ChannelKind kind)
    {
        var s = await SeedAsync(kind);
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, Payload(s, "573001112233", "msg-1", "¿Horario?"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queued = Assert.Single(await InboundAsync(s));
        Assert.Equal(InboundStatus.Pending, queued.Status);
        Assert.Equal("¿Horario?", queued.Text);
        Assert.Empty(graph.Sent); // el webhook solo encola: responder lo hace el trabajador

        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        var sent = Assert.Single(graph.Sent);
        Assert.Equal((kind, s.ExternalId, "token-del-cliente", "573001112233", "De 8 a 18."), sent);

        var done = Assert.Single(await InboundAsync(s));
        Assert.Equal(InboundStatus.Done, done.Status);
        Assert.Null(done.Text); // el contenido del mensaje no se conserva
        Assert.Null(done.ReplyText);
        Assert.NotNull(done.ProcessedAt);

        var conversation = await api.WithDbAsync(db => db.Conversations.SingleAsync(c => c.ChannelId == s.ChannelId));
        Assert.Equal("573001112233", conversation.ContactId);
        Assert.Equal(s.ClientId, conversation.ClientId);
        var usage = await api.WithDbAsync(db => db.ChatUsages.SingleAsync(u => u.ClientId == s.ClientId));
        Assert.Equal(1, usage.Conversations);
    }

    [Fact]
    public async Task Meta_retrying_the_same_message_does_not_create_a_second_reply()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        var json = Payload(s, "573001112233", "wamid.dup", "¿Horario?");

        await PostAsync(client, json);
        await PostAsync(client, json);
        await PostAsync(client, json);
        await DrainAsync(factory, s.ChannelId);
        await PostAsync(client, json); // incluso después de atendido
        await DrainAsync(factory, s.ChannelId);

        Assert.Single(await InboundAsync(s));
        Assert.Single(graph.Sent);
    }

    [Fact]
    public async Task Messages_for_unknown_or_disabled_channels_are_ignored()
    {
        var disabled = await SeedAsync(status: ChannelStatus.Disabled);
        var tokenError = await SeedAsync(status: ChannelStatus.TokenError);
        var unknown = new Seeded(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "no-existe-" + Guid.NewGuid().ToString("N")[..8], ChannelKind.WhatsApp, disabled.PublicKey, disabled.VerifyToken); // canal ajeno al bot que recibe
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        foreach (var s in new[] { disabled, tokenError, unknown })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Payload(s, "573001112233", "m1", "¿Horario?"))).StatusCode); // 200: a Meta no se le hace reintentar
        }

        Assert.Empty(await InboundAsync(disabled));
        Assert.Empty(await InboundAsync(tokenError));
        Assert.Empty(graph.Sent);
    }

    [Fact]
    public async Task Echoes_and_delivery_events_of_our_own_replies_are_not_answered_again()
    {
        var s = await SeedAsync(ChannelKind.Messenger);
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        var echo = $"{{\"object\":\"page\",\"entry\":[{{\"id\":\"{s.ExternalId}\",\"messaging\":[{{\"sender\":{{\"id\":\"{s.ExternalId}\"}},\"recipient\":{{\"id\":\"U1\"}},\"message\":{{\"mid\":\"m_e\",\"text\":\"De 8 a 18.\",\"is_echo\":true}}}}]}}]}}";

        await PostAsync(client, echo);

        Assert.Empty(await InboundAsync(s)); // sin esto, cada respuesta del bot generaría otra y se armaría un bucle
    }

    [Fact]
    public async Task A_message_that_is_not_text_gets_a_fixed_reply_and_opens_no_conversation()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "wamid.audio", null));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal(MetaInboxProcessor.NonTextReply, Assert.Single(graph.Sent).Text);
        Assert.Empty(await api.WithDbAsync(db => db.Conversations.Where(c => c.ChannelId == s.ChannelId).ToListAsync()));
    }

    [Fact]
    public async Task The_privacy_notice_is_sent_once_when_the_conversation_opens()
    {
        var s = await SeedAsync(privacyUrl: "https://negocio.test/privacidad");
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "m1", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);
        await PostAsync(client, Payload(s, "573001112233", "m2", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal(2, graph.Sent.Count);
        Assert.Contains("https://negocio.test/privacidad", graph.Sent[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("De 8 a 18.", graph.Sent[0].Text, StringComparison.Ordinal);
        Assert.Equal("De 8 a 18.", graph.Sent[1].Text); // en la misma ventana ya no se repite
    }

    [Fact]
    public async Task The_per_contact_hourly_cap_stops_a_loop_without_spending_the_model()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph, o => o.MaxMessagesPerContactPerHour = 2);
        using var client = factory.CreateClient();

        for (var i = 1; i <= 5; i++)
        {
            await PostAsync(client, Payload(s, "573001112233", $"m{i}", "¿Horario?"));
        }

        var outcomes = await DrainAsync(factory, s.ChannelId);

        Assert.Equal(2, outcomes.Count(o => o == InboundOutcome.Done));
        Assert.Equal(3, outcomes.Count(o => o == InboundOutcome.Dropped));
        Assert.Equal(2, graph.Sent.Count);

        // Otro contacto no se ve afectado.
        await PostAsync(client, Payload(s, "573009998877", "otro-1", "¿Horario?"));
        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));
    }

    [Fact]
    public async Task A_transient_send_failure_is_retried_without_asking_the_chatbot_again()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph { FailOn = attempt => attempt == 0 ? new MetaSendException("Meta respondió 503.") : null };
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, Payload(s, "573001112233", "m1", "¿Horario?"));

        var first = await DrainAsync(factory, s.ChannelId);
        Assert.Equal([InboundOutcome.Retry], first);
        var pending = Assert.Single(await InboundAsync(s));
        Assert.Equal(InboundStatus.Pending, pending.Status);
        Assert.Equal("De 8 a 18.", pending.ReplyText); // la respuesta ya estaba generada y guardada

        // El reintento no espera: se fuerza la hora y se vuelve a atender.
        await api.WithDbAsync(async db =>
        {
            await db.ChannelInbounds.Where(i => i.ChannelId == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(i => i.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            return 0;
        });
        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        Assert.Single(graph.Sent);
        var conversation = await api.WithDbAsync(db => db.Conversations.SingleAsync(c => c.ChannelId == s.ChannelId));
        Assert.Equal(1, conversation.MessageCount); // el modelo no volvió a gastarse ni se duplicó el mensaje
        Assert.Equal(1, (await api.WithDbAsync(db => db.ChatUsages.SingleAsync(u => u.ClientId == s.ClientId))).Messages);
    }

    [Fact]
    public async Task A_reply_cut_into_parts_resumes_after_the_last_part_that_arrived()
    {
        var s = await SeedAsync(ChannelKind.Instagram);
        var longAnswer = string.Join(' ', Enumerable.Repeat("información útil", 130)); // ~2.000 caracteres: 3 partes en Instagram (1.000)
        // La respuesta ya está generada y guardada (como queda tras un primer intento): solo falta enviarla.
        await api.WithDbAsync(async db =>
        {
            db.ChannelInbounds.Add(new ChannelInbound
            {
                Id = Guid.CreateVersion7(),
                ClientId = s.ClientId,
                ChannelId = s.ChannelId,
                ExternalMessageId = "m1",
                ContactId = "IGSID1",
                ReplyText = longAnswer,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        var graph = new FakeGraph { FailOn = attempt => attempt == 1 ? new MetaSendException("Meta respondió 503.") : null }; // la segunda parte falla una vez
        using var factory = Factory(graph);

        Assert.Equal([InboundOutcome.Retry], await DrainAsync(factory, s.ChannelId));
        Assert.Single(graph.Sent); // la primera parte sí llegó
        Assert.Equal(1, Assert.Single(await InboundAsync(s)).SentParts);
        await api.WithDbAsync(async db =>
        {
            await db.ChannelInbounds.Where(i => i.ChannelId == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(i => i.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            return 0;
        });
        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        Assert.True(graph.Sent.Count >= 3);
        Assert.All(graph.Sent, p => Assert.InRange(p.Text.Length, 1, 1000));
        Assert.Equal(longAnswer, string.Join(' ', graph.Sent.Select(p => p.Text))); // todo llegó, en orden y una sola vez
    }

    private async Task QueueReplyAsync(Seeded s, string reply)
    {
        await api.WithDbAsync(async db =>
        {
            db.ChannelInbounds.Add(new ChannelInbound
            {
                Id = Guid.CreateVersion7(),
                ClientId = s.ClientId,
                ChannelId = s.ChannelId,
                ExternalMessageId = "m1",
                ContactId = "573001112233",
                ReplyText = reply,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task On_whatsapp_the_product_photos_in_a_reply_are_sent_as_images_after_the_text()
    {
        var s = await SeedAsync();
        await QueueReplyAsync(s, "La bandeja paisa cuesta $38.000.\nFoto: https://cdn.donporko.co/bandeja.jpg\nFoto: https://cdn.donporko.co/bandeja.jpg\nFoto: https://cdn.donporko.co/chicharron.jpg");
        var graph = new FakeGraph();
        using var factory = Factory(graph);

        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        Assert.Equal("La bandeja paisa cuesta $38.000.", Assert.Single(graph.Sent).Text); // el texto ya no lleva la línea «Foto:»
        Assert.Equal(["https://cdn.donporko.co/bandeja.jpg", "https://cdn.donporko.co/chicharron.jpg"], graph.Images); // sin repetir
        Assert.Equal(3, Assert.Single(await InboundAsync(s)).SentParts);
    }

    [Fact]
    public async Task A_photo_that_meta_cannot_open_does_not_fail_the_reply_whose_text_already_went_out()
    {
        var s = await SeedAsync();
        await QueueReplyAsync(s, "Así luce el plato.\nFoto: https://cdn.donporko.co/roto.jpg");
        var graph = new FakeGraph { ImageFailure = new MetaSendException("Meta respondió 400.", permanent: true) };
        using var factory = Factory(graph);

        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        Assert.Equal("Así luce el plato.", Assert.Single(graph.Sent).Text);
        Assert.Empty(graph.Images);
        Assert.Equal(InboundStatus.Done, Assert.Single(await InboundAsync(s)).Status);
    }

    [Fact]
    public async Task A_token_rejected_by_Meta_marks_the_channel_and_stops_retrying()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph { FailOn = _ => new MetaSendException("token", tokenRejected: true) };
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, Payload(s, "573001112233", "m1", "¿Horario?"));

        Assert.Equal([InboundOutcome.Failed], await DrainAsync(factory, s.ChannelId));

        Assert.Equal(ChannelStatus.TokenError, (await api.WithDbAsync(db => db.Channels.SingleAsync(c => c.Id == s.ChannelId))).Status);
        Assert.Equal(InboundStatus.Failed, Assert.Single(await InboundAsync(s)).Status);

        // Con el canal en error, lo siguiente ni se encola.
        await PostAsync(client, Payload(s, "573001112233", "m2", "¿Horario?"));
        Assert.Single(await InboundAsync(s));
    }

    [Fact]
    public async Task A_permanent_error_or_too_many_attempts_end_in_failure_not_in_an_endless_retry()
    {
        var permanent = await SeedAsync();
        var graph = new FakeGraph { FailOn = _ => new MetaSendException("destinatario inválido", permanent: true) };
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, Payload(permanent, "573001112233", "m1", "¿Horario?"));
        Assert.Equal([InboundOutcome.Failed], await DrainAsync(factory, permanent.ChannelId));

        var flaky = await SeedAsync();
        using var factory2 = Factory(new FakeGraph { FailOn = _ => new MetaSendException("503") }, o => o.MaxAttempts = 2);
        using var client2 = factory2.CreateClient();
        await PostAsync(client2, Payload(flaky, "573001112233", "m1", "¿Horario?"));
        Assert.Equal([InboundOutcome.Retry], await DrainAsync(factory2, flaky.ChannelId));
        await api.WithDbAsync(async db =>
        {
            await db.ChannelInbounds.Where(i => i.ChannelId == flaky.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(i => i.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            return 0;
        });
        Assert.Equal([InboundOutcome.Failed], await DrainAsync(factory2, flaky.ChannelId)); // al segundo intento se rinde
    }

    [Fact]
    public async Task Two_businesses_with_the_same_contact_id_never_mix_conversations_replies_or_tokens()
    {
        var a = await SeedAsync(token: "token-A");
        var b = await SeedAsync(token: "token-B", faq: "Pregunta: ¿Horario?\nRespuesta: SECRETO del otro negocio.");
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(a, "573001112233", "m1", "¿Horario?"));
        await PostAsync(client, Payload(b, "573001112233", "m1", "¿Horario?"));
        await DrainAsync(factory, a.ChannelId, b.ChannelId);

        var forA = Assert.Single(graph.Sent, x => x.ChannelId == a.ExternalId);
        var forB = Assert.Single(graph.Sent, x => x.ChannelId == b.ExternalId);
        Assert.Equal(("token-A", "De 8 a 18."), (forA.Token, forA.Text));
        Assert.Equal(("token-B", "SECRETO del otro negocio."), (forB.Token, forB.Text));
        Assert.Equal(a.ClientId, (await api.WithDbAsync(db => db.Conversations.SingleAsync(c => c.ChannelId == a.ChannelId))).ClientId);
        Assert.Equal(b.ClientId, (await api.WithDbAsync(db => db.Conversations.SingleAsync(c => c.ChannelId == b.ChannelId))).ClientId);
    }

    [Fact]
    public async Task The_access_token_is_stored_encrypted_never_in_clear()
    {
        var s = await SeedAsync(token: "EAAB-token-en-claro-que-no-debe-guardarse");

        var channel = await api.WithDbAsync(db => db.Channels.SingleAsync(c => c.Id == s.ChannelId));

        Assert.DoesNotContain("EAAB", channel.AccessTokenEncrypted, StringComparison.Ordinal);
        Assert.Equal("EAAB-token-en-claro-que-no-debe-guardarse", api.Services.GetRequiredService<ISecretProtector>().Unprotect(channel.AccessTokenEncrypted));
    }

    [Fact]
    public async Task The_same_phone_number_id_cannot_be_connected_twice_for_the_same_kind()
    {
        var s = await SeedAsync();

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => api.WithDbAsync(async db =>
        {
            db.Channels.Add(new Channel { Id = Guid.CreateVersion7(), ClientId = Guid.CreateVersion7(), BotId = Guid.CreateVersion7(), Kind = s.Kind, ExternalId = s.ExternalId, DisplayName = "Otro", AccessTokenEncrypted = "x", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        }));

        Assert.NotNull(duplicate);
        // Mismo id en otro tipo de canal sí es posible: los ids de Meta no son únicos entre productos.
        await api.WithDbAsync(async db =>
        {
            db.Channels.Add(new Channel { Id = Guid.CreateVersion7(), ClientId = s.ClientId, BotId = s.BotId, Kind = ChannelKind.Messenger, ExternalId = s.ExternalId, DisplayName = "Otro tipo", AccessTokenEncrypted = "x", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task Maintenance_purges_old_finished_inbound_records_but_keeps_recent_and_pending_ones()
    {
        var s = await SeedAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        await api.WithDbAsync(async db =>
        {
            ChannelInbound Row(string id, InboundStatus status, DateTimeOffset created) =>
                new() { Id = Guid.CreateVersion7(), ClientId = s.ClientId, ChannelId = s.ChannelId, ExternalMessageId = id, ContactId = "573001112233", Status = status, NextAttemptAt = created, CreatedAt = created };
            db.ChannelInbounds.AddRange(Row("viejo-hecho", InboundStatus.Done, old), Row("viejo-fallido", InboundStatus.Failed, old), Row("viejo-pendiente", InboundStatus.Pending, old), Row("reciente-hecho", InboundStatus.Done, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });

        await api.Services.GetRequiredService<MaintenanceRunner>().RunOnceAsync(CancellationToken.None);

        var left = (await InboundAsync(s)).Select(i => i.ExternalMessageId).OrderBy(x => x).ToList();
        Assert.Equal(["reciente-hecho", "viejo-pendiente"], left);
    }

    private static async Task<ServiceResult<T>> AsTenantAsync<T>(WebApplicationFactory<Program> factory, Guid tenantId, Func<ConversationService, Task<ServiceResult<T>>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetClient(tenantId);
        return await action(scope.ServiceProvider.GetRequiredService<ConversationService>());
    }

    private async Task<Conversation> ConversationOfAsync(Seeded s) =>
        await api.WithDbAsync(db => db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.ChannelId == s.ChannelId));

    [Fact]
    public async Task The_bot_list_counts_only_this_business_conversations_that_wait_for_a_person()
    {
        var s = await SeedAsync(faq: "Pregunta: ¿Horario?\nRespuesta: De 8 a 18.");
        var other = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        async Task<int> CountAsync(Guid tenantId)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ClientScope>().SetClient(tenantId);
            return (await scope.ServiceProvider.GetRequiredService<BotService>().ListAsync(null, CancellationToken.None)).NeedingAttention;
        }

        await PostAsync(client, Payload(s, "573001112233", "wamid.a1", "¿Horario?")); // el bot sabe: nadie tiene que mirarla
        await DrainAsync(factory, s.ChannelId);
        Assert.Equal(0, await CountAsync(s.ClientId));

        await PostAsync(client, Payload(s, "573004445566", "wamid.a2", "Quiero una cotización de bicicletas eléctricas")); // el bot no sabe
        await DrainAsync(factory, s.ChannelId);
        Assert.Equal(1, await CountAsync(s.ClientId));
        Assert.Equal(0, await CountAsync(other.ClientId)); // otro negocio no la ve

        // Una persona la atiende: ya no espera.
        var conversation = await api.WithDbAsync(db => db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.ChannelId == s.ChannelId && c.ContactId == "573004445566"));
        await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("Con gusto te cotizo."), CancellationToken.None));
        Assert.Equal(0, await CountAsync(s.ClientId));
    }

    [Fact]
    public async Task A_person_who_takes_over_silences_the_bot_and_can_reply_through_the_same_channel()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "wamid.h1", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);
        Assert.Single(graph.Sent); // el bot respondió
        var conversation = await ConversationOfAsync(s);

        // El negocio responde: sale por el mismo canal, al mismo contacto, con el token del canal, y la conversación pasa a manos de la persona.
        var reply = await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("Hola, soy Laura. Te ayudo."), CancellationToken.None));
        Assert.True(reply.IsSuccess);
        Assert.Equal((ChannelKind.WhatsApp, s.ExternalId, "token-del-cliente", "573001112233", "Hola, soy Laura. Te ayudo."), graph.Sent[1]);

        // Cada mensaje enviado por WhatsApp (el del bot y el de la persona) se cuenta en el mes, porque Meta cobra a partir del 1.001 mensual por número.
        Assert.Equal(2, await api.WithDbAsync(db => db.ChatUsages.IgnoreQueryFilters().Where(u => u.ClientId == s.ClientId).SumAsync(u => u.WhatsAppMessagesSent)));

        // Si el contacto vuelve a escribir, el bot calla: se guarda el mensaje, no se envía nada y la conversación sigue pidiendo atención.
        await PostAsync(client, Payload(s, "573001112233", "wamid.h2", "¿Y el domingo?"));
        var outcomes = await DrainAsync(factory, s.ChannelId);
        Assert.Equal([InboundOutcome.Dropped], outcomes);
        Assert.Equal(2, graph.Sent.Count);

        var after = await ConversationOfAsync(s);
        Assert.True(after.HumanActive);
        Assert.True(after.NeedsAttention);
        var roles = await api.WithDbAsync(db => db.ChatMessages.IgnoreQueryFilters().Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.CreatedAt).Select(m => m.Role).ToListAsync());
        Assert.Equal(["user", "assistant", "agent", "user"], roles);

        // Se devuelve al bot: vuelve a responder desde el siguiente mensaje.
        Assert.True((await AsTenantAsync(factory, s.ClientId, c => c.ReleaseAsync(s.BotId, conversation.Id, CancellationToken.None))).IsSuccess);
        await PostAsync(client, Payload(s, "573001112233", "wamid.h3", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);
        Assert.Equal(3, graph.Sent.Count);
        Assert.False((await ConversationOfAsync(s)).HumanActive);
    }

    private sealed class HandoffModel(string reply) : IAiGateway
    {
        public bool UsesModel => true;

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken ct) => Task.FromResult(new AiReply(reply, 10, 5, "fake"));
    }

    [Fact]
    public async Task When_the_model_asks_for_a_person_the_contact_gets_the_handoff_message_and_the_bot_goes_quiet()
    {
        var s = await SeedAsync(handoffMessage: "Ya aviso al equipo, en un momento te atiende una persona. 🐷");
        var graph = new FakeGraph();
        using var factory = Factory(graph).WithWebHostBuilder(b => b.ConfigureServices(sv => sv.AddSingleton<IAiGateway>(new HandoffModel("Claro, te paso con el equipo. [[OPERADOR]]"))));
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "wamid.o1", "Quiero hablar con un asesor"));
        await DrainAsync(factory, s.ChannelId);

        // Sale el mensaje del negocio, nunca la marca interna.
        Assert.Equal("Ya aviso al equipo, en un momento te atiende una persona. 🐷", Assert.Single(graph.Sent).Text);
        var conversation = await ConversationOfAsync(s);
        Assert.True(conversation.HumanActive);
        Assert.True(conversation.NeedsAttention);

        // Desde ahí el bot calla y el mensaje solo se guarda.
        await PostAsync(client, Payload(s, "573001112233", "wamid.o2", "¿Hola?"));
        Assert.Equal([InboundOutcome.Dropped], await DrainAsync(factory, s.ChannelId));
        Assert.Single(graph.Sent);
    }

    [Fact]
    public async Task Without_a_configured_handoff_message_the_model_text_is_sent_without_the_marker()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph).WithWebHostBuilder(b => b.ConfigureServices(sv => sv.AddSingleton<IAiGateway>(new HandoffModel("Lamento lo ocurrido, aviso al equipo. [[operador]]"))));
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "wamid.o3", "Esto es inaceptable"));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal("Lamento lo ocurrido, aviso al equipo.", Assert.Single(graph.Sent).Text);
        Assert.True((await ConversationOfAsync(s)).HumanActive);
    }

    [Fact]
    public async Task A_normal_answer_does_not_hand_off_and_the_rule_is_only_in_the_prompt_for_meta_channels()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph).WithWebHostBuilder(b => b.ConfigureServices(sv => sv.AddSingleton<IAiGateway>(new HandoffModel("De 8 a 18."))));
        using var client = factory.CreateClient();

        await PostAsync(client, Payload(s, "573001112233", "wamid.o4", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal("De 8 a 18.", Assert.Single(graph.Sent).Text);
        Assert.False((await ConversationOfAsync(s)).HumanActive);

        var bot = new Bot { Id = Guid.CreateVersion7(), ClientId = Guid.CreateVersion7(), PublicKey = "k", Name = "B", CreatedAt = DateTimeOffset.UtcNow };
        Assert.Contains(ChatService.HandoffMarker, ChatService.BuildSystemPrompt(bot, canHandoff: true), StringComparison.Ordinal);
        Assert.DoesNotContain(ChatService.HandoffMarker, ChatService.BuildSystemPrompt(bot), StringComparison.Ordinal);
        Assert.Equal("Hola", ChatService.StripHandoff("Hola [[ OPERADOR ]]", out var found));
        Assert.True(found);
    }

    private static string WhatsAppFile(Seeded s, string contact, string messageId, string type, string? caption, string mediaId = "MEDIA1")
    {
        var cap = caption is null ? "" : $",\"caption\":\"{caption}\"";
        var name = type == "document" ? ",\"filename\":\"comprobante.pdf\"" : "";
        var mime = type == "document" ? "application/pdf" : "image/jpeg";
        var json = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"id\":\"W\",\"changes\":[{\"field\":\"messages\",\"value\":{\"messaging_product\":\"whatsapp\"," +
            $"\"metadata\":{{\"phone_number_id\":\"{s.ExternalId}\"}},\"contacts\":[{{\"profile\":{{\"name\":\"Ana\"}},\"wa_id\":\"{contact}\"}}],\"messages\":[" +
            $"{{\"from\":\"{contact}\",\"id\":\"{messageId}\",\"timestamp\":\"1\",\"type\":\"{type}\",\"{type}\":{{\"id\":\"{mediaId}\",\"mime_type\":\"{mime}\"{cap}{name}}}}}]}}}}]}}]}}";
        BotOfPayload[json] = s.PublicKey;
        return json;
    }

    [Fact]
    public void The_parser_reads_whatsapp_media_with_its_caption_and_messenger_attachment_urls()
    {
        var seed = new Seeded(Guid.Empty, Guid.Empty, Guid.Empty, "PN1", ChannelKind.WhatsApp, "bt_x", "x");
        var wa = MetaWebhookParser.Parse(Encoding.UTF8.GetBytes(WhatsAppFile(seed, "573001112233", "wamid.1", "document", "Aquí va el pago")));
        var m = Assert.Single(wa);
        Assert.Equal("Aquí va el pago", m.Text);
        var a = Assert.Single(m.Attachments!);
        Assert.Equal(("document", "MEDIA1", "application/pdf", "comprobante.pdf"), (a.Kind, a.MediaId, a.MimeType, a.FileName));

        const string Messenger = "{\"object\":\"page\",\"entry\":[{\"id\":\"PG1\",\"messaging\":[{\"sender\":{\"id\":\"U1\"},\"message\":{\"mid\":\"m1\",\"attachments\":[" +
            "{\"type\":\"image\",\"payload\":{\"url\":\"https://scontent.xx.fbcdn.net/a.jpg\"}},{\"type\":\"location\",\"payload\":{\"coordinates\":{}}},{\"type\":\"file\",\"payload\":{\"url\":\"https://cdn.fbsbx.com/f.pdf\"}}]}}]}]}";
        var fb = Assert.Single(MetaWebhookParser.Parse(Encoding.UTF8.GetBytes(Messenger)));
        Assert.Null(fb.Text);
        Assert.Equal(["image", "document"], fb.Attachments!.Select(x => x.Kind));
    }

    [Fact]
    public async Task A_photo_without_text_is_saved_on_disk_acknowledged_and_flagged_for_a_person()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, WhatsAppFile(s, "573001112233", "wamid.f1", "image", null))).StatusCode);
        Assert.Equal([InboundOutcome.Done], await DrainAsync(factory, s.ChannelId));

        Assert.Equal(ChatService.AttachmentAck, Assert.Single(graph.Sent).Text);
        Assert.Equal("MEDIA1", Assert.Single(graph.Downloads).MediaId);
        var conversation = await ConversationOfAsync(s);
        Assert.True(conversation.NeedsAttention);
        Assert.False(conversation.HumanActive);

        var transcript = (await AsTenantAsync(factory, s.ClientId, c => c.GetTranscriptAsync(s.BotId, conversation.Id, CancellationToken.None))).Value!;
        Assert.Equal(["user", "assistant"], transcript.Select(m => m.Role));
        Assert.Equal("📎 Imagen", transcript[0].Content);
        var file = Assert.Single(transcript[0].Attachments!);
        Assert.True(file.Available);
        Assert.Equal("image/jpeg", file.MimeType);

        var opened = (await AsTenantAsync(factory, s.ClientId, c => c.OpenAttachmentAsync(s.BotId, conversation.Id, file.Id, CancellationToken.None))).Value!;
        await using var stream = opened.Content;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 }, copy.ToArray());
    }

    [Fact]
    public async Task A_photo_with_a_caption_is_saved_and_the_caption_is_answered_normally_without_the_acknowledgement()
    {
        var s = await SeedAsync(faq: "Pregunta: ¿Horario?\nRespuesta: De 8 a 18.");
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, WhatsAppFile(s, "573001112233", "wamid.f2", "image", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal("De 8 a 18.", Assert.Single(graph.Sent).Text);
        var conversation = await ConversationOfAsync(s);
        var transcript = (await AsTenantAsync(factory, s.ClientId, c => c.GetTranscriptAsync(s.BotId, conversation.Id, CancellationToken.None))).Value!;
        Assert.Single(transcript, m => m.Attachments is { Count: 1 });
        Assert.Contains(transcript, m => m.Role == "user" && m.Content == "¿Horario?");
    }

    [Fact]
    public async Task A_failed_download_is_recorded_as_unavailable_and_the_contact_still_gets_the_acknowledgement()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph { Download = _ => throw new MetaSendException("caducó", permanent: true) };
        using var factory = Factory(graph);
        using var client = factory.CreateClient();

        await PostAsync(client, WhatsAppFile(s, "573001112233", "wamid.f3", "document", null));
        await DrainAsync(factory, s.ChannelId);

        Assert.Equal(ChatService.AttachmentAck, Assert.Single(graph.Sent).Text);
        var conversation = await ConversationOfAsync(s);
        var transcript = (await AsTenantAsync(factory, s.ClientId, c => c.GetTranscriptAsync(s.BotId, conversation.Id, CancellationToken.None))).Value!;
        var file = Assert.Single(transcript[0].Attachments!);
        Assert.False(file.Available);
        Assert.Equal("comprobante.pdf", file.FileName);
        Assert.Equal("gone", (await AsTenantAsync(factory, s.ClientId, c => c.OpenAttachmentAsync(s.BotId, conversation.Id, file.Id, CancellationToken.None))).Error!.Code);
    }

    [Fact]
    public async Task When_a_person_has_the_conversation_a_file_is_saved_without_any_reply()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, Payload(s, "573001112233", "wamid.p1", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);
        var conversation = await ConversationOfAsync(s);
        await AsTenantAsync(factory, s.ClientId, c => c.TakeOverAsync(s.BotId, conversation.Id, CancellationToken.None));
        var before = graph.Sent.Count;

        await PostAsync(client, WhatsAppFile(s, "573001112233", "wamid.p2", "image", null));
        Assert.Equal([InboundOutcome.Dropped], await DrainAsync(factory, s.ChannelId));

        Assert.Equal(before, graph.Sent.Count);
        Assert.Single(await api.WithDbAsync(db => db.ChatAttachments.IgnoreQueryFilters().Where(a => a.ConversationId == conversation.Id).ToListAsync()));
    }

    [Fact]
    public async Task Files_expire_after_the_retention_period_and_their_content_is_removed_from_disk()
    {
        var s = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, WhatsAppFile(s, "573001112233", "wamid.e1", "image", null));
        await DrainAsync(factory, s.ChannelId);
        var conversation = await ConversationOfAsync(s);
        var attachment = Assert.Single(await api.WithDbAsync(db => db.ChatAttachments.IgnoreQueryFilters().Where(a => a.ConversationId == conversation.Id).ToListAsync()));
        Assert.True(attachment.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6)); // se conserva una semana
        var store = factory.Services.GetRequiredService<IAttachmentStore>();
        using (var open = store.OpenRead(s.ClientId, attachment.Id))
        {
            Assert.NotNull(open);
        }

        await api.WithDbAsync(async db =>
        {
            var row = await db.ChatAttachments.IgnoreQueryFilters().SingleAsync(a => a.Id == attachment.Id);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            return 0;
        });
        await factory.Services.GetRequiredService<MaintenanceRunner>().RunOnceAsync(CancellationToken.None);

        Assert.Null(store.OpenRead(s.ClientId, attachment.Id));
        Assert.False((await api.WithDbAsync(db => db.ChatAttachments.IgnoreQueryFilters().SingleAsync(a => a.Id == attachment.Id))).Stored);
        var transcript = (await AsTenantAsync(factory, s.ClientId, c => c.GetTranscriptAsync(s.BotId, conversation.Id, CancellationToken.None))).Value!;
        Assert.False(transcript[0].Attachments![0].Available);
    }

    [Fact]
    public async Task A_person_cannot_reply_after_the_24_hour_window_or_when_the_channel_rejects_the_token_or_to_another_business()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        var graph = new FakeGraph();
        using var factory = Factory(graph);
        using var client = factory.CreateClient();
        await PostAsync(client, Payload(s, "573001112233", "wamid.v1", "¿Horario?"));
        await DrainAsync(factory, s.ChannelId);
        var conversation = await ConversationOfAsync(s);

        // Otro negocio no ve ni toca esta conversación.
        var foreign = await AsTenantAsync(factory, other.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("hola"), CancellationToken.None));
        Assert.Equal("not_found", foreign.Error!.Code);
        Assert.Equal("not_found", (await AsTenantAsync(factory, other.ClientId, c => c.TakeOverAsync(s.BotId, conversation.Id, CancellationToken.None))).Error!.Code);

        // Vacío o demasiado largo.
        Assert.Equal("validation_error", (await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("   "), CancellationToken.None))).Error!.Code);
        Assert.Equal("validation_error", (await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest(new string('x', ConversationService.MaxAgentReplyChars + 1)), CancellationToken.None))).Error!.Code);

        // Meta rechaza el token: el canal queda en error y la persona recibe el aviso.
        graph.FailOn = _ => new MetaSendException("token", tokenRejected: true);
        var rejected = await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("hola"), CancellationToken.None));
        Assert.Equal("invalid_state", rejected.Error!.Code);
        Assert.Equal(ChannelStatus.TokenError, await api.WithDbAsync(db => db.Channels.IgnoreQueryFilters().Where(c => c.Id == s.ChannelId).Select(c => c.Status).SingleAsync()));
        Assert.False((await ConversationOfAsync(s)).HumanActive); // un envío fallido no toma la conversación

        // Pasadas 24 horas del último mensaje del contacto, Meta no deja escribirle.
        await api.WithDbAsync(async db =>
        {
            await db.ChatMessages.IgnoreQueryFilters().Where(m => m.ConversationId == conversation.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.CreatedAt, DateTimeOffset.UtcNow.AddHours(-30)));
            await db.Channels.IgnoreQueryFilters().Where(c => c.Id == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChannelStatus.Active));
            return 0;
        });
        graph.FailOn = _ => null;
        var late = await AsTenantAsync(factory, s.ClientId, c => c.ReplyAsync(s.BotId, conversation.Id, new AgentReplyRequest("hola"), CancellationToken.None));
        Assert.Equal("invalid_state", late.Error!.Code);
        Assert.Contains("24 horas", late.Error.Message);
    }
}
