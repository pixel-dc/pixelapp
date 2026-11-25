using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Pixela.Application.Chat;
using Pixela.Infrastructure.Chat;

namespace Pixela.Api.Tests;

/// <summary>El cliente real de administración de Meta, contra respuestas simuladas de Graph: forma de las llamadas y lectura de lo que devuelve.</summary>
public class MetaAdminClientTests
{
    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private sealed class Stub(Func<Seen, (HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(seen);
            var (status, json) = reply(seen);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (MetaAdminClient Client, Stub Stub) Make(Func<Seen, (HttpStatusCode, string)> reply)
    {
        var stub = new Stub(reply);
        return (new MetaAdminClient(Options.Create(new MetaOptions { GraphVersion = "v21.0" }), stub), stub);
    }

    [Fact]
    public async Task Tokens_travel_in_the_header_never_in_the_url()
    {
        var (client, stub) = Make(_ => (HttpStatusCode.OK, """{"data":{"is_valid":true,"app_id":"123","type":"SYSTEM_USER","expires_at":0}}"""));

        var info = await client.DebugTokenAsync("EAAB-secreto", CancellationToken.None);

        Assert.True(info.IsValid);
        Assert.Equal("123", info.AppId);
        Assert.Equal("SYSTEM_USER", info.Type);
        Assert.Null(info.ExpiresAt); // expires_at = 0 significa que no vence
        var seen = Assert.Single(stub.Requests);
        Assert.Equal("Bearer EAAB-secreto", seen.Authorization);
        Assert.Equal("/v21.0/debug_token", seen.Uri.AbsolutePath);
        Assert.Contains("input_token=", seen.Uri.Query, StringComparison.Ordinal); // lo que se consulta; la credencial va en la cabecera
    }

    [Fact]
    public async Task A_revoked_token_is_reported_as_invalid_not_as_a_failure()
    {
        var (client, _) = Make(_ => (HttpStatusCode.BadRequest, """{"error":{"message":"Error validating access token","code":190}}"""));

        var info = await client.DebugTokenAsync("viejo", CancellationToken.None);

        Assert.False(info.IsValid);
    }

    [Fact]
    public async Task App_subscriptions_are_read_with_the_app_token()
    {
        var (client, stub) = Make(_ => (HttpStatusCode.OK, """
            {"data":[{"object":"page","callback_url":"https://x/meta/webhook/bt_1","active":true,"fields":[{"name":"messages"},{"name":"messaging_postbacks"}]},
                     {"object":"instagram","callback_url":"https://otro","active":false,"fields":[]}]}
            """));

        var subs = await client.GetAppSubscriptionsAsync("55", "sec", CancellationToken.None);

        Assert.Equal(2, subs.Count);
        Assert.Equal("page", subs[0].Topic);
        Assert.True(subs[0].Active);
        Assert.Equal(["messages", "messaging_postbacks"], subs[0].Fields);
        Assert.False(subs[1].Active);
        Assert.Equal("Bearer 55|sec", stub.Requests[0].Authorization);
        Assert.Equal("/v21.0/55/subscriptions", stub.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Setting_the_app_webhook_posts_a_form_with_the_verify_token_and_the_fields()
    {
        var (client, stub) = Make(_ => (HttpStatusCode.OK, """{"success":true}"""));

        await client.SetAppSubscriptionAsync("55", "sec", "whatsapp_business_account", "https://x/meta/webhook/bt_1", "vt", ["messages", "account_update"], CancellationToken.None);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("", seen.Uri.Query); // nada en la URL
        Assert.Contains("object=whatsapp_business_account", seen.Body, StringComparison.Ordinal);
        Assert.Contains("verify_token=vt", seen.Body, StringComparison.Ordinal);
        Assert.Contains("fields=messages%2Caccount_update", seen.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_phone_webhook_levels_are_separated_from_the_app_level_one()
    {
        var (client, _) = Make(_ => (HttpStatusCode.OK, """
            {"display_phone_number":"+57 301","status":"CONNECTED","webhook_configuration":{"application":"https://app","phone_number":"https://propio"}}
            """));

        var phone = await client.GetPhoneAsync("123", "tok", CancellationToken.None);

        Assert.Equal("CONNECTED", phone.Status);
        var only = Assert.Single(phone.Overrides); // «application» se revisa aparte
        Assert.Equal(("phone_number", "https://propio"), (only.Key, only.Value));
    }

    [Fact]
    public async Task Subscribed_apps_are_read_for_both_a_WABA_and_a_page()
    {
        var (waba, _) = Make(_ => (HttpStatusCode.OK, """{"data":[{"whatsapp_business_api_data":{"id":"1670","name":"App"}}]}"""));
        var (page, _) = Make(_ => (HttpStatusCode.OK, """{"data":[{"id":"1670","name":"App","subscribed_fields":["messages","messaging_postbacks"]}]}"""));

        var a = Assert.Single(await waba.GetSubscribedAppsAsync("w", "t", CancellationToken.None));
        var b = Assert.Single(await page.GetSubscribedAppsAsync("p", "t", CancellationToken.None));

        Assert.Equal("1670", a.AppId);
        Assert.Empty(a.Fields);
        Assert.Equal("1670", b.AppId);
        Assert.Equal(["messages", "messaging_postbacks"], b.Fields);
    }

    [Fact]
    public async Task Meta_errors_surface_their_message_and_code_without_the_token()
    {
        var (client, _) = Make(_ => (HttpStatusCode.BadRequest, """{"error":{"message":"(#10) This endpoint requires the 'pages_read_engagement' permission","code":10}}"""));

        var ex = await Assert.ThrowsAsync<MetaAdminException>(() => client.GetPageAccessTokenAsync("181", "tok-secreto", CancellationToken.None));

        Assert.Equal(10, ex.Code);
        Assert.Contains("pages_read_engagement", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-secreto", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_token_is_returned_only_when_Meta_gives_one()
    {
        var (with, _) = Make(_ => (HttpStatusCode.OK, """{"access_token":"EAAPAGE","id":"181"}"""));
        var (without, _) = Make(_ => (HttpStatusCode.OK, """{"id":"181"}"""));

        Assert.Equal("EAAPAGE", await with.GetPageAccessTokenAsync("181", "t", CancellationToken.None));
        Assert.Null(await without.GetPageAccessTokenAsync("181", "t", CancellationToken.None));
    }

    [Theory]
    [InlineData("../me")]
    [InlineData("123/subscribed_apps?x=")]
    [InlineData("")]
    public async Task A_stored_id_cannot_change_the_address_that_is_called(string id)
    {
        var (client, stub) = Make(_ => (HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<MetaAdminException>(() => client.GetSubscribedAppsAsync(id, "t", CancellationToken.None));

        Assert.Empty(stub.Requests);
    }
}
