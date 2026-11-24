using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pixela.Application.Chat;
using Pixela.Application.Http;
using Pixela.Infrastructure.Http;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Chat;

/// <summary>
/// Cliente MCP por HTTP (transporte «streamable»: JSON-RPC por POST, respuesta en JSON o en un flujo de eventos). Sin estado entre llamadas:
/// cada operación abre su sesión, hace lo suyo y la cierra. Solo conecta a IPs públicas (misma defensa SSRF que los webhooks, comprobada
/// al conectar), no sigue redirecciones y limita tiempo y tamaño de la respuesta.
/// </summary>
public sealed class McpHttpClient : IMcpClient, IDisposable
{
    private const string ProtocolVersion = "2025-03-26";
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxPages = 5;

    private readonly HttpClient http;

    public McpHttpClient(IOptions<OutboundHttpOptions> options, HttpMessageHandler? handler = null)
    {
        var allowPrivate = options.Value.AllowPrivateNetworks;
        http = handler is null
            ? new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = (context, ct) => SafeConnect.ConnectAsync(context, allowPrivate, ct),
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
    }

    public void Dispose() => http.Dispose();

    public Task<IReadOnlyList<McpTool>> ListToolsAsync(Uri url, string? token, CancellationToken ct) =>
        WithSessionAsync<IReadOnlyList<McpTool>>(url, token, async session =>
        {
            var tools = new List<McpTool>();
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var result = await session.RequestAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, ct);
                foreach (var t in result?["tools"] as JsonArray ?? [])
                {
                    if (t?["name"]?.GetValue<string>() is { Length: > 0 } name)
                    {
                        tools.Add(new McpTool(name, t["description"]?.GetValue<string>(), t["inputSchema"]?.DeepClone()));
                    }
                }

                cursor = result?["nextCursor"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }

            return tools;
        }, ct);

    public Task<string> CallToolAsync(Uri url, string? token, string tool, JsonObject arguments, CancellationToken ct) =>
        WithSessionAsync(url, token, async session =>
        {
            var result = await session.RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, ct);
            var text = new StringBuilder();
            foreach (var block in result?["content"] as JsonArray ?? [])
            {
                if (block?["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { } t)
                {
                    text.AppendLine(t);
                }
                else
                {
                    text.AppendLine("[contenido que no es texto, omitido]");
                }
            }

            var output = text.ToString().Trim();
            if (result?["isError"]?.GetValue<bool>() == true)
            {
                throw new McpException(output.Length > 0 ? Truncate(output) : "La herramienta devolvió un error.");
            }

            return output.Length > 0 ? output : "(sin resultados)";
        }, ct);

    private async Task<T> WithSessionAsync<T>(Uri url, string? token, Func<Session, Task<T>> work, CancellationToken ct)
    {
        var session = new Session(http, url, token);
        try
        {
            await session.InitializeAsync(ct);
            return await work(session);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new McpException("El servidor tardó demasiado en responder.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            throw new McpException("No se pudo conectar con el servidor.", ex);
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    private sealed class Session(HttpClient http, Uri url, string? token)
    {
        private string? sessionId;
        private string protocol = ProtocolVersion;
        private int nextId;

        public async Task InitializeAsync(CancellationToken ct)
        {
            var init = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Pixela", ["version"] = "1.0" },
            }, ct);
            protocol = init?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion;
            using var _ = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
        }

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            var id = ++nextId;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            using var response = await SendAsync(message, ct);
            if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            {
                sessionId = ids.FirstOrDefault();
            }

            var body = await ReadBodyAsync(response, ct);
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var reply = type == "text/event-stream" ? FindInEvents(body, id) : JsonNode.Parse(body) as JsonObject;
            if (reply is null)
            {
                throw new McpException("El servidor no respondió con el formato esperado.");
            }

            if (reply["error"] is JsonObject error)
            {
                throw new McpException(Truncate("El servidor devolvió un error: " + (error["message"]?.GetValue<string>() ?? "sin detalle")));
            }

            return reply["result"];
        }

        public async Task CloseAsync()
        {
            if (sessionId is null)
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var request = Build(HttpMethod.Delete, null);
                using var _ = await http.SendAsync(request, cts.Token);
            }
            catch (Exception)
            {
                // Cerrar la sesión es una cortesía: si falla, el servidor la vence solo.
            }
        }

        private async Task<HttpResponseMessage> SendAsync(JsonObject message, CancellationToken ct)
        {
            using var request = Build(HttpMethod.Post, message);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            response.Dispose();
            throw new McpException(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "El servidor rechazó el acceso: revisa el token."
                : status is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                    ? "El servidor redirige a otra dirección: usa la dirección final del servidor MCP."
                    : $"El servidor respondió {(int)status}: ¿es la dirección de un servidor MCP?");
        }

        private HttpRequestMessage Build(HttpMethod method, JsonObject? message)
        {
            var request = new HttpRequestMessage(method, url);
            if (message is not null)
            {
                request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
            }

            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (sessionId is not null)
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            }

            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
            return request;
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes)
                {
                    throw new McpException("La respuesta del servidor es demasiado grande.");
                }
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>En un flujo de eventos (SSE), el mensaje que responde a nuestra petición va en líneas «data:».</summary>
        private static JsonObject? FindInEvents(string body, int id)
        {
            var data = new StringBuilder();
            foreach (var raw in body.Split('\n').Append(""))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n');
                    continue;
                }

                if (line.Length > 0 || data.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(data.ToString()) is JsonObject node && node["id"]?.GetValue<int>() == id)
                    {
                        return node;
                    }
                }
                catch (JsonException)
                {
                    // Un evento que no es JSON (un ping, por ejemplo) no es la respuesta.
                }

                data.Clear();
            }

            return null;
        }
    }
}
