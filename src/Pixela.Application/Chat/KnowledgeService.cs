using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

public sealed record FaqItem(string? Question, string? Answer);

public sealed record AddFaqRequest(string? Title, List<FaqItem>? Items);

public sealed record ProductItem(string? Name, string? Price, string? Description, string? ImageUrl, string? Category);

public sealed record AddProductsRequest(string? Title, List<ProductItem>? Items);

public sealed record AddTextRequest(string? Title, string? Text);

public sealed record AddUrlRequest(string? Url, string? Title);

/// <summary>Cambios a una fuente cargada a mano: según su tipo se usa el texto, las preguntas o los productos.</summary>
public sealed record UpdateSourceRequest(string? Title, string? Text, List<FaqItem>? Faq, List<ProductItem>? Products);

/// <param name="Rebuilt">Se reconstruyó a partir del texto indexado (la fuente es anterior a la edición): puede variar el orden o el formato.</param>
public sealed record SourceContent(Guid Id, string Kind, string Title, string? Text, IReadOnlyList<FaqItem>? Faq, IReadOnlyList<ProductItem>? Products, bool Rebuilt);

public sealed record AddSupabaseRequest(
    string? Title, string? Url, string? Table, string? Columns, string? ApiKey, int? RefreshMinutes, string? LinkTemplate = null, string? ImageColumn = null);

public sealed record SourceView(
    Guid Id, string Kind, string Title, string? SourceUrl, string Status, string? Error, int Chunks, DateTimeOffset CreatedAt,
    string? Table = null, string? Columns = null, int? RefreshMinutes = null, DateTimeOffset? SyncedAt = null, string? LinkTemplate = null, string? ImageColumn = null);

/// <summary>Información que el bot puede usar para responder: se trocea, se indexa y se busca con texto completo.</summary>
public sealed partial class KnowledgeService(IAppDbContext db, IPdfTextExtractor pdf, IKnowledgeSearch search, ISecretProtector secrets, TimeProvider clock)
{
    /// <summary>Solo proyectos de Supabase: así el servidor nunca se conecta a una dirección elegida por el visitante (SSRF).</summary>
    [GeneratedRegex(@"^https://[a-z0-9]{10,30}\.supabase\.co/?$")]
    private static partial Regex SupabaseHost();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex Identifier();

    public static readonly int[] RefreshChoices = [0, 15, 60, 360, 1440];

    public const int MaxSourcesPerBot = 50;
    public const int MaxTextChars = 200_000;
    public const int MaxPdfBytes = 10 * 1024 * 1024;
    public const int MaxPdfPages = 100;
    public const int MaxFaqItems = 100;
    public const int MaxProducts = 300;
    private const int MaxChunksPerSource = 400;

    public async Task<ServiceResult<IReadOnlyList<SourceView>>> ListAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<IReadOnlyList<SourceView>>.Fail("not_found", "Chatbot no encontrado.");
        }

        var sources = await db.KnowledgeSources.AsNoTracking().Where(s => s.BotId == botId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var counts = await db.KnowledgeChunks.Where(c => c.BotId == botId).GroupBy(c => c.SourceId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return ServiceResult<IReadOnlyList<SourceView>>.Ok(sources.Select(s => ToView(s, counts.GetValueOrDefault(s.Id))).ToList());
    }

    private sealed record Prepared(string Title, IReadOnlyList<string> Chunks, string? Content);

    private readonly record struct Checked(Prepared? Value, ServiceResult<SourceView>? Error);

    private static Checked PrepareFaq(string? rawTitle, List<FaqItem>? rawItems)
    {
        var title = (rawTitle ?? "").Trim();
        var items = (rawItems ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Question) || !string.IsNullOrWhiteSpace(i.Answer)).ToList();
        var fields = new Dictionary<string, string>();
        if (title.Length is 0 or > 300)
        {
            fields["title"] = "El título es obligatorio (máximo 300 caracteres).";
        }

        if (items.Count == 0 || items.Count > MaxFaqItems)
        {
            fields["items"] = $"Añade entre 1 y {MaxFaqItems} preguntas.";
        }
        else if (items.Any(i => string.IsNullOrWhiteSpace(i.Question) || string.IsNullOrWhiteSpace(i.Answer)
            || i.Question!.Trim().Length > 300 || i.Answer!.Trim().Length > 2000))
        {
            fields["items"] = "Cada pregunta necesita su respuesta (pregunta hasta 300 caracteres, respuesta hasta 2000).";
        }

        if (fields.Count > 0)
        {
            return new Checked(null, ServiceResult<SourceView>.Fail("validation_error", fields.Values.First(), fields));
        }

        // Cada pregunta con su respuesta es un fragmento propio: se recuperan completas, sin mezclarse con otras.
        var chunks = items.Select(i => $"Pregunta: {i.Question!.Trim()}\nRespuesta: {i.Answer!.Trim()}").ToList();
        var content = JsonSerializer.Serialize(items.Select(i => new FaqItem(i.Question!.Trim(), i.Answer!.Trim())));
        return new Checked(new Prepared(title, chunks, content), null);
    }

    public async Task<ServiceResult<SourceView>> AddFaqAsync(Guid botId, AddFaqRequest request, CancellationToken ct)
    {
        var p = PrepareFaq(request.Title, request.Items);
        return p.Error ?? await StoreAsync(botId, KnowledgeKind.Faq, p.Value!.Title, null, p.Value.Chunks, ct, p.Value.Content);
    }

    private static Checked PrepareProducts(string? rawTitle, List<ProductItem>? rawItems)
    {
        var title = (rawTitle ?? "").Trim();
        var items = (rawItems ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Name) || !string.IsNullOrWhiteSpace(i.Price) || !string.IsNullOrWhiteSpace(i.Description) || !string.IsNullOrWhiteSpace(i.ImageUrl)).ToList();
        if (title.Length is 0 or > 300)
        {
            return new Checked(null, Invalid("title", "El título es obligatorio (máximo 300 caracteres)."));
        }

        if (items.Count is 0 or > MaxProducts)
        {
            return new Checked(null, Invalid("items", $"Añade entre 1 y {MaxProducts} productos."));
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = i + 1;
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 150)
            {
                return new Checked(null, Invalid("items", $"Producto {row}: el nombre es obligatorio (máximo 150 caracteres)."));
            }

            if ((item.Price?.Trim().Length ?? 0) > 40 || (item.Category?.Trim().Length ?? 0) > 80 || (item.Description?.Trim().Length ?? 0) > 800)
            {
                return new Checked(null, Invalid("items", $"Producto {row} («{item.Name.Trim()}»): precio hasta 40 caracteres, categoría hasta 80 y descripción hasta 800."));
            }

            if (!string.IsNullOrWhiteSpace(item.ImageUrl) && !IsHttpsImageUrl(item.ImageUrl))
            {
                return new Checked(null, Invalid("items", $"Producto {row} («{item.Name.Trim()}»): la imagen debe ser una dirección que empiece por https:// (sin espacios)."));
            }
        }

        return new Checked(new Prepared(title, items.Select(ProductLine).ToList(), JsonSerializer.Serialize(items)), null);
    }

    /// <summary>Productos o platos cargados a mano. Cada uno es un fragmento propio con el mismo formato del catálogo («columna: valor | …», con «foto: …»), que el bot ya sabe usar.</summary>
    public async Task<ServiceResult<SourceView>> AddProductsAsync(Guid botId, AddProductsRequest request, CancellationToken ct)
    {
        var p = PrepareProducts(request.Title, request.Items);
        return p.Error ?? await StoreAsync(botId, KnowledgeKind.Products, p.Value!.Title, null, p.Value.Chunks, ct, p.Value.Content);
    }

    /// <summary>Una dirección https sin datos de acceso ni espacios; la foto se le envía al cliente tal cual.</summary>
    public static bool IsHttpsImageUrl(string? value) =>
        value is { Length: > 0 and <= 2000 } && !value.Trim().Any(char.IsWhiteSpace)
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo);

    private static string ProductLine(ProductItem i)
    {
        static string One(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
        var parts = new List<string> { $"producto: {One(i.Name!)}" };
        if (!string.IsNullOrWhiteSpace(i.Category))
        {
            parts.Add($"categoría: {One(i.Category)}");
        }

        if (!string.IsNullOrWhiteSpace(i.Price))
        {
            parts.Add($"precio: {One(i.Price)}");
        }

        if (!string.IsNullOrWhiteSpace(i.Description))
        {
            parts.Add($"descripción: {One(i.Description)}");
        }

        if (!string.IsNullOrWhiteSpace(i.ImageUrl))
        {
            parts.Add($"foto: {i.ImageUrl.Trim()}");
        }

        return string.Join(" | ", parts);
    }

    private static Checked PrepareText(string? rawTitle, string? rawText)
    {
        var title = (rawTitle ?? "").Trim();
        var text = rawText ?? "";
        if (title.Length is 0 or > 300)
        {
            return new Checked(null, Invalid("title", "El título es obligatorio (máximo 300 caracteres)."));
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextChars)
        {
            return new Checked(null, Invalid("text", $"Escribe el texto (máximo {MaxTextChars:N0} caracteres)."));
        }

        return new Checked(new Prepared(title, Chunker.Split(text), text), null);
    }

    public async Task<ServiceResult<SourceView>> AddTextAsync(Guid botId, AddTextRequest request, CancellationToken ct)
    {
        var p = PrepareText(request.Title, request.Text);
        return p.Error ?? await StoreAsync(botId, KnowledgeKind.Text, p.Value!.Title, null, p.Value.Chunks, ct, p.Value.Content);
    }

    public async Task<ServiceResult<SourceView>> AddPdfAsync(Guid botId, string? title, string? fileName, Stream content, long length, CancellationToken ct)
    {
        if (length is <= 0 or > MaxPdfBytes)
        {
            return Invalid("file", $"El PDF debe pesar entre 1 byte y {MaxPdfBytes / (1024 * 1024)} MB.");
        }

        string text;
        try
        {
            text = pdf.Extract(content, MaxPdfPages);
        }
        catch (InvalidDataException)
        {
            return Invalid("file", "No se pudo leer el PDF (¿está protegido o dañado?).");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Invalid("file", "El PDF no tiene texto que se pueda leer (¿es un escaneo?).");
        }

        if (text.Length > MaxTextChars)
        {
            return Invalid("file", $"El PDF tiene demasiado texto (máximo {MaxTextChars:N0} caracteres).");
        }

        var name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName ?? "") : title.Trim();
        name = string.IsNullOrWhiteSpace(name) ? "PDF" : name[..Math.Min(name.Length, 300)];
        return await StoreAsync(botId, KnowledgeKind.Pdf, name, null, Chunker.Split(text), ct, text);
    }

    /// <summary>Una página web se descarga en segundo plano: la fuente queda pendiente y la procesa el worker.</summary>
    public async Task<ServiceResult<SourceView>> AddUrlAsync(Guid botId, AddUrlRequest request, CancellationToken ct)
    {
        var raw = (request.Url ?? "").Trim();
        if (raw.Length > 2000 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("url", "Indica una dirección http(s) válida.");
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? uri.Host : request.Title.Trim();
        if (title.Length > 300)
        {
            return Invalid("title", "Máximo 300 caracteres.");
        }

        var guard = await CheckBotAndQuotaAsync(botId, ct);
        if (guard is not null)
        {
            return guard;
        }

        var source = new KnowledgeSource
        {
            Id = Guid.CreateVersion7(),
            ClientId = await BotClientAsync(botId, ct),
            BotId = botId,
            Kind = KnowledgeKind.Url,
            Title = title,
            SourceUrl = uri.ToString(),
            Status = KnowledgeStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);
        return ServiceResult<SourceView>.Ok(ToView(source, 0));
    }

    /// <summary>Llaves que NO deben guardarse aquí: dan más acceso del que el bot necesita. Devuelve el motivo, o null si la llave sirve.</summary>
    public static string? WrongKeyKind(string key)
    {
        if (key.StartsWith("sbp_", StringComparison.Ordinal))
        {
            return "Eso es un token personal de tu cuenta de Supabase (da acceso a todos tus proyectos), no una llave del proyecto. Revócalo en Account → Access Tokens y usa la Publishable key (sb_publishable_…) de Project Settings → API Keys.";
        }

        if (key.StartsWith("sb_secret_", StringComparison.Ordinal) || IsServiceRoleJwt(key))
        {
            return "Esa llave tiene acceso total a la base y se salta las políticas de seguridad. Usa la Publishable key (sb_publishable_…) o la anon del proyecto.";
        }

        return null;
    }

    private static bool IsServiceRoleJwt(string key)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || !key.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Contains("\"service_role\"", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Catálogo de Supabase: se valida y queda pendiente; el worker lo lee con la llave de solo lectura.</summary>
    public async Task<ServiceResult<SourceView>> AddSupabaseAsync(Guid botId, AddSupabaseRequest request, CancellationToken ct)
    {
        var url = (request.Url ?? "").Trim();
        if (!SupabaseHost().IsMatch(url))
        {
            return Invalid("url", "Indica la URL del proyecto, con la forma https://xxxxxxxx.supabase.co.");
        }

        var table = (request.Table ?? "").Trim();
        if (!Identifier().IsMatch(table))
        {
            return Invalid("table", "Indica el nombre de la tabla o vista (letras, números y guion bajo).");
        }

        string? columns = null;
        if (!string.IsNullOrWhiteSpace(request.Columns))
        {
            var list = request.Columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            if (list.Count is 0 or > 30 || list.Any(c => !Identifier().IsMatch(c)))
            {
                return Invalid("columns", "Indica hasta 30 columnas separadas por coma (letras, números y guion bajo).");
            }

            columns = string.Join(',', list);
        }

        var linkTemplate = string.IsNullOrWhiteSpace(request.LinkTemplate) ? null : request.LinkTemplate.Trim();
        if (linkTemplate is not null && !CatalogText.IsValidLinkTemplate(linkTemplate))
        {
            return Invalid("link_template", "Escribe una dirección https con columnas entre llaves, por ejemplo https://mitienda.com/producto/{slug}.");
        }

        var imageColumn = string.IsNullOrWhiteSpace(request.ImageColumn) ? null : request.ImageColumn.Trim();
        if (imageColumn is not null && !Identifier().IsMatch(imageColumn))
        {
            return Invalid("image_column", "Indica el nombre de la columna de la foto (letras, números y guion bajo).");
        }

        var key = (request.ApiKey ?? "").Trim();
        if (key.Length is < 20 or > 2000 || key.Any(char.IsWhiteSpace))
        {
            return Invalid("api_key", "Pega la llave de solo lectura (anon o publishable) del proyecto.");
        }

        var wrongKey = WrongKeyKind(key);
        if (wrongKey is not null)
        {
            return Invalid("api_key", wrongKey);
        }

        var refresh = request.RefreshMinutes ?? 0;
        if (!RefreshChoices.Contains(refresh))
        {
            return Invalid("refresh_minutes", "Elige 0 (solo a mano), 15, 60, 360 o 1440 minutos.");
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? $"Catálogo · {table}" : request.Title.Trim();
        if (title.Length > 300)
        {
            return Invalid("title", "Máximo 300 caracteres.");
        }

        var guard = await CheckBotAndQuotaAsync(botId, ct);
        if (guard is not null)
        {
            return guard;
        }

        var source = new KnowledgeSource
        {
            Id = Guid.CreateVersion7(),
            ClientId = await BotClientAsync(botId, ct),
            BotId = botId,
            Kind = KnowledgeKind.Supabase,
            Title = title,
            SourceUrl = url.TrimEnd('/'),
            SourceTable = table,
            SourceColumns = columns,
            LinkTemplate = linkTemplate,
            ImageColumn = imageColumn,
            SecretEncrypted = secrets.Protect(key),
            RefreshMinutes = refresh == 0 ? null : refresh,
            Status = KnowledgeStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);
        return ServiceResult<SourceView>.Ok(ToView(source, 0));
    }

    /// <summary>Lo que hay que mostrar para editar una fuente cargada a mano.</summary>
    public async Task<ServiceResult<SourceContent>> GetContentAsync(Guid botId, Guid sourceId, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.BotId == botId, ct);
        if (source is null)
        {
            return ServiceResult<SourceContent>.Fail("not_found", "Fuente no encontrada.");
        }

        var kind = source.Kind.ToString().ToLowerInvariant();
        switch (source.Kind)
        {
            case KnowledgeKind.Text or KnowledgeKind.Pdf:
                {
                    if (source.Content is not null)
                    {
                        return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, source.Content, null, null, false));
                    }

                    var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => c.SourceId == sourceId).OrderBy(c => c.Id).Select(c => c.Text).ToListAsync(ct);
                    return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, Chunker.Join(chunks), null, null, true));
                }

            case KnowledgeKind.Faq:
                {
                    if (source.Content is not null)
                    {
                        return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, null, JsonSerializer.Deserialize<List<FaqItem>>(source.Content), null, false));
                    }

                    var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => c.SourceId == sourceId).OrderBy(c => c.Id).Select(c => c.Text).ToListAsync(ct);
                    return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, null, chunks.Select(ParseFaq).ToList(), null, true));
                }

            case KnowledgeKind.Products:
                {
                    if (source.Content is not null)
                    {
                        return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, null, null, JsonSerializer.Deserialize<List<ProductItem>>(source.Content), false));
                    }

                    var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => c.SourceId == sourceId).OrderBy(c => c.Id).Select(c => c.Text).ToListAsync(ct);
                    return ServiceResult<SourceContent>.Ok(new SourceContent(source.Id, kind, source.Title, null, null, chunks.Select(ParseProduct).ToList(), true));
                }

            default:
                return ServiceResult<SourceContent>.Fail("invalid_state", "Esta fuente no se edita a mano: usa Reindexar para volver a leerla.");
        }
    }

    private static FaqItem ParseFaq(string chunk)
    {
        const string answerMark = "\nRespuesta: ";
        var split = chunk.IndexOf(answerMark, StringComparison.Ordinal);
        var question = split >= 0 ? chunk[..split] : chunk;
        var answer = split >= 0 ? chunk[(split + answerMark.Length)..] : "";
        return new FaqItem(question.StartsWith("Pregunta: ", StringComparison.Ordinal) ? question["Pregunta: ".Length..] : question, answer);
    }

    private static ProductItem ParseProduct(string chunk)
    {
        string? name = null, price = null, description = null, image = null, category = null;
        foreach (var part in chunk.Split(" | "))
        {
            var colon = part.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var value = part[(colon + 2)..];
            switch (part[..colon])
            {
                case "producto": name = value; break;
                case "categoría": category = value; break;
                case "precio": price = value; break;
                case "descripción": description = value; break;
                case "foto": image = value; break;
            }
        }

        return new ProductItem(name, price, description, image, category);
    }

    /// <summary>Cambia una fuente cargada a mano: se reemplaza lo indexado de esa fuente por lo nuevo, en una sola transacción. Las que se leen de fuera (página, catálogo) se vuelven a leer con Reindexar.</summary>
    public async Task<ServiceResult<SourceView>> UpdateAsync(Guid botId, Guid sourceId, UpdateSourceRequest request, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.BotId == botId, ct);
        if (source is null)
        {
            return ServiceResult<SourceView>.Fail("not_found", "Fuente no encontrada.");
        }

        var p = source.Kind switch
        {
            KnowledgeKind.Faq => PrepareFaq(request.Title, request.Faq),
            KnowledgeKind.Products => PrepareProducts(request.Title, request.Products),
            KnowledgeKind.Text or KnowledgeKind.Pdf => PrepareText(request.Title, request.Text),
            _ => new Checked(null, ServiceResult<SourceView>.Fail("invalid_state", "Esta fuente no se edita a mano: usa Reindexar para volver a leerla.")),
        };
        if (p.Error is not null)
        {
            return p.Error;
        }

        var prepared = p.Value!;
        if (prepared.Chunks.Count == 0 || prepared.Chunks.Count > MaxChunksPerSource)
        {
            return Invalid("text", prepared.Chunks.Count == 0 ? "No hay texto que guardar." : "Esta fuente es demasiado grande: divídela en varias.");
        }

        db.KnowledgeChunks.RemoveRange(await db.KnowledgeChunks.Where(c => c.SourceId == sourceId).ToListAsync(ct));
        source.Title = prepared.Title;
        source.Content = prepared.Content;
        source.Status = KnowledgeStatus.Indexed;
        source.Error = null;
        AddChunks(source.ClientId, botId, source.Id, prepared.Chunks);
        await db.SaveChangesAsync(ct);
        return ServiceResult<SourceView>.Ok(ToView(source, prepared.Chunks.Count));
    }

    public async Task<bool> DeleteAsync(Guid botId, Guid sourceId, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.BotId == botId, ct);
        if (source is null)
        {
            return false;
        }

        db.KnowledgeChunks.RemoveRange(await db.KnowledgeChunks.Where(c => c.SourceId == sourceId).ToListAsync(ct));
        db.KnowledgeSources.Remove(source);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Vuelve a descargar una página: quita lo indexado y la deja pendiente para el worker.</summary>
    public async Task<ServiceResult<SourceView>> ReindexAsync(Guid botId, Guid sourceId, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.BotId == botId, ct);
        if (source is null)
        {
            return ServiceResult<SourceView>.Fail("not_found", "Fuente no encontrada.");
        }

        if (source.Kind is not (KnowledgeKind.Url or KnowledgeKind.Supabase))
        {
            return ServiceResult<SourceView>.Fail("invalid_state", "Solo las páginas web y los catálogos se pueden volver a leer.");
        }

        // Un catálogo conserva lo que ya sabe el bot hasta que la nueva lectura funcione.
        if (source.Kind == KnowledgeKind.Url)
        {
            db.KnowledgeChunks.RemoveRange(await db.KnowledgeChunks.Where(c => c.SourceId == sourceId).ToListAsync(ct));
        }

        source.Status = KnowledgeStatus.Pending;
        source.Error = null;
        await db.SaveChangesAsync(ct);
        return ServiceResult<SourceView>.Ok(ToView(source, 0));
    }

    /// <summary>Prueba de búsqueda del panel: qué fragmentos encontraría el bot para esta pregunta.</summary>
    public async Task<ServiceResult<IReadOnlyList<KnowledgeHit>>> TestSearchAsync(Guid botId, string? query, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<IReadOnlyList<KnowledgeHit>>.Fail("not_found", "Chatbot no encontrado.");
        }

        return ServiceResult<IReadOnlyList<KnowledgeHit>>.Ok(await search.SearchAsync(botId, query ?? "", 5, ct));
    }

    /// <summary>
    /// Para el worker (contexto de sistema): procesa una fuente de URL pendiente. Devuelve false si no había ninguna.
    /// Cualquier fallo de la descarga deja la fuente en <c>Failed</c> con un mensaje, sin tirar el worker.
    /// </summary>
    public async Task<bool> IndexNextPendingUrlAsync(IWebPageFetcher fetcher, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.Kind == KnowledgeKind.Url && s.Status == KnowledgeStatus.Pending)
            .OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (source is null)
        {
            return false;
        }

        try
        {
            var page = await fetcher.FetchAsync(new Uri(source.SourceUrl!), ct);
            var chunks = page.Text.Length > MaxTextChars ? Chunker.Split(page.Text[..MaxTextChars]) : Chunker.Split(page.Text);
            if (chunks.Count == 0)
            {
                Fail(source, "La página no tiene texto que se pueda leer.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(page.Title) && source.Title == new Uri(source.SourceUrl!).Host)
                {
                    source.Title = page.Title.Trim()[..Math.Min(page.Title.Trim().Length, 300)];
                }

                AddChunks(source.ClientId, source.BotId, source.Id, chunks);
                source.Status = KnowledgeStatus.Indexed;
                source.Error = null;
            }
        }
        catch (InvalidOperationException ex)
        {
            Fail(source, ex.Message);
        }
        catch (HttpRequestException)
        {
            Fail(source, "No se pudo descargar la página.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(source, "La página tardó demasiado en responder.");
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Para el worker: deja pendientes los catálogos de Supabase cuyo plazo de actualización venció.</summary>
    public async Task QueueDueCatalogRefreshesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.KnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.Kind == KnowledgeKind.Supabase && s.RefreshMinutes != null && s.Status != KnowledgeStatus.Pending
                && (s.SyncedAt == null || s.SyncedAt.Value.AddMinutes(s.RefreshMinutes.Value) <= now))
            .ToListAsync(ct);
        foreach (var s in due)
        {
            s.Status = KnowledgeStatus.Pending;
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Para el worker (contexto de sistema): lee un catálogo de Supabase pendiente. Si la lectura falla, la fuente queda en
    /// <c>Failed</c> con el motivo y se conserva lo que ya sabía el bot; si funciona, se reemplaza todo de una vez.
    /// </summary>
    public async Task<bool> IndexNextPendingCatalogAsync(ICatalogFetcher fetcher, CancellationToken ct)
    {
        var source = await db.KnowledgeSources.IgnoreQueryFilters()
            .Where(s => s.Kind == KnowledgeKind.Supabase && s.Status == KnowledgeStatus.Pending)
            .OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (source is null)
        {
            return false;
        }

        try
        {
            // Con columnas elegidas también se piden las del enlace y la foto, aunque no se muestren como datos.
            var chosen = source.SourceColumns?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var select = chosen is null
                ? null
                : string.Join(',', chosen.Concat(CatalogText.LinkColumns(source.LinkTemplate)).Concat(source.ImageColumn is null ? [] : [source.ImageColumn]).Distinct(StringComparer.OrdinalIgnoreCase));
            var json = await fetcher.FetchAsync(new Uri(source.SourceUrl!), source.SourceTable!, select, secrets.Unprotect(source.SecretEncrypted!), ct);
            var text = CatalogText.Render(source.Title, json, chosen, source.LinkTemplate, source.ImageColumn);
            var chunks = text.Length > MaxTextChars ? [] : Chunker.Split(text);
            if (chunks.Count == 0 || chunks.Count > MaxChunksPerSource)
            {
                Fail(source, "El catálogo es demasiado grande: elige menos columnas o usa una vista más corta.");
            }
            else
            {
                db.KnowledgeChunks.RemoveRange(await db.KnowledgeChunks.IgnoreQueryFilters().Where(c => c.SourceId == source.Id).ToListAsync(ct));
                AddChunks(source.ClientId, source.BotId, source.Id, chunks);
                source.Status = KnowledgeStatus.Indexed;
                source.Error = null;
                source.SyncedAt = clock.GetUtcNow();
            }
        }
        catch (InvalidOperationException ex)
        {
            Fail(source, ex.Message);
        }
        catch (HttpRequestException)
        {
            Fail(source, "No se pudo conectar con Supabase.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(source, "Supabase tardó demasiado en responder.");
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ServiceResult<SourceView>> StoreAsync(Guid botId, KnowledgeKind kind, string title, string? url, IReadOnlyList<string> chunks, CancellationToken ct, string? content = null)
    {
        var guard = await CheckBotAndQuotaAsync(botId, ct);
        if (guard is not null)
        {
            return guard;
        }

        if (chunks.Count == 0)
        {
            return Invalid("text", "No hay texto que guardar.");
        }

        if (chunks.Count > MaxChunksPerSource)
        {
            return Invalid("text", "Esta fuente es demasiado grande: divídela en varias.");
        }

        var source = new KnowledgeSource
        {
            Id = Guid.CreateVersion7(),
            ClientId = await BotClientAsync(botId, ct),
            BotId = botId,
            Kind = kind,
            Title = title,
            SourceUrl = url,
            Content = content,
            Status = KnowledgeStatus.Indexed,
            CreatedAt = clock.GetUtcNow(),
        };
        db.KnowledgeSources.Add(source);
        AddChunks(source.ClientId, botId, source.Id, chunks);
        await db.SaveChangesAsync(ct);
        return ServiceResult<SourceView>.Ok(ToView(source, chunks.Count));
    }

    private async Task<ServiceResult<SourceView>?> CheckBotAndQuotaAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<SourceView>.Fail("not_found", "Chatbot no encontrado.");
        }

        return await db.KnowledgeSources.CountAsync(s => s.BotId == botId, ct) >= MaxSourcesPerBot
            ? ServiceResult<SourceView>.Fail("limit_reached", $"Un chatbot admite hasta {MaxSourcesPerBot} fuentes.")
            : null;
    }

    private void AddChunks(Guid tenantId, Guid botId, Guid sourceId, IReadOnlyList<string> chunks)
    {
        foreach (var text in chunks.Take(MaxChunksPerSource))
        {
            db.KnowledgeChunks.Add(new KnowledgeChunk { Id = Guid.CreateVersion7(), ClientId = tenantId, BotId = botId, SourceId = sourceId, Text = text });
        }
    }

    private static void Fail(KnowledgeSource source, string message)
    {
        source.Status = KnowledgeStatus.Failed;
        source.Error = message.Length > 500 ? message[..500] : message;
    }

    private static ServiceResult<SourceView> Invalid(string field, string message) =>
        ServiceResult<SourceView>.Fail("validation_error", message, new Dictionary<string, string> { [field] = message });

    private static SourceView ToView(KnowledgeSource s, int chunks) =>
        new(s.Id, s.Kind.ToString().ToLowerInvariant(), s.Title, s.SourceUrl, s.Status.ToString().ToLowerInvariant(), s.Error, chunks, s.CreatedAt,
            s.SourceTable, s.SourceColumns, s.RefreshMinutes, s.SyncedAt, s.LinkTemplate, s.ImageColumn);

    /// <summary>Cliente dueño del bot (ya comprobado con el filtro): lo nuevo hereda su cliente, también si quien crea es la agencia.</summary>
    private Task<Guid> BotClientAsync(Guid botId, CancellationToken ct) =>
        db.Bots.Where(b => b.Id == botId).Select(b => b.ClientId).FirstAsync(ct);
}
