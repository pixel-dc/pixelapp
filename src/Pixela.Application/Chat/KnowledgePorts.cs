namespace Pixela.Application.Chat;

/// <summary>Un fragmento recuperado para responder una pregunta.</summary>
public sealed record KnowledgeHit(string Text, string SourceTitle, double Rank);

/// <summary>Búsqueda de texto completo sobre el conocimiento de un bot. La implementa Infrastructure (PostgreSQL).</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid botId, string query, int limit, CancellationToken ct);

    /// <summary>Todo el conocimiento del bot si pesa a lo sumo <paramref name="maxChars"/> caracteres; null si es más grande (entonces se busca).</summary>
    Task<IReadOnlyList<KnowledgeHit>?> AllIfSmallAsync(Guid botId, int maxChars, CancellationToken ct);
}

/// <summary>Extrae el texto de un PDF. Lanza <see cref="InvalidDataException"/> si el archivo no se puede leer.</summary>
public interface IPdfTextExtractor
{
    string Extract(Stream pdf, int maxPages);
}

public sealed record WebPage(string Title, string Text);

/// <summary>Lee las filas de una tabla de Supabase (PostgREST) con una llave de solo lectura y devuelve el JSON tal cual (un arreglo de objetos).</summary>
public interface ICatalogFetcher
{
    /// <exception cref="InvalidOperationException">Fallo con un mensaje apto para el usuario (llave sin permiso, tabla inexistente, demasiado grande…).</exception>
    Task<string> FetchAsync(Uri projectUrl, string table, string? columns, string apiKey, CancellationToken ct);
}

/// <summary>Descarga una página web pública y devuelve su texto. Debe rechazar destinos privados (SSRF); lanza <see cref="InvalidOperationException"/> con un mensaje apto para el usuario si no puede.</summary>
public interface IWebPageFetcher
{
    Task<WebPage> FetchAsync(Uri url, CancellationToken ct);
}
