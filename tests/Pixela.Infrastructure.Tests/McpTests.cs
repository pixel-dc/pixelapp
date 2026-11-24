using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pixela.Application.Chat;
using Pixela.Application.Http;
using Pixela.Infrastructure.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Tests;

/// <summary>El cliente MCP (protocolo por HTTP) y el bucle de herramientas del modelo, con servidores simulados.</summary>
public class McpTests
{
    private static HttpResponseMessage Json(string body, string type = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static McpHttpClient Client(FakeHandler handler) => new(Options.Create(new OutboundHttpOptions()), handler);

    private static HttpResponseMessage Init(HttpRequestMessage _)
    {
        var r = Json("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{}}}""");
        r.Headers.Add("Mcp-Session-Id", "sesion-1");
        return r;
    }

    private static HttpResponseMessage Accepted(HttpRequestMessage _) => new(HttpStatusCode.Accepted);

    private static readonly Uri Server = new("https://tienda.example.com/mcp");

    [Fact]
    public async Task Lists_tools_reading_an_event_stream_and_carries_the_token_and_the_session()
    {
        var handler = new FakeHandler(
            Init,
            Accepted,
            _ => Json("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"buscar_producto\",\"description\":\"Busca\",\"inputSchema\":{\"type\":\"object\"}}]}}\n\n", "text/event-stream"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(handler);

        var tools = await client.ListToolsAsync(Server, "tok-secreto", CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("buscar_producto", tool.Name);
        Assert.Equal("Busca", tool.Description);

        Assert.Equal(4, handler.Received.Count); // inicia, avisa, lista y cierra la sesión
        Assert.All(handler.Received, r => Assert.Equal("tok-secreto", r.Request.Headers.Authorization!.Parameter));
        Assert.False(handler.Received[0].Request.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("sesion-1", handler.Received[2].Request.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("tools/list", JsonNode.Parse(handler.Received[2].Body)!["method"]!.GetValue<string>());
        Assert.Equal(HttpMethod.Delete, handler.Received[3].Request.Method);
    }

    [Fact]
    public async Task Calls_a_tool_and_returns_its_text()
    {
        var handler = new FakeHandler(
            Init,
            Accepted,
            _ => Json("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"Camiseta M: 12 en existencia"}]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(handler);

        var text = await client.CallToolAsync(Server, null, "buscar_producto", new JsonObject { ["q"] = "camiseta" }, CancellationToken.None);

        Assert.Equal("Camiseta M: 12 en existencia", text);
        var call = JsonNode.Parse(handler.Received[2].Body)!;
        Assert.Equal("tools/call", call["method"]!.GetValue<string>());
        Assert.Equal("buscar_producto", call["params"]!["name"]!.GetValue<string>());
        Assert.Equal("camiseta", call["params"]!["arguments"]!["q"]!.GetValue<string>());
        Assert.Null(handler.Received[0].Request.Headers.Authorization); // sin token no se envía la cabecera
    }

    [Fact]
    public async Task A_tool_that_reports_an_error_becomes_an_exception_with_its_message()
    {
        var handler = new FakeHandler(
            Init,
            Accepted,
            _ => Json("""{"jsonrpc":"2.0","id":2,"result":{"isError":true,"content":[{"type":"text","text":"Pedido no encontrado"}]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(Server, null, "estado_pedido", new JsonObject(), CancellationToken.None));

        Assert.Contains("Pedido no encontrado", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "token")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    public async Task A_server_that_refuses_gives_a_message_the_user_can_act_on(HttpStatusCode status, string expected)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(Server, "x", CancellationToken.None));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_is_reported_instead_of_followed()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(Server, null, CancellationToken.None));

        Assert.Contains("redirige", ex.Message, StringComparison.Ordinal);
    }

    // ---- bucle de herramientas del modelo ----

    private static AnthropicGateway Gateway(FakeHandler handler, int rounds = 4) =>
        new(Options.Create(new AiOptions
        {
            MaxToolRounds = rounds,
            Anthropic = new AnthropicOptions { MaxRetries = 0, RetryDelayMs = 0 },
        }), handler);

    private const string AskTool = """{"model":"claude-haiku-5-5","stop_reason":"tool_use","content":[{"type":"text","text":"Reviso."},{"type":"tool_use","id":"tu_1","name":"abc123_buscar","input":{"q":"camiseta"}}],"usage":{"input_tokens":100,"output_tokens":10}}""";
    private const string Final = """{"model":"claude-haiku-5-5","stop_reason":"end_turn","content":[{"type":"text","text":"Quedan 12."}],"usage":{"input_tokens":150,"output_tokens":5}}""";

    private static AiRequest ToolRequest(AiToolRunner run) => new(
        "Reglas", [], [new AiMessage("user", "¿Hay camiseta M?")], 300, null,
        [new AiTool("abc123_buscar", "Busca productos", JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""))],
        run,
        ApiKey: "sk-test");

    [Fact]
    public async Task The_model_asks_for_a_tool_gets_the_result_and_then_answers()
    {
        var handler = new FakeHandler(_ => Json(AskTool), _ => Json(Final));
        using var gateway = Gateway(handler);
        JsonObject? received = null;

        var reply = await gateway.CompleteAsync(ToolRequest((name, input, _) =>
        {
            Assert.Equal("abc123_buscar", name);
            received = input;
            return Task.FromResult("Camiseta M: 12");
        }), CancellationToken.None);

        Assert.Equal("Quedan 12.", reply.Text);
        Assert.Equal("camiseta", received!["q"]!.GetValue<string>());
        Assert.Equal(250, reply.TokensIn); // las dos rondas se cobran
        Assert.Equal(15, reply.TokensOut);
        Assert.Equal(1, reply.ToolCalls);

        var first = JsonNode.Parse(handler.Received[0].Body)!;
        Assert.Equal("abc123_buscar", first["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("object", first["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());

        var second = JsonNode.Parse(handler.Received[1].Body)!["messages"]!.AsArray();
        Assert.Equal(3, second.Count);
        Assert.Equal("assistant", second[1]!["role"]!.GetValue<string>());
        Assert.Equal("tool_use", second[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("user", second[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", second[2]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tu_1", second[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("Camiseta M: 12", second[2]!["content"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failing_tool_is_reported_to_the_model_so_it_can_explain_instead_of_breaking_the_chat()
    {
        var handler = new FakeHandler(_ => Json(AskTool), _ => Json(Final));
        using var gateway = Gateway(handler);

        var reply = await gateway.CompleteAsync(ToolRequest((_, _, _) => throw new McpException("El servidor tardó demasiado en responder.")), CancellationToken.None);

        Assert.Equal("Quedan 12.", reply.Text);
        var result = JsonNode.Parse(handler.Received[1].Body)!["messages"]![2]!["content"]![0]!;
        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Contains("tardó demasiado", result["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_the_allowed_rounds_the_model_must_answer_with_what_it_has()
    {
        var handler = new FakeHandler(_ => Json(AskTool), _ => Json(Final));
        using var gateway = Gateway(handler, rounds: 1);

        var reply = await gateway.CompleteAsync(ToolRequest((_, _, _) => Task.FromResult("dato")), CancellationToken.None);

        Assert.Equal("Quedan 12.", reply.Text);
        Assert.Null(JsonNode.Parse(handler.Received[0].Body)!["tool_choice"]);
        Assert.Equal("none", JsonNode.Parse(handler.Received[1].Body)!["tool_choice"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_tools_the_request_is_exactly_as_before()
    {
        var handler = new FakeHandler(_ => Json(Final));
        using var gateway = Gateway(handler);

        var reply = await gateway.CompleteAsync(new AiRequest("Reglas", [], [new AiMessage("user", "Hola")], 300, null, ApiKey: "sk-test"), CancellationToken.None);

        Assert.Equal(0, reply.ToolCalls);
        Assert.Null(JsonNode.Parse(handler.Received.Single().Body)!["tools"]);
    }
}
