using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

/// <param name="Channel">whatsapp, messenger o instagram; null si es el chat web.</param>
/// <param name="Contact">Nombre o id del contacto en el canal (dato personal que el negocio ya conoce); null en el chat web.</param>
/// <param name="HumanActive">Una persona del negocio atiende la conversación y el bot no responde.</param>
/// <param name="CanReply">El negocio todavía puede escribir al contacto: el chat web, o un canal activo, y el contacto escribió en las últimas 24 horas.</param>
/// <param name="BotId">Solo en la bandeja única: a qué chatbot pertenece la conversación.</param>
public sealed record ConversationSummary(
    Guid Id, string? FirstMessage, int Messages, DateTimeOffset StartedAt, DateTimeOffset LastMessageAt, string? Channel = null, string? Contact = null, bool NeedsAttention = false,
    bool HumanActive = false, bool CanReply = false, Guid? BotId = null, string? BotName = null);

public sealed record AgentReplyRequest(string? Text);

/// <param name="Available">El archivo todavía está guardado y se puede abrir; si no, ya venció o no se pudo descargar.</param>
public sealed record TranscriptAttachment(Guid Id, string Kind, string? MimeType, string? FileName, long SizeBytes, bool Available);

public sealed record TranscriptMessage(string Role, string Content, DateTimeOffset CreatedAt, IReadOnlyList<TranscriptAttachment>? Attachments = null);

public sealed record AttachmentFile(Stream Content, string MimeType, string? FileName);

public sealed record LeadView(Guid Id, Guid? ConversationId, string Name, string? Email, string? Phone, string? Interest, DateTimeOffset CreatedAt);

/// <summary>Lo que el panel ve de un chatbot: conversaciones (solo lectura, con borrado) y datos de contacto dejados por los visitantes.</summary>
public sealed class ConversationService(IAppDbContext db, IMetaGraph graph, ISecretProtector secrets, IAttachmentStore attachmentStore, TimeProvider clock)
{
    public const int PageSize = 50;
    public const int MaxAgentReplyChars = 2000;

    public async Task<ServiceResult<IReadOnlyList<ConversationSummary>>> ListConversationsAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<IReadOnlyList<ConversationSummary>>.Fail("not_found", "Chatbot no encontrado.");
        }

        var rows = await db.Conversations.AsNoTracking().Where(c => c.BotId == botId)
            .OrderByDescending(c => c.LastMessageAt).Take(PageSize).ToListAsync(ct);
        return ServiceResult<IReadOnlyList<ConversationSummary>>.Ok(await SummarizeAsync(rows, null, ct));
    }

    public const int InboxSize = 100;

    /// <summary>
    /// Bandeja única: las conversaciones de todos los chatbots del negocio (web, WhatsApp, Messenger e Instagram) en una sola lista, con las que
    /// esperan a una persona primero. <paramref name="onlyAttention"/> deja solo las que esperan o ya atiende una persona.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListInboxAsync(bool onlyAttention, CancellationToken ct)
    {
        var query = db.Conversations.AsNoTracking().AsQueryable();
        if (onlyAttention)
        {
            query = query.Where(c => c.NeedsAttention || c.HumanActive);
        }

        var rows = await query.OrderByDescending(c => c.NeedsAttention).ThenByDescending(c => c.LastMessageAt).Take(InboxSize).ToListAsync(ct);
        var botIds = rows.Select(c => c.BotId).Distinct().ToList();
        var names = await db.Bots.AsNoTracking().Where(b => botIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        return await SummarizeAsync(rows, names, ct);
    }

    private async Task<IReadOnlyList<ConversationSummary>> SummarizeAsync(List<Conversation> rows, IReadOnlyDictionary<Guid, string>? botNames, CancellationToken ct)
    {
        var ids = rows.Select(c => c.Id).ToList();
        var firsts = await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.ConversationId) && m.Role == "user")
            .OrderBy(m => m.CreatedAt).Select(m => new { m.ConversationId, m.Content }).ToListAsync(ct);
        var firstByConversation = firsts.GroupBy(m => m.ConversationId).ToDictionary(g => g.Key, g => g.First().Content);

        var channelIds = rows.Where(c => c.ChannelId != null).Select(c => c.ChannelId!.Value).Distinct().ToList();
        var channels = await db.Channels.AsNoTracking().Where(ch => channelIds.Contains(ch.Id)).ToDictionaryAsync(ch => ch.Id, ch => ch, ct);

        // Última vez que escribió el contacto: de ahí corre la ventana de 24 horas en la que Meta deja contestarle.
        var lastUser = await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.ConversationId) && m.Role == "user")
            .GroupBy(m => m.ConversationId).Select(g => new { Id = g.Key, At = g.Max(m => m.CreatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
        var now = clock.GetUtcNow();

        return rows
            .Select(c =>
            {
                var channel = c.ChannelId is { } chId ? channels.GetValueOrDefault(chId) : null;
                // Chat web (sin canal): el visitante puede seguir en la página; con canal, Meta exige que sea el canal activo y dentro de las 24 horas.
                var canReply = (c.ChannelId is null || channel is { Status: ChannelStatus.Active }) && lastUser.TryGetValue(c.Id, out var at) && now - at < ChatService.ConversationWindow;
                return new ConversationSummary(
                    c.Id, firstByConversation.GetValueOrDefault(c.Id) is { } f ? Snippet(f) : null, c.MessageCount, c.CreatedAt, c.LastMessageAt,
                    channel?.Kind.ToString().ToLowerInvariant(),
                    c.ContactId is null ? null : (string.IsNullOrWhiteSpace(c.ContactName) ? c.ContactId : $"{c.ContactName} ({c.ContactId})"),
                    c.NeedsAttention, c.HumanActive, canReply, botNames is null ? null : c.BotId, botNames?.GetValueOrDefault(c.BotId));
            })
            .ToList();
    }

    /// <summary>La persona del negocio toma la conversación: el bot deja de responderle al contacto hasta que se la devuelvan.</summary>
    public async Task<ServiceResult<bool>> TakeOverAsync(Guid botId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.BotId == botId, ct);
        if (conversation is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Conversación no encontrada.");
        }

        conversation.HumanActive = true;
        conversation.HumanActivityAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Devuelve la conversación al bot: vuelve a responder desde el siguiente mensaje del contacto.</summary>
    public async Task<ServiceResult<bool>> ReleaseAsync(Guid botId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.BotId == botId, ct);
        if (conversation is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Conversación no encontrada.");
        }

        conversation.HumanActive = false;
        conversation.HumanActivityAt = null;
        conversation.NeedsAttention = false;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Una persona del negocio le escribe al contacto por el mismo canal. Solo se puede dentro de las 24 horas posteriores al último mensaje del contacto
    /// (lo que Meta permite). Responder equivale a tomar la conversación: el bot calla hasta que se la devuelvan.
    /// </summary>
    public async Task<ServiceResult<bool>> ReplyAsync(Guid botId, Guid conversationId, AgentReplyRequest request, CancellationToken ct)
    {
        var text = (request.Text ?? "").Trim();
        if (text.Length == 0 || text.Length > MaxAgentReplyChars)
        {
            return ServiceResult<bool>.Fail("validation_error", $"Escribe un mensaje de hasta {MaxAgentReplyChars} caracteres.");
        }

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.BotId == botId, ct);
        if (conversation is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Conversación no encontrada.");
        }

        if (conversation.ChannelId is null)
        {
            // Chat web: no hay a quién enviarle nada; el widget abierto consulta y muestra el mensaje del equipo.
            var sentAt = clock.GetUtcNow();
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.CreateVersion7(),
                ClientId = conversation.ClientId,
                ConversationId = conversation.Id,
                Role = "agent",
                Content = text,
                CreatedAt = sentAt,
            });
            conversation.HumanActive = true;
            conversation.HumanActivityAt = sentAt;
            conversation.NeedsAttention = false;
            conversation.LastMessageAt = sentAt;
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == conversation.ChannelId, ct);
        if (channel is null || conversation.ContactId is null)
        {
            return ServiceResult<bool>.Fail("validation_error", "El canal de esta conversación ya no existe.");
        }

        if (channel.Status != ChannelStatus.Active)
        {
            return ServiceResult<bool>.Fail("invalid_state", channel.Status == ChannelStatus.TokenError ? "Meta rechazó el token del canal: cámbialo en la pestaña Canales." : "El canal está desactivado.");
        }

        var now = clock.GetUtcNow();
        var lastUser = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId && m.Role == "user")
            .MaxAsync(m => (DateTimeOffset?)m.CreatedAt, ct);
        if (lastUser is null || now - lastUser.Value >= ChatService.ConversationWindow)
        {
            return ServiceResult<bool>.Fail("invalid_state", "Pasaron más de 24 horas desde el último mensaje del contacto: Meta ya no permite escribirle desde aquí. Cuando él escriba de nuevo, podrás responder.");
        }

        string token;
        try
        {
            token = secrets.Unprotect(channel.AccessTokenEncrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            channel.Status = ChannelStatus.TokenError;
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Fail("invalid_state", "El token del canal no se pudo leer: cámbialo en la pestaña Canales.");
        }

        try
        {
            foreach (var part in ChannelText.Split(text, ChannelText.MaxLength(channel.Kind)))
            {
                await graph.SendTextAsync(channel.Kind, channel.ExternalId, token, conversation.ContactId, part, ct);
                if (channel.Kind == ChannelKind.WhatsApp)
                {
                    await db.RecordWhatsAppSentAsync(conversation.BotId, conversation.ClientId, UsagePeriod.Of(now), 1, now, ct);
                }
            }
        }
        catch (MetaSendException ex) when (ex.TokenRejected)
        {
            channel.Status = ChannelStatus.TokenError;
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Fail("invalid_state", "Meta rechazó el token del canal: cámbialo en la pestaña Canales.");
        }
        catch (MetaSendException)
        {
            return ServiceResult<bool>.Fail("unavailable", "Meta no aceptó el mensaje. Intenta de nuevo en unos minutos.");
        }

        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.CreateVersion7(),
            ClientId = conversation.ClientId,
            ConversationId = conversation.Id,
            Role = "agent",
            Content = text.Length > 4000 ? text[..4000] : text,
            CreatedAt = now,
        });
        conversation.HumanActive = true;
        conversation.HumanActivityAt = now;
        conversation.NeedsAttention = false;
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<IReadOnlyList<TranscriptMessage>>> GetTranscriptAsync(Guid botId, Guid conversationId, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId && c.BotId == botId, ct))
        {
            return ServiceResult<IReadOnlyList<TranscriptMessage>>.Fail("not_found", "Conversación no encontrada.");
        }

        var rows = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt).Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt }).ToListAsync(ct);
        var files = (await db.ChatAttachments.AsNoTracking().Where(a => a.ConversationId == conversationId && a.MessageId != null).OrderBy(a => a.CreatedAt).ToListAsync(ct))
            .GroupBy(a => a.MessageId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var now = clock.GetUtcNow();
        IReadOnlyList<TranscriptMessage> messages = rows
            .Select(m => new TranscriptMessage(m.Role, m.Content, m.CreatedAt, files.TryGetValue(m.Id, out var list)
                ? list.Select(a => new TranscriptAttachment(a.Id, a.Kind, a.MimeType, a.FileName, a.SizeBytes, a.Stored && a.ExpiresAt > now)).ToList()
                : null))
            .ToList();
        return ServiceResult<IReadOnlyList<TranscriptMessage>>.Ok(messages);
    }

    /// <summary>Abre un archivo que envió el contacto. <c>gone</c> si ya venció o no se pudo descargar.</summary>
    public async Task<ServiceResult<AttachmentFile>> OpenAttachmentAsync(Guid botId, Guid conversationId, Guid attachmentId, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId && c.BotId == botId, ct))
        {
            return ServiceResult<AttachmentFile>.Fail("not_found", "Conversación no encontrada.");
        }

        var a = await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.ConversationId == conversationId, ct);
        if (a is null)
        {
            return ServiceResult<AttachmentFile>.Fail("not_found", "Archivo no encontrado.");
        }

        if (!a.Stored || a.ExpiresAt <= clock.GetUtcNow() || attachmentStore.OpenRead(a.ClientId, a.Id) is not { } stream)
        {
            return ServiceResult<AttachmentFile>.Fail("gone", "El archivo ya no está disponible: se conserva pocos días.");
        }

        return ServiceResult<AttachmentFile>.Ok(new AttachmentFile(stream, a.MimeType ?? "application/octet-stream", a.FileName));
    }

    /// <summary>Borra la conversación y sus mensajes (derecho de supresión). Los contactos dejados se conservan, sin enlace a la conversación.</summary>
    public async Task<bool> DeleteConversationAsync(Guid botId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.BotId == botId, ct);
        if (conversation is null)
        {
            return false;
        }

        var attachments = await db.ChatAttachments.Where(a => a.ConversationId == conversationId).ToListAsync(ct);
        foreach (var a in attachments)
        {
            attachmentStore.Delete(a.ClientId, a.Id);
        }

        db.ChatAttachments.RemoveRange(attachments);
        db.ChatMessages.RemoveRange(await db.ChatMessages.Where(m => m.ConversationId == conversationId).ToListAsync(ct));
        foreach (var lead in await db.Leads.Where(l => l.ConversationId == conversationId).ToListAsync(ct))
        {
            lead.ConversationId = null;
        }

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ServiceResult<IReadOnlyList<LeadView>>> ListLeadsAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<IReadOnlyList<LeadView>>.Fail("not_found", "Chatbot no encontrado.");
        }

        var leads = await db.Leads.AsNoTracking().Where(l => l.BotId == botId).OrderByDescending(l => l.CreatedAt).Take(200)
            .Select(l => new LeadView(l.Id, l.ConversationId, l.Name, l.Email, l.Phone, l.Interest, l.CreatedAt)).ToListAsync(ct);
        return ServiceResult<IReadOnlyList<LeadView>>.Ok(leads);
    }

    public async Task<bool> DeleteLeadAsync(Guid botId, Guid leadId, CancellationToken ct)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId && l.BotId == botId, ct);
        if (lead is null)
        {
            return false;
        }

        db.Leads.Remove(lead);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Snippet(string text) => text.Length <= 120 ? text : text[..120] + "…";
}
