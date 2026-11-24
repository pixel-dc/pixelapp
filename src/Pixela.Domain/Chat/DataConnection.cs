namespace Pixela.Domain.Chat;

/// <summary>
/// Un servidor MCP (WooCommerce, Supabase, lo que sea) al que el chatbot puede consultar datos en vivo mientras conversa.
/// Solo las herramientas marcadas en <see cref="AllowedTools"/> llegan al modelo.
/// </summary>
public class DataConnection
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BotId { get; set; }

    public required string Name { get; set; }

    /// <summary>Dirección https del servidor MCP (transporte HTTP).</summary>
    public required string Url { get; set; }

    /// <summary>Token de acceso (se envía como Bearer), cifrado. Nunca sale del servidor.</summary>
    public string? SecretEncrypted { get; set; }

    /// <summary>Nombres de las herramientas que el bot puede usar, separados por coma. Vacío = ninguna.</summary>
    public string AllowedTools { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Último error al conectar o al consultar, para mostrarlo en el panel.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
