using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Application.Http;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pixela.Application.Chat;

public sealed record McpTool(string Name, string? Description, JsonNode? InputSchema);

/// <summary>El servidor MCP no respondió como se esperaba. El mensaje es apto para mostrarlo al usuario del panel.</summary>
public sealed class McpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Cliente del protocolo MCP (transporte HTTP). Solo conecta a destinos públicos y limita tiempo y tamaño.</summary>
public interface IMcpClient
{
    /// <exception cref="McpException">No se pudo hablar con el servidor.</exception>
    Task<IReadOnlyList<McpTool>> ListToolsAsync(Uri url, string? token, CancellationToken ct);

    /// <summary>Ejecuta una herramienta y devuelve su resultado en texto.</summary>
    /// <exception cref="McpException">No se pudo hablar con el servidor o la herramienta falló.</exception>
    Task<string> CallToolAsync(Uri url, string? token, string tool, JsonObject arguments, CancellationToken ct);
}

public sealed record CreateConnectionRequest(string? Name, string? Url, string? Token);

public sealed record UpdateConnectionRequest(string? Name, string? Token, bool? IsActive, List<string>? AllowedTools);

public sealed record ConnectionView(Guid Id, string Name, string Url, bool IsActive, bool HasToken, IReadOnlyList<string> AllowedTools, string? LastError, DateTimeOffset CreatedAt);

public sealed record ConnectionToolView(string Name, string? Description, bool Allowed);

/// <summary>Las herramientas que un chatbot puede usar en esta conversación y cómo ejecutarlas.</summary>
public sealed record ToolSet(IReadOnlyList<AiTool> Tools, AiToolRunner Run);

/// <summary>
/// Conexiones de un chatbot a servidores MCP: alta con prueba de conexión, herramientas permitidas y el conjunto que usa el chat.
/// El token se guarda cifrado y no vuelve al panel; lo que devuelve una herramienta se entrega al modelo como dato, nunca como instrucción.
/// </summary>
public sealed partial class DataConnectionService(
    IAppDbContext db, ISecretProtector secrets, IMcpClient mcp, IOptions<OutboundHttpOptions> outboundOptions, TimeProvider clock)
{
    public const int MaxPerBot = 5;
    public const int MaxResultChars = 8_000;
    private static readonly TimeSpan ToolsCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    [GeneratedRegex(@"[^A-Za-z0-9_-]")]
    private static partial Regex NotToolChar();

    private static readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<McpTool> Tools)> ToolsCache = new();

    public async Task<ServiceResult<IReadOnlyList<ConnectionView>>> ListAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<IReadOnlyList<ConnectionView>>.Fail("not_found", "Chatbot no encontrado.");
        }

        var rows = await db.DataConnections.AsNoTracking().Where(c => c.BotId == botId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return ServiceResult<IReadOnlyList<ConnectionView>>.Ok(rows.Select(ToView).ToList());
    }

    public async Task<ServiceResult<ConnectionView>> CreateAsync(Guid botId, CreateConnectionRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? "").Trim();
        var url = (request.Url ?? "").Trim();
        var token = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
        var fields = new Dictionary<string, string>();
        if (name.Length is 0 or > 100)
        {
            fields["name"] = "El nombre es obligatorio (máximo 100 caracteres).";
        }

        if (OutboundUrlValidator.Validate(url, outboundOptions.Value) is { } urlError)
        {
            fields["url"] = urlError;
        }

        if (token is { Length: > 2000 })
        {
            fields["token"] = "El token es demasiado largo.";
        }

        if (fields.Count > 0)
        {
            return ServiceResult<ConnectionView>.Fail("validation_error", "Revisa los datos de la conexión.", fields);
        }

        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<ConnectionView>.Fail("not_found", "Chatbot no encontrado.");
        }

        if (await db.DataConnections.CountAsync(c => c.BotId == botId, ct) >= MaxPerBot)
        {
            return ServiceResult<ConnectionView>.Fail("limit_reached", $"Un chatbot admite hasta {MaxPerBot} conexiones de datos.");
        }

        // Se prueba antes de guardar: si no responde, el usuario se entera en el momento y con el motivo.
        try
        {
            await mcp.ListToolsAsync(new Uri(url), token, ct);
        }
        catch (McpException ex)
        {
            return ServiceResult<ConnectionView>.Fail("validation_error", ex.Message, new Dictionary<string, string> { ["url"] = ex.Message });
        }

        var connection = new DataConnection
        {
            Id = Guid.CreateVersion7(),
            ClientId = await BotClientAsync(botId, ct),
            BotId = botId,
            Name = name,
            Url = url,
            SecretEncrypted = token is null ? null : secrets.Protect(token),
            CreatedAt = clock.GetUtcNow(),
        };
        db.DataConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        return ServiceResult<ConnectionView>.Ok(ToView(connection));
    }

    /// <summary>Las herramientas que ofrece el servidor en este momento, marcando las que el chatbot puede usar.</summary>
    public async Task<ServiceResult<IReadOnlyList<ConnectionToolView>>> ToolsAsync(Guid botId, Guid id, CancellationToken ct)
    {
        var connection = await db.DataConnections.FirstOrDefaultAsync(c => c.Id == id && c.BotId == botId, ct);
        if (connection is null)
        {
            return ServiceResult<IReadOnlyList<ConnectionToolView>>.Fail("not_found", "Conexión no encontrada.");
        }

        try
        {
            var tools = await ListCachedAsync(connection, fresh: true, ct);
            var allowed = AllowedSet(connection);
            connection.LastError = null;
            await db.SaveChangesAsync(ct);
            return ServiceResult<IReadOnlyList<ConnectionToolView>>.Ok(tools.Select(t => new ConnectionToolView(t.Name, t.Description, allowed.Contains(t.Name))).ToList());
        }
        catch (McpException ex)
        {
            connection.LastError = Truncate(ex.Message);
            await db.SaveChangesAsync(ct);
            return ServiceResult<IReadOnlyList<ConnectionToolView>>.Fail("unavailable", ex.Message);
        }
    }

    public async Task<ServiceResult<ConnectionView>> UpdateAsync(Guid botId, Guid id, UpdateConnectionRequest request, CancellationToken ct)
    {
        var connection = await db.DataConnections.FirstOrDefaultAsync(c => c.Id == id && c.BotId == botId, ct);
        if (connection is null)
        {
            return ServiceResult<ConnectionView>.Fail("not_found", "Conexión no encontrada.");
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is 0 or > 100)
            {
                return ServiceResult<ConnectionView>.Fail("validation_error", "El nombre es obligatorio (máximo 100 caracteres).", new Dictionary<string, string> { ["name"] = "Máximo 100 caracteres." });
            }

            connection.Name = name;
        }

        if (!string.IsNullOrWhiteSpace(request.Token))
        {
            connection.SecretEncrypted = secrets.Protect(request.Token.Trim());
            ToolsCache.TryRemove(connection.Id, out _);
        }

        if (request.IsActive is { } active)
        {
            connection.IsActive = active;
        }

        if (request.AllowedTools is { } allowedTools)
        {
            var names = allowedTools.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (names.Any(n => n.Length > 128 || n.Contains(',')) || string.Join(',', names).Length > 2000)
            {
                return ServiceResult<ConnectionView>.Fail("validation_error", "Lista de herramientas no válida.", new Dictionary<string, string> { ["allowed_tools"] = "Lista no válida." });
            }

            connection.AllowedTools = string.Join(',', names);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<ConnectionView>.Ok(ToView(connection));
    }

    public async Task<bool> DeleteAsync(Guid botId, Guid id, CancellationToken ct)
    {
        var connection = await db.DataConnections.FirstOrDefaultAsync(c => c.Id == id && c.BotId == botId, ct);
        if (connection is null)
        {
            return false;
        }

        db.DataConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        ToolsCache.TryRemove(id, out _);
        return true;
    }

    /// <summary>
    /// Las herramientas permitidas de las conexiones activas del chatbot, listas para el modelo. Null si no hay ninguna (el chat sigue como siempre).
    /// Un servidor caído no rompe la conversación: se omite y se anota el error.
    /// </summary>
    public async Task<ToolSet?> ResolveForChatAsync(Guid botId, CancellationToken ct)
    {
        var connections = await db.DataConnections.Where(c => c.BotId == botId && c.IsActive && c.AllowedTools != "").ToListAsync(ct);
        if (connections.Count == 0)
        {
            return null;
        }

        var tools = new List<AiTool>();
        var routes = new Dictionary<string, (DataConnection Connection, string Tool)>(StringComparer.Ordinal);
        var dirty = false;
        foreach (var connection in connections)
        {
            IReadOnlyList<McpTool> listed;
            try
            {
                listed = await ListCachedAsync(connection, fresh: false, ct);
                if (connection.LastError is not null)
                {
                    connection.LastError = null;
                    dirty = true;
                }
            }
            catch (McpException ex)
            {
                connection.LastError = Truncate(ex.Message);
                dirty = true;
                continue;
            }

            var allowed = AllowedSet(connection);
            var prefix = connection.Id.ToString("N")[^6..];
            foreach (var tool in listed.Where(t => allowed.Contains(t.Name)))
            {
                var exposed = ExposedName(prefix, tool.Name);
                if (routes.TryAdd(exposed, (connection, tool.Name)))
                {
                    tools.Add(new AiTool(exposed, Truncate(tool.Description ?? tool.Name, 1000), tool.InputSchema));
                }
            }
        }

        if (dirty)
        {
            await db.SaveChangesAsync(ct);
        }

        if (tools.Count == 0)
        {
            return null;
        }

        async Task<string> Run(string exposedName, JsonObject input, CancellationToken token)
        {
            if (!routes.TryGetValue(exposedName, out var route))
            {
                throw new McpException("Esa herramienta no está disponible.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(CallTimeout);
            var secret = route.Connection.SecretEncrypted is null ? null : secrets.Unprotect(route.Connection.SecretEncrypted);
            var text = await mcp.CallToolAsync(new Uri(route.Connection.Url), secret, route.Tool, input, timeout.Token);
            return Truncate(text, MaxResultChars);
        }

        return new ToolSet(tools, Run);
    }

    internal static string ExposedName(string prefix, string tool)
    {
        var clean = NotToolChar().Replace(tool, "_");
        var name = $"{prefix}_{clean}";
        return name.Length <= 64 ? name : name[..64];
    }

    private async Task<IReadOnlyList<McpTool>> ListCachedAsync(DataConnection connection, bool fresh, CancellationToken ct)
    {
        if (!fresh && ToolsCache.TryGetValue(connection.Id, out var hit) && clock.GetUtcNow() - hit.At < ToolsCacheTtl)
        {
            return hit.Tools;
        }

        var secret = connection.SecretEncrypted is null ? null : secrets.Unprotect(connection.SecretEncrypted);
        var tools = await mcp.ListToolsAsync(new Uri(connection.Url), secret, ct);
        ToolsCache[connection.Id] = (clock.GetUtcNow(), tools);
        return tools;
    }

    private static HashSet<string> AllowedSet(DataConnection c) =>
        c.AllowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    private static string Truncate(string value, int max = 500) => value.Length <= max ? value : value[..max];

    private static ConnectionView ToView(DataConnection c) =>
        new(c.Id, c.Name, c.Url, c.IsActive, c.SecretEncrypted is not null, AllowedSet(c).Order(StringComparer.Ordinal).ToList(), c.LastError, c.CreatedAt);

    /// <summary>Cliente dueño del bot (ya comprobado con el filtro): lo nuevo hereda su cliente, también si quien crea es la agencia.</summary>
    private Task<Guid> BotClientAsync(Guid botId, CancellationToken ct) =>
        db.Bots.Where(b => b.Id == botId).Select(b => b.ClientId).FirstAsync(ct);
}
