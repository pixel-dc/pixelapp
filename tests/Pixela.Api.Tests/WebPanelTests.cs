using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Pixela.Api.Tests;

/// <summary>La API sirve el panel: archivos con caché larga, rutas del panel con index.html, y las de la API intactas.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WebPanelTests(ApiFixture api) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("px-web-").FullName;

    [Fact]
    public async Task Serves_files_falls_back_to_index_and_keeps_api_404s()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<!doctype html><title>Pixela</title>");
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "app-abc123.js"), "console.log(1)");
        using var factory = api.WithWebHostBuilder(b => b.UseSetting("Web:Root", _root));
        using var http = factory.CreateClient();

        var asset = await http.GetAsync(new Uri("/assets/app-abc123.js", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var page = await http.GetAsync(new Uri("/clientes/123", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>Pixela</title>", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-cache", page.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri("/panel/no-existe", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri("/meta/otra-cosa", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/health", UriKind.Relative))).StatusCode);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
