using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Application.Http;
using Pixela.Infrastructure.Persistence;
using Pixela.Infrastructure.Http;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Pixela.Infrastructure.Chat;

/// <summary>Búsqueda de texto completo en español sobre <c>knowledge_chunks</c> (columna <c>Tsv</c> generada por PostgreSQL).</summary>
public sealed partial class PostgresKnowledgeSearch(AppDbContext db, IClientScope tenant) : IKnowledgeSearch
{
    private const int MaxTerms = 12;

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}")]
    private static partial Regex Word();

    private sealed class Row
    {
        public string Text { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public double Rank { get; set; }
    }

    private sealed class TotalRow
    {
        public long Total { get; set; }
    }

    public async Task<IReadOnlyList<KnowledgeHit>?> AllIfSmallAsync(Guid botId, int maxChars, CancellationToken ct)
    {
        var tenantId = tenant.ClientId;
        var all = tenant.AllClients;
        var total = await db.Database.SqlQuery<TotalRow>($"""
            SELECT COALESCE(SUM(length(k."Text")), 0)::bigint AS "Total" FROM knowledge_chunks k
            WHERE k."BotId" = {botId} AND ({all} OR k."ClientId" = {tenantId})
            """).SingleAsync(ct);
        if (total.Total > maxChars)
        {
            return null;
        }

        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT k."Text" AS "Text", s."Title" AS "SourceTitle", 0::float8 AS "Rank"
            FROM knowledge_chunks k
            JOIN knowledge_sources s ON s."Id" = k."SourceId"
            WHERE k."BotId" = {botId} AND ({all} OR k."ClientId" = {tenantId})
            ORDER BY s."Id", k."Id"
            """).ToListAsync(ct);
        return rows.Select(r => new KnowledgeHit(r.Text, r.SourceTitle, r.Rank)).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid botId, string query, int limit, CancellationToken ct)
    {
        // Las preguntas son frases libres: se buscan sus palabras con "o" (más coincidencias = mejor posición), no todas a la vez.
        var terms = Word().Matches(query.Length > 500 ? query[..500] : query).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(MaxTerms).ToList();
        if (terms.Count == 0)
        {
            return [];
        }

        var tsquery = string.Join(" | ", terms);
        var tenantId = tenant.ClientId;
        var all = tenant.AllClients;

        // SQL directo (no pasa por el filtro global): el cliente se exige aquí de forma explícita (la vista de agencia ve todos).
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT k."Text" AS "Text", s."Title" AS "SourceTitle", ts_rank_cd(k."Tsv", q.query)::float8 AS "Rank"
            FROM knowledge_chunks k
            JOIN knowledge_sources s ON s."Id" = k."SourceId"
            CROSS JOIN (SELECT to_tsquery('spanish', {tsquery}) AS query) q
            WHERE k."BotId" = {botId} AND ({all} OR k."ClientId" = {tenantId}) AND k."Tsv" @@ q.query
            ORDER BY 3 DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        return rows.Select(r => new KnowledgeHit(r.Text, r.SourceTitle, r.Rank)).ToList();
    }
}

/// <summary>Texto de un PDF con PdfPig (Apache-2.0), en orden de lectura.</summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public string Extract(Stream pdf, int maxPages)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var text = new StringBuilder();
            for (var number = 1; number <= Math.Min(document.NumberOfPages, maxPages); number++)
            {
                text.AppendLine(ContentOrderTextExtractor.GetText(document.GetPage(number)));
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("No se pudo leer el PDF.", ex);
        }
    }
}

/// <summary>
/// Descarga páginas web para el conocimiento de un bot. Solo conecta a IPs públicas (misma defensa SSRF que los webhooks, comprobada al
/// conectar), sigue hasta 4 redirecciones revalidando cada destino, y limita tiempo, tamaño y tipo de contenido.
/// </summary>
public sealed class SafeWebPageFetcher : IWebPageFetcher, IDisposable
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 4;

    private static readonly string[] BlockTags = ["p", "div", "li", "h1", "h2", "h3", "h4", "h5", "h6", "br", "tr", "section", "article", "ul", "ol", "table", "blockquote", "pre"];
    private static readonly string[] Noise = ["script", "style", "noscript", "nav", "footer", "header", "aside", "form", "svg", "iframe", "template"];
    private static readonly string[] AllowedTypes = ["text/html", "application/xhtml+xml", "text/plain"];

    private readonly HttpClient _client;

    public SafeWebPageFetcher(IOptions<OutboundHttpOptions> options)
    {
        var allowPrivate = options.Value.AllowPrivateNetworks;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => SafeConnect.ConnectAsync(context, allowPrivate, ct),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PixelappBot", "1.0"));
    }

    public async Task<WebPage> FetchAsync(Uri url, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await _client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location is { } location ? new Uri(current, location) : null;
                if (next is null || next.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException("La página redirige a un destino no válido.");
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"La página respondió {(int)response.StatusCode}.");
            }

            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (!AllowedTypes.Contains(type))
            {
                throw new InvalidOperationException("Solo se pueden leer páginas web o texto (no PDF, imágenes ni otros archivos).");
            }

            var html = await ReadLimitedAsync(response, ct);
            return type == "text/plain" ? new WebPage(url.Host, html) : ExtractText(html, url.Host);
        }

        throw new InvalidOperationException("La página redirige demasiadas veces.");
    }

    public void Dispose() => _client.Dispose();

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new InvalidOperationException("La página es demasiado grande (máximo 2 MB).");
            }

            buffer.Write(chunk, 0, read);
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8");
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Texto legible de una página: título y contenido principal sin menús, scripts ni pies de página.</summary>
    public static WebPage ExtractText(string html, string fallbackTitle)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var title = HtmlEntity.DeEntitize(document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "").Trim();
        foreach (var node in document.DocumentNode.Descendants().Where(n => Noise.Contains(n.Name)).ToList())
        {
            node.Remove();
        }

        // Los saltos entre bloques evitan que "Hola" y "Mundo" de dos <p> acaben pegados.
        foreach (var node in document.DocumentNode.Descendants().Where(n => BlockTags.Contains(n.Name)).ToList())
        {
            node.ParentNode?.InsertAfter(document.CreateTextNode("\n"), node);
        }

        var root = document.DocumentNode.SelectSingleNode("//main") ?? document.DocumentNode.SelectSingleNode("//article")
            ?? document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
        var text = HtmlEntity.DeEntitize(root.InnerText);
        return new WebPage(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title, text);
    }
}
