using Microsoft.AspNetCore.Mvc;
using Pixela.Api.Auth;
using Pixela.Application.Chat;
using Pixela.Application.Clients;
using Pixela.Application.Common;

namespace Pixela.Api.Endpoints;

public sealed record CreateBotRequest(Guid? AssetId, BotRequest? Bot);

/// <summary>
/// Chatbots en el panel (bajo <c>/panel</c>, con su cabecera anti-CSRF). Permisos:
/// <list type="bullet">
/// <item>Agencia: crear, configurar, borrar y mover bots; llave de Claude, app de Meta, canales y conexiones.</item>
/// <item>Administrador del cliente: ver sus bots, alimentar el conocimiento, contactos, uso y respuestas rápidas.</item>
/// <item>Cualquiera (también el operador): la bandeja.</item>
/// </list>
/// </summary>
public static class BotEndpoints
{
    public static void MapBots(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/panel/bots").RequireAuthorization(Policies.ClientAdmin);
        var agency = app.MapGroup("/panel/bots").RequireAuthorization(Policies.Agency);
        var attend = app.MapGroup("/panel/bots").RequireAuthorization(Policies.Session);

        admin.MapGet("/", async (Guid? assetId, BotService s, CancellationToken ct) =>
        {
            var list = await s.ListAsync(assetId, ct);
            return Results.Ok(new { bots = list.Bots, needing_attention = list.NeedingAttention });
        });
        admin.MapGet("/{id:guid}", async (Guid id, BotService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.GetAsync(id, ct), b => new { bot = b }));
        agency.MapPost("/", async (CreateBotRequest r, BotService s, CancellationToken ct) =>
            r.AssetId is not { } assetId
                ? PanelEndpoints.ToResult(ServiceResult<BotView>.Fail("validation_error", "Elige el asset del chatbot.", new Dictionary<string, string> { ["asset_id"] = "Requerido." }), _ => new { })
                : PanelEndpoints.ToResult(await s.CreateAsync(assetId, r.Bot ?? new BotRequest(null, null, null, null, null, null, null, null), ct), b => new { bot = b }, StatusCodes.Status201Created));
        agency.MapPatch("/{id:guid}", async (Guid id, BotRequest r, BotService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.UpdateAsync(id, r, ct), b => new { bot = b }));
        agency.MapDelete("/{id:guid}", async (Guid id, BotService s, CancellationToken ct) =>
            await s.DeleteAsync(id, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Chatbot no encontrado."));
        agency.MapPost("/{id:guid}/move", async (Guid id, MoveBotRequest r, ClientService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.MoveBotAsync(id, r.AssetId, ct), _ => new { ok = true }));
        agency.MapPut("/{id:guid}/icon", async (Guid id, IFormFile file, BotService s, CancellationToken ct) =>
                PanelEndpoints.ToResult(await s.SetIconAsync(id, await PanelEndpoints.ReadImageAsync(file, ct), ct), _ => new { ok = true }))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ImageUpload.MaxBytes + (64 * 1024)));
        agency.MapDelete("/{id:guid}/icon", async (Guid id, BotService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.RemoveIconAsync(id, ct), _ => new { ok = true }));
        attend.MapGet("/{id:guid}/icon", async (Guid id, BotService s, HttpContext http, CancellationToken ct) => PanelEndpoints.ServeImage(http, await s.GetIconAsync(id, ct)));

        MapIntegrations(agency);
        MapKnowledge(admin);
        MapInbox(admin, attend);
    }

    /// <summary>Llave de Claude y app de Meta del bot: los secretos nunca se devuelven, solo sus últimos caracteres.</summary>
    private static void MapIntegrations(RouteGroupBuilder bots)
    {
        bots.MapGet("/{id:guid}/claude-key", async (Guid id, BotKeyService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.GetAsync(id, ct), k => new { key = k }));
        bots.MapPut("/{id:guid}/claude-key", async (Guid id, SetAiKeyRequest r, BotKeyService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.SetAsync(id, r, ct), k => new { key = k }));
        bots.MapDelete("/{id:guid}/claude-key", async (Guid id, BotKeyService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.ClearAsync(id, ct), k => new { key = k }));
        bots.MapPost("/{id:guid}/claude-key/test", async (Guid id, TestAiKeyRequest r, BotKeyService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.TestAsync(id, r, ct), c => new { result = c.ToString().ToLowerInvariant() }));

        bots.MapGet("/{id:guid}/meta-app", async (Guid id, BotMetaAppService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.GetAsync(id, ct), a => new { meta_app = a }));
        bots.MapPut("/{id:guid}/meta-app/secret", async (Guid id, SetMetaAppSecretRequest r, BotMetaAppService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.SetAppSecretAsync(id, r, ct), a => new { meta_app = a }));
        bots.MapDelete("/{id:guid}/meta-app/secret", async (Guid id, BotMetaAppService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ClearAppSecretAsync(id, ct), a => new { meta_app = a }));
        bots.MapPost("/{id:guid}/meta-app/verify-token", async (Guid id, BotMetaAppService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.RegenerateVerifyTokenAsync(id, ct), a => new { meta_app = a }));

        bots.MapGet("/{id:guid}/channels", async (Guid id, ChannelService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.ListAsync(id, ct), l => new { channels = l.Channels }));
        bots.MapPost("/{id:guid}/channels", async (Guid id, ChannelRequest r, ChannelService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.CreateAsync(id, r, ct), c => new { channel = c }, StatusCodes.Status201Created));
        bots.MapPatch("/{id:guid}/channels/{channelId:guid}", async (Guid id, Guid channelId, ChannelRequest r, ChannelService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAsync(id, channelId, r, ct), c => new { channel = c }));
        bots.MapDelete("/{id:guid}/channels/{channelId:guid}", async (Guid id, Guid channelId, ChannelService s, CancellationToken ct) =>
            await s.DeleteAsync(id, channelId, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Canal no encontrado."));

        var connections = bots.MapGroup("/{id:guid}/connections");
        connections.MapGet("/", async (Guid id, DataConnectionService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.ListAsync(id, ct), list => new { connections = list }));
        connections.MapPost("/", async (Guid id, CreateConnectionRequest r, DataConnectionService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.CreateAsync(id, r, ct), v => new { connection = v }, StatusCodes.Status201Created));
        connections.MapGet("/{connectionId:guid}/tools", async (Guid id, Guid connectionId, DataConnectionService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ToolsAsync(id, connectionId, ct), list => new { tools = list }));
        connections.MapPatch("/{connectionId:guid}", async (Guid id, Guid connectionId, UpdateConnectionRequest r, DataConnectionService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAsync(id, connectionId, r, ct), v => new { connection = v }));
        connections.MapDelete("/{connectionId:guid}", async (Guid id, Guid connectionId, DataConnectionService s, CancellationToken ct) =>
            await s.DeleteAsync(id, connectionId, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Conexión no encontrada."));
    }

    /// <summary>Información que el bot usa para responder: FAQ, productos, texto, PDF, páginas web y catálogos de Supabase.</summary>
    private static void MapKnowledge(RouteGroupBuilder bots)
    {
        var sources = bots.MapGroup("/{id:guid}/sources");
        sources.MapGet("/", async (Guid id, KnowledgeService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.ListAsync(id, ct), list => new { sources = list }));
        sources.MapPost("/faq", async (Guid id, AddFaqRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.AddFaqAsync(id, r, ct), v => new { source = v }, StatusCodes.Status201Created));
        sources.MapPost("/products", async (Guid id, AddProductsRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.AddProductsAsync(id, r, ct), v => new { source = v }, StatusCodes.Status201Created));
        sources.MapPost("/text", async (Guid id, AddTextRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.AddTextAsync(id, r, ct), v => new { source = v }, StatusCodes.Status201Created));
        sources.MapPost("/url", async (Guid id, AddUrlRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.AddUrlAsync(id, r, ct), v => new { source = v }, StatusCodes.Status201Created));
        sources.MapPost("/supabase", async (Guid id, AddSupabaseRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.AddSupabaseAsync(id, r, ct), v => new { source = v }, StatusCodes.Status201Created));

        // El PDF viaja como formulario; la defensa CSRF es la cabecera obligatoria del panel, no el token de antiforgery.
        sources.MapPost("/pdf", async (Guid id, IFormFile file, [FromForm] string? title, KnowledgeService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct);
                copy.Position = 0;
                return PanelEndpoints.ToResult(await s.AddPdfAsync(id, title, file.FileName, copy, copy.Length, ct), v => new { source = v }, StatusCodes.Status201Created);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(KnowledgeService.MaxPdfBytes + (1024 * 1024)));

        sources.MapGet("/{sourceId:guid}/content", async (Guid id, Guid sourceId, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.GetContentAsync(id, sourceId, ct), c => new { source = c }));
        sources.MapPut("/{sourceId:guid}", async (Guid id, Guid sourceId, UpdateSourceRequest r, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAsync(id, sourceId, r, ct), v => new { source = v }));
        sources.MapDelete("/{sourceId:guid}", async (Guid id, Guid sourceId, KnowledgeService s, CancellationToken ct) =>
            await s.DeleteAsync(id, sourceId, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Fuente no encontrada."));
        sources.MapPost("/{sourceId:guid}/reindex", async (Guid id, Guid sourceId, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ReindexAsync(id, sourceId, ct), v => new { source = v }));

        bots.MapGet("/{id:guid}/search", async (Guid id, string? q, KnowledgeService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.TestSearchAsync(id, q, ct), hits => new { hits = hits.Select(h => new { text = h.Text, source = h.SourceTitle, rank = h.Rank }) }));
    }

    /// <summary>Conversaciones y contactos. Lo que atiende el operador va en <paramref name="attend"/>; borrar, contactos y uso, en <paramref name="admin"/>.</summary>
    private static void MapInbox(RouteGroupBuilder admin, RouteGroupBuilder attend)
    {
        admin.MapGet("/{id:guid}/usage", async (Guid id, BotUsageService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.GetAsync(id, ct), u => new { usage = u }));

        // Bandeja única: conversaciones de todos los chatbots visibles en una sola lista.
        attend.MapGet("/inbox", async (string? filter, ConversationService s, CancellationToken ct) =>
            Results.Ok(new { conversations = await s.ListInboxAsync(string.Equals(filter, "attention", StringComparison.OrdinalIgnoreCase), ct) }));
        attend.MapGet("/{id:guid}/conversations", async (Guid id, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ListConversationsAsync(id, ct), l => new { conversations = l }));
        attend.MapGet("/{id:guid}/conversations/{conversationId:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid conversationId, Guid attachmentId, ConversationService s, HttpContext http, CancellationToken ct) =>
        {
            var result = await s.OpenAttachmentAsync(id, conversationId, attachmentId, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.Error!.Code == "gone" ? StatusCodes.Status410Gone : StatusCodes.Status404NotFound);
            }

            // Contenido de terceros: nunca se ejecuta ni se interpreta como página. Solo las imágenes se muestran dentro; lo demás se descarga.
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            http.Response.Headers["Cache-Control"] = "private, no-store";
            var file = result.Value!;
            return AttachmentTypes.IsInline(file.MimeType)
                ? Results.Stream(file.Content, file.MimeType)
                : Results.Stream(file.Content, file.MimeType, fileDownloadName: file.FileName ?? "archivo");
        });
        attend.MapGet("/{id:guid}/conversations/{conversationId:guid}", async (Guid id, Guid conversationId, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.GetTranscriptAsync(id, conversationId, ct), m => new { messages = m }));
        attend.MapPost("/{id:guid}/conversations/{conversationId:guid}/takeover", async (Guid id, Guid conversationId, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.TakeOverAsync(id, conversationId, ct), _ => new { human_active = true }));
        attend.MapPost("/{id:guid}/conversations/{conversationId:guid}/release", async (Guid id, Guid conversationId, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ReleaseAsync(id, conversationId, ct), _ => new { human_active = false }));
        attend.MapPost("/{id:guid}/conversations/{conversationId:guid}/reply", async (Guid id, Guid conversationId, AgentReplyRequest r, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ReplyAsync(id, conversationId, r, ct), _ => new { sent = true }));

        // Respuestas rápidas: las ven y usan quienes atienden; las edita el administrador.
        attend.MapGet("/{id:guid}/quick-replies", async (Guid id, QuickReplyService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.GetAsync(id, ct), v => new { quick_replies = v }));
        attend.MapPost("/{id:guid}/conversations/{conversationId:guid}/quick-reply", async (Guid id, Guid conversationId, QuickReplyRequest r, HttpContext http, QuickReplyService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.SendAsync(PanelEndpoints.Principal(http).UserId, id, conversationId, r.Kind, ct), _ => new { sent = true }));
        admin.MapPut("/{id:guid}/quick-replies", async (Guid id, SetQuickRepliesRequest r, QuickReplyService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.SetAsync(id, r, ct), v => new { quick_replies = v }));

        admin.MapDelete("/{id:guid}/conversations/{conversationId:guid}", async (Guid id, Guid conversationId, ConversationService s, CancellationToken ct) =>
            await s.DeleteConversationAsync(id, conversationId, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Conversación no encontrada."));
        admin.MapGet("/{id:guid}/leads", async (Guid id, ConversationService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.ListLeadsAsync(id, ct), l => new { leads = l }));
        admin.MapDelete("/{id:guid}/leads/{leadId:guid}", async (Guid id, Guid leadId, ConversationService s, CancellationToken ct) =>
            await s.DeleteLeadAsync(id, leadId, ct) ? Results.NoContent() : PanelEndpoints.NotFound("Contacto no encontrado."));
    }
}
