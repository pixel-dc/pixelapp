using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pixela.Application.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Chat;

/// <summary>Responde con la API de mensajes de Anthropic. Las reglas del sistema van con <c>cache_control</c> y el contexto recuperado, aparte.</summary>
public sealed class AnthropicGateway : IAiGateway, IAiKeyTester, IDisposable
{
    private const string Version = "2023-06-01";
    private readonly HttpClient http;
    private readonly AiOptions options;

    public AnthropicGateway(IOptions<AiOptions> options, HttpMessageHandler? handler = null)
    {
        this.options = options.Value;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(this.options.Anthropic.TimeoutSeconds);
    }

    public void Dispose() => http.Dispose();

    public bool RequiresKey => true;

    public async Task<AiReply> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            // La llave es de cada bot: sin ella no hay a quién cobrarle la llamada. ChatService ya atiende sin modelo en ese caso.
            throw new AiUnavailableException("El chatbot no tiene llave de Claude.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model;
        var body = BuildBody(request, model, request.Effort ?? options.Effort);
        var hasTools = request.Tools is { Count: > 0 } && request.RunTool is not null;

        var tokensIn = 0;
        var tokensOut = 0;
        var calls = 0;
        for (var round = 0; ; round++)
        {
            var last = round >= options.MaxToolRounds;
            if (hasTools && last)
            {
                // Se acabaron las rondas de consulta: el modelo debe responder con lo que ya tiene.
                body["tool_choice"] = new JsonObject { ["type"] = "none" };
            }

            var json = await PostAsync(body.ToJsonString(), request.ApiKey, ct);
            var usage = json["usage"];
            // Lo leído de caché y lo escrito en caché también se facturan: se suman a la entrada.
            tokensIn += Count(usage, "input_tokens") + Count(usage, "cache_creation_input_tokens") + Count(usage, "cache_read_input_tokens");
            tokensOut += Count(usage, "output_tokens");

            var uses = json["stop_reason"]?.GetValue<string>() == "tool_use" && json["content"] is JsonArray;
            if (!uses || !hasTools || last)
            {
                var text = ReadText(json);
                return new AiReply(text, tokensIn, tokensOut, json["model"]?.GetValue<string>() ?? model, calls);
            }

            // El modelo pidió herramientas: se ejecutan (cada fallo vuelve como resultado, para que lo explique) y se le devuelve el resultado.
            var content = (JsonArray)json["content"]!;
            var messages = (JsonArray)body["messages"]!;
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.ToJsonString()) });
            var results = new JsonArray();
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() != "tool_use")
                {
                    continue;
                }

                calls++;
                var id = block["id"]?.GetValue<string>() ?? "";
                var name = block["name"]?.GetValue<string>() ?? "";
                var input = block["input"] as JsonObject ?? new JsonObject();
                string output;
                var isError = false;
                try
                {
                    output = await request.RunTool!(name, input, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output = "No se pudo consultar: " + ex.Message;
                    isError = true;
                }

                var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output };
                if (isError)
                {
                    result["is_error"] = true;
                }

                results.Add(result);
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
    }

    /// <summary>Llamada mínima (un token) para saber si la llave sirve. Solo distingue "la rechazan" de "no se pudo comprobar".</summary>
    public async Task<AiKeyCheck> TestAsync(string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = 1,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hola" }),
        };
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Anthropic.BaseUrl), "/v1/messages"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            message.Headers.Add("x-api-key", key);
            message.Headers.Add("anthropic-version", Version);
            using var response = await http.SendAsync(message, ct);
            if (response.IsSuccessStatusCode)
            {
                return AiKeyCheck.Valid;
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? AiKeyCheck.Rejected : AiKeyCheck.Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return AiKeyCheck.Unavailable;
        }
    }

    private async Task<JsonObject> PostAsync(string body, string apiKey, CancellationToken ct)
    {
        var url = new Uri(new Uri(options.Anthropic.BaseUrl), "/v1/messages");

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
                message.Headers.Add("x-api-key", apiKey);
                message.Headers.Add("anthropic-version", Version);

                response = await http.SendAsync(message, ct);
                if (response.IsSuccessStatusCode)
                {
                    return await ReadJsonAsync(response, ct);
                }

                if (!IsRetryable(response.StatusCode) || attempt >= options.Anthropic.MaxRetries)
                {
                    // El cuerpo del error no se propaga: puede traer detalles de la cuenta.
                    throw new AiUnavailableException($"Anthropic respondió {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= options.Anthropic.MaxRetries)
                {
                    throw new AiUnavailableException("No se pudo contactar con Anthropic.", ex);
                }
            }
            finally
            {
                response?.Dispose();
            }

            await Task.Delay(options.Anthropic.RetryDelayMs * (attempt + 1), ct);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500 || status == (HttpStatusCode)529;

    internal static JsonObject BuildBody(AiRequest request, string model, string? effort = null)
    {
        // Bloque 1 (estable): reglas y datos del negocio, cacheable. Bloque 2: contexto de esta pregunta.
        var system = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            },
        };
        if (request.Context.Count > 0)
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = ContextBlock.Render(request.Context) });
        }

        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = system,
            ["messages"] = messages,
        };
        if (!string.IsNullOrWhiteSpace(effort))
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema is JsonObject schema ? JsonNode.Parse(schema.ToJsonString()) : new JsonObject { ["type"] = "object" },
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new AiUnavailableException("Respuesta vacía de Anthropic.");
        }
        catch (JsonException ex)
        {
            throw new AiUnavailableException("Respuesta ilegible de Anthropic.", ex);
        }
    }

    private static string ReadText(JsonObject json)
    {
        var text = new StringBuilder();
        if (json["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { } t)
                {
                    text.Append(t);
                }
            }
        }

        if (text.Length == 0)
        {
            throw new AiUnavailableException("Anthropic no devolvió texto.");
        }

        return text.ToString().Trim();
    }

    private static int Count(JsonNode? usage, string name) => usage?[name]?.GetValue<int>() ?? 0;
}
