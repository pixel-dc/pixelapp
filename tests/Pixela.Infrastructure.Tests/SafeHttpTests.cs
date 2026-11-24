using System.Net;
using System.Net.Sockets;
using System.Text;
using Pixela.Application.Http;
using Pixela.Infrastructure.Http;

namespace Pixela.Infrastructure.Tests;

/// <summary>Servidor HTTP mínimo: acepta una conexión, guarda la petición y responde con el código indicado.</summary>
internal sealed class TinyHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;

    public TinyHttpServer(int statusCode, string? redirectTo = null)
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[16384];
                    var read = await stream.ReadAsync(buffer);
                    LastRequest = Encoding.UTF8.GetString(buffer, 0, read);
                    Hits++;
                    var location = redirectTo is null ? string.Empty : $"Location: {redirectTo}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} X\r\n{location}Content-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Servidor detenido al terminar la prueba.
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string? LastRequest { get; private set; }
    public int Hits { get; private set; }

    public void Dispose() => _listener.Stop();
}

public class SafeHttpTests
{
    private static HttpClient Create(bool allowPrivate) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, ct) => SafeConnect.ConnectAsync(context, allowPrivate, ct),
        })
        { Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task Loopback_is_blocked_by_default_and_nothing_is_sent()
    {
        using var server = new TinyHttpServer(200);
        using var http = Create(allowPrivate: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/x")));
        Assert.Equal(0, server.Hits);
    }

    [Fact]
    public async Task Localhost_name_is_blocked_too()
    {
        using var server = new TinyHttpServer(200);
        using var http = Create(allowPrivate: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri($"http://localhost:{server.Port}/x")));
        Assert.Equal(0, server.Hits);
    }

    [Fact]
    public async Task Private_networks_can_be_allowed_for_development()
    {
        using var server = new TinyHttpServer(204);
        using var http = Create(allowPrivate: true);

        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/x"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, server.Hits);
    }

    [Fact]
    public async Task Redirects_are_never_followed()
    {
        using var internalServer = new TinyHttpServer(200);
        using var redirector = new TinyHttpServer(302, redirectTo: $"http://127.0.0.1:{internalServer.Port}/secreto");
        using var http = Create(allowPrivate: true);

        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{redirector.Port}/x"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, internalServer.Hits);
    }

    [Theory]
    [InlineData("http://tienda.com/mcp", "https")]
    [InlineData("https://user:pass@tienda.com/mcp", "credenciales")]
    [InlineData("https://localhost/mcp", "público")]
    [InlineData("https://servidor.internal/mcp", "público")]
    [InlineData("https://10.0.0.5/mcp", "público")]
    [InlineData("https://169.254.169.254/latest", "público")]
    [InlineData("https://intranet/mcp", "público")]
    [InlineData("no es una url", "válida")]
    public void Validator_rejects_unsafe_urls(string url, string reason) =>
        Assert.Contains(reason, OutboundUrlValidator.Validate(url, new OutboundHttpOptions()), StringComparison.Ordinal);

    [Fact]
    public void Validator_accepts_public_https() =>
        Assert.Null(OutboundUrlValidator.Validate("https://tienda.com/mcp", new OutboundHttpOptions()));
}
