using System.Net;
using System.Text;
using Pixela.Infrastructure.Chat;

namespace Pixela.Infrastructure.Tests;

public class SupabaseCatalogFetcherTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly Uri Project = new("https://abcdefghijklmnopqrst.supabase.co");

    [Fact]
    public async Task A_new_style_key_goes_only_in_apikey_and_a_legacy_jwt_also_as_bearer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        using var fetcher = new SupabaseCatalogFetcher(handler);

        await fetcher.FetchAsync(Project, "productos", "nombre,precio", "sb_publishable_0123456789abcdef", CancellationToken.None);
        Assert.Equal("sb_publishable_0123456789abcdef", handler.Seen!.Headers.GetValues("apikey").Single());
        Assert.False(handler.Seen.Headers.Contains("Authorization"));
        Assert.Equal("/rest/v1/productos", handler.Seen.RequestUri!.AbsolutePath);
        Assert.Contains("select=nombre,precio", handler.Seen.RequestUri.Query, StringComparison.Ordinal);

        await fetcher.FetchAsync(Project, "productos", null, "eyJhbGciOiJIUzI1NiJ9.payload.firma", CancellationToken.None);
        Assert.Equal("Bearer eyJhbGciOiJIUzI1NiJ9.payload.firma", handler.Seen!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task A_401_and_a_403_say_different_things_and_never_echo_the_key()
    {
        const string key = "sb_publishable_0123456789abcdef";
        using var unauthorized = new SupabaseCatalogFetcher(new StubHandler(HttpStatusCode.Unauthorized, """{"message":"Invalid API key"}"""));
        var a = await Assert.ThrowsAsync<InvalidOperationException>(() => unauthorized.FetchAsync(Project, "p", null, key, CancellationToken.None));
        Assert.Contains("no reconoce la llave", a.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", a.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, a.Message, StringComparison.Ordinal);

        using var forbidden = new SupabaseCatalogFetcher(new StubHandler(HttpStatusCode.Forbidden, """{"message":"permission denied for table productos"}"""));
        var b = await Assert.ThrowsAsync<InvalidOperationException>(() => forbidden.FetchAsync(Project, "productos", null, key, CancellationToken.None));
        Assert.Contains("no tiene permiso", b.Message, StringComparison.Ordinal);
        Assert.Contains("permission denied for table productos", b.Message, StringComparison.Ordinal);
    }
}
