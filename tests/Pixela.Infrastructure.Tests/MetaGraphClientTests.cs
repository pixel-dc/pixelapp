using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pixela.Application.Chat;
using Pixela.Domain.Chat;
using Pixela.Infrastructure.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Tests;

/// <summary>Envío de mensajes por la API de Meta con un manejador HTTP simulado.</summary>
public class MetaGraphClientTests
{
    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MetaGraphClient Client(FakeHandler handler, int retries = 2) =>
        new(Options.Create(new MetaOptions { GraphVersion = "v99.0", MaxRetries = retries, RetryDelayMs = 0 }), handler);

    [Fact]
    public async Task WhatsApp_sends_to_the_number_id_with_the_documented_body_and_a_bearer_token()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        await client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok-1", "573001112233", "Hola", CancellationToken.None);

        var (request, body) = Assert.Single(handler.Received);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.facebook.com/v99.0/PN123/messages", request.RequestUri!.ToString());
        Assert.Equal("Bearer tok-1", request.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("whatsapp", json["messaging_product"]!.GetValue<string>());
        Assert.Equal("573001112233", json["to"]!.GetValue<string>());
        Assert.Equal("text", json["type"]!.GetValue<string>());
        Assert.Equal("Hola", json["text"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_whatsapp_contact_that_hid_the_phone_number_is_answered_by_its_meta_id_in_recipient()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        await client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok-1", "CA.13491208655302741918", "Hola", CancellationToken.None);

        var json = JsonNode.Parse(Assert.Single(handler.Received).Body)!;
        Assert.Equal("CA.13491208655302741918", json["recipient"]!.GetValue<string>());
        Assert.Null(json["to"]);
        Assert.Equal("individual", json["recipient_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhatsApp_images_are_sent_by_public_link_to_the_phone_or_to_the_meta_id()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"), _ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        await client.SendImageAsync(ChannelKind.WhatsApp, "PN123", "tok-1", "573001112233", "https://cdn.donporko.co/a.jpg", CancellationToken.None);
        await client.SendImageAsync(ChannelKind.WhatsApp, "PN123", "tok-1", "CO.1349", "https://cdn.donporko.co/a.jpg", CancellationToken.None);

        var first = JsonNode.Parse(handler.Received[0].Body)!;
        Assert.Equal("image", first["type"]!.GetValue<string>());
        Assert.Equal("https://cdn.donporko.co/a.jpg", first["image"]!["link"]!.GetValue<string>());
        Assert.Equal("573001112233", first["to"]!.GetValue<string>());
        Assert.Equal("CO.1349", JsonNode.Parse(handler.Received[1].Body)!["recipient"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_channels_do_not_send_images_by_link()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendImageAsync(ChannelKind.Instagram, "P", "t", "U", "https://x.co/a.jpg", CancellationToken.None));

        Assert.True(ex.Permanent);
        Assert.Empty(handler.Received);
    }

    [Theory]
    [InlineData(ChannelKind.Messenger)]
    [InlineData(ChannelKind.Instagram)]
    public async Task Messenger_and_Instagram_reply_through_me_messages_inside_the_response_window(ChannelKind kind)
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        await client.SendTextAsync(kind, "PAGE9", "tok-2", "USER1", "Hola", CancellationToken.None);

        var (request, body) = Assert.Single(handler.Received);
        Assert.Equal("https://graph.facebook.com/v99.0/me/messages", request.RequestUri!.ToString());
        Assert.Equal("Bearer tok-2", request.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("USER1", json["recipient"]!["id"]!.GetValue<string>());
        Assert.Equal("RESPONSE", json["messaging_type"]!.GetValue<string>()); // solo se responde, nunca se inicia una conversación
        Assert.Equal("Hola", json["message"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Special_characters_in_the_text_are_escaped_not_injected_into_the_json()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        const string tricky = "comillas \" y \\ y {\"to\":\"otro\"}\n¿ñ? 😀";

        await client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok", "573001112233", tricky, CancellationToken.None);

        var json = JsonNode.Parse(handler.Received[0].Body)!;
        Assert.Equal(tricky, json["text"]!["body"]!.GetValue<string>());
        Assert.Equal("573001112233", json["to"]!.GetValue<string>()); // el texto no pudo cambiar el destinatario
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Rate_limits_and_server_errors_are_retried_and_then_succeed(int status)
    {
        var handler = new FakeHandler(_ => Json((HttpStatusCode)status, "{}"), _ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        await client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok", "573001112233", "Hola", CancellationToken.None);

        Assert.Equal(2, handler.Received.Count);
    }

    [Fact]
    public async Task Persistent_server_errors_give_up_after_the_retries_as_a_transient_failure()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var client = Client(handler, retries: 2);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok", "573001112233", "Hola", CancellationToken.None));

        Assert.False(ex.Permanent);
        Assert.False(ex.TokenRejected);
        Assert.Equal(3, handler.Received.Count); // el primer intento más dos reintentos
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(400, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""")]
    public async Task A_rejected_token_is_reported_and_never_retried(int status, string body)
    {
        var handler = new FakeHandler(_ => Json((HttpStatusCode)status, body));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.Messenger, "PAGE9", "tok", "USER1", "Hola", CancellationToken.None));

        Assert.True(ex.TokenRejected);
        Assert.Single(handler.Received);
    }

    [Fact]
    public async Task Other_client_errors_are_permanent_and_do_not_leak_the_response_body()
    {
        const string secret = "datos-del-contacto-573001112233";
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"" + secret + "\",\"code\":131030}}"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok", "573001112233", "Hola", CancellationToken.None));

        Assert.True(ex.Permanent);
        Assert.False(ex.TokenRejected);
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Received);
    }

    [Fact]
    public async Task A_connection_failure_is_a_transient_error_after_the_retries()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("sin red"));
        using var client = Client(handler, retries: 1);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.WhatsApp, "PN123", "tok", "573001112233", "Hola", CancellationToken.None));

        Assert.False(ex.Permanent);
        Assert.Equal(2, handler.Received.Count);
    }

    [Fact]
    public async Task A_rejected_send_keeps_the_Meta_error_code_and_subcode_but_never_its_text()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"(#10) Esta persona Ana Pérez no está disponible","code":10,"error_subcode":2018278}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.Messenger, "P", "tok", "5001", "hola", CancellationToken.None));

        Assert.True(ex.Permanent);
        Assert.Equal((10, 2018278), (ex.MetaCode, ex.MetaSubcode));
        Assert.DoesNotContain("Ana", ex.Message, StringComparison.Ordinal); // el texto de Meta puede traer datos del contacto
    }

    [Fact]
    public async Task A_rejection_without_a_readable_body_still_fails_cleanly()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "no es json"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() => client.SendTextAsync(ChannelKind.Messenger, "P", "tok", "5001", "hola", CancellationToken.None));

        Assert.True(ex.Permanent);
        Assert.Null(ex.MetaCode);
    }
}
