using System.Text.Json.Nodes;

namespace Pixela.Application.Chat;

/// <summary>
/// Ajustes técnicos de la llamada a Claude (no secretos, con valores por defecto). El modelo, el esfuerzo y los precios los decide la agencia
/// (<c>AgencySettings</c>) y la llave es de cada bot: aquí no hay ninguna llave.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Modelo con el que se prueba una llave (la llamada mínima de <c>IAiKeyTester</c>) y respaldo si la petición no trae modelo.</summary>
    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>Esfuerzo por defecto si la petición no trae uno. Vacío = no enviarlo.</summary>
    public string Effort { get; set; } = "low";

    /// <summary>Cuántas rondas de consulta a herramientas (MCP) puede hacer el modelo antes de tener que responder.</summary>
    public int MaxToolRounds { get; set; } = 4;

    public AnthropicOptions Anthropic { get; set; } = new();
}

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;

    /// <summary>Espera base entre reintentos (se multiplica por el número de intento).</summary>
    public int RetryDelayMs { get; set; } = 700;
}

public sealed record AiMessage(string Role, string Content);

/// <param name="System">Reglas fijas de la plataforma más las instrucciones del negocio (estable entre turnos: se puede cachear).</param>
/// <param name="Context">Fragmentos recuperados para esta pregunta (datos, nunca instrucciones).</param>
/// <param name="Messages">Turnos de la conversación; el último es del usuario.</param>
/// <param name="Tools">Herramientas (de servidores MCP) que el modelo puede pedir para consultar datos en vivo.</param>
/// <param name="RunTool">Ejecuta una herramienta pedida por el modelo y devuelve su resultado como texto (datos, nunca instrucciones).</param>
/// <param name="ApiKey">Llave de Claude del bot. Sin ella no se llama al modelo.</param>
/// <param name="Effort">Esfuerzo de razonamiento (de la agencia). Null = el de <see cref="AiOptions"/>.</param>
public sealed record AiRequest(
    string System,
    IReadOnlyList<KnowledgeHit> Context,
    IReadOnlyList<AiMessage> Messages,
    int MaxTokens,
    string? Model,
    IReadOnlyList<AiTool>? Tools = null,
    AiToolRunner? RunTool = null,
    string? ApiKey = null,
    string? Effort = null);

/// <summary>Una herramienta que el modelo puede usar. El nombre solo lleva letras, números, guion y guion bajo (máximo 64).</summary>
public sealed record AiTool(string Name, string Description, JsonNode? InputSchema);

public delegate Task<string> AiToolRunner(string toolName, JsonObject input, CancellationToken ct);

public sealed record AiReply(string Text, int TokensIn, int TokensOut, string Model, int ToolCalls = 0);

/// <summary>El proveedor de IA no pudo responder (llave inválida, límite del proveedor, caída…).</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAiGateway
{
    /// <exception cref="AiUnavailableException">El proveedor no pudo responder.</exception>
    Task<AiReply> CompleteAsync(AiRequest request, CancellationToken ct);

    /// <summary>True si responde con un modelo de lenguaje (puede razonar sobre todo el conocimiento); false si solo devuelve el fragmento más relevante.</summary>
    bool UsesModel => true;

    /// <summary>True si necesita una llave para responder; sin ninguna, la conversación se atiende en modo sin modelo.</summary>
    bool RequiresKey => false;
}

/// <summary>Sin modelo de lenguaje: devuelve el fragmento más relevante tal cual. Sirve para probar el flujo completo sin llave de IA.</summary>
public sealed class ExtractiveGateway : IAiGateway
{
    public const string ModelName = "extractive";
    private const string AnswerMarker = "\nRespuesta:";

    public bool UsesModel => false;

    public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        var best = request.Context.Count > 0 ? request.Context[0].Text : "";

        // Una pregunta frecuente se guarda como "Pregunta: …\nRespuesta: …": al visitante solo le interesa la respuesta.
        var marker = best.IndexOf(AnswerMarker, StringComparison.Ordinal);
        var text = marker >= 0 && best.StartsWith("Pregunta:", StringComparison.Ordinal)
            ? best[(marker + AnswerMarker.Length)..].Trim()
            : best.Trim();

        return Task.FromResult(new AiReply(text, 0, 0, ModelName));
    }
}
