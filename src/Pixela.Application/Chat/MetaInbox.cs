using Pixela.Application.Abstractions;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pixela.Application.Chat;

public enum InboundOutcome
{
    /// <summary>Se respondió.</summary>
    Done,

    /// <summary>Se descartó sin responder (canal desactivado, contacto que excede el tope, mensaje ya inútil).</summary>
    Dropped,

    /// <summary>Falló por algo pasajero: queda en cola para reintentar.</summary>
    Retry,

    /// <summary>Falló sin remedio (token rechazado, agotó los intentos).</summary>
    Failed,
}

/// <summary>Bot dueño de un webhook de Meta: su app (clave secreta, null si falta) y su token de verificación.</summary>
public sealed record MetaBotApp(Guid BotId, Guid ClientId, string? AppSecret, string VerifyToken);

/// <summary>Recibe los mensajes de un webhook de Meta y los deja en cola. No responde nada: eso lo hace <see cref="MetaInboxProcessor"/>.</summary>
public sealed class MetaInboxService(IAppDbContext db, ISecretProtector secrets, TimeProvider clock)
{
    /// <summary>
    /// Bot dueño de un webhook (<c>/meta/webhook/{clave pública}</c>) con su clave secreta de la app de Meta y su token de verificación, ya
    /// descifrados. Null si el bot no existe o no tiene app de Meta configurada. Cada bot trae su propia app: no hay secreto global.
    /// </summary>
    public async Task<MetaBotApp?> ResolveBotAppAsync(string? publicKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 64)
        {
            return null;
        }

        // El webhook es anónimo: el bot se busca por su clave pública, sin filtro, y de ahí sale el cliente de todo lo demás.
        var row = await db.Bots.IgnoreQueryFilters().AsNoTracking().Where(b => b.PublicKey == publicKey)
            .Join(db.BotSecrets.IgnoreQueryFilters(), b => b.Id, s => s.BotId, (b, s) => new { b.Id, b.ClientId, s.MetaAppSecretEncrypted, s.MetaVerifyTokenEncrypted })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        try
        {
            var appSecret = row.MetaAppSecretEncrypted is { Length: > 0 } a ? secrets.Unprotect(a) : null;
            return new MetaBotApp(row.Id, row.ClientId, appSecret, secrets.Unprotect(row.MetaVerifyTokenEncrypted));
        }
        catch (Exception)
        {
            return null; // secreto ilegible (otra llave maestra): el webhook no se acepta
        }
    }

    /// <summary>
    /// Guarda los mensajes que pertenecen a un canal activo de <paramref name="botId"/> y que no se habían recibido antes (un webhook firmado con
    /// la app de un bot no puede meter mensajes en los canales de otro). Devuelve cuántos quedaron en cola.
    /// </summary>
    public async Task<int> AcceptAsync(Guid botId, IReadOnlyList<InboundMessage> messages, CancellationToken ct)
    {
        var accepted = 0;
        var now = clock.GetUtcNow();
        var channels = new Dictionary<(ChannelKind, string), Channel?>();

        foreach (var m in messages)
        {
            if (!channels.TryGetValue((m.Kind, m.ExternalChannelId), out var channel))
            {
                // El webhook es anónimo: el canal se busca por el id que trae Meta, sin tenant, y de ahí sale el tenant de todo lo demás.
                channel = await db.Channels.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(c => c.BotId == botId && c.Kind == m.Kind && c.ExternalId == m.ExternalChannelId, ct);
                channels[(m.Kind, m.ExternalChannelId)] = channel;
            }

            if (channel is not { Status: ChannelStatus.Active })
            {
                continue;
            }

            if (await db.ChannelInbounds.IgnoreQueryFilters().AnyAsync(i => i.ChannelId == channel.Id && i.ExternalMessageId == m.MessageId, ct))
            {
                continue; // reintento de Meta: ya lo tenemos
            }

            var row = new ChannelInbound
            {
                Id = Guid.CreateVersion7(),
                ClientId = channel.ClientId,
                ChannelId = channel.Id,
                ExternalMessageId = m.MessageId,
                ContactId = m.ContactId.Length > 160 ? m.ContactId[..160] : m.ContactId,
                ContactName = m.ContactName is { Length: > 120 } ? m.ContactName[..120] : m.ContactName,
                Text = m.Text,
                AttachmentsJson = m.Attachments is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(m.Attachments) : null,
                NextAttemptAt = now,
                CreatedAt = now,
            };
            db.ChannelInbounds.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                accepted++;
            }
            catch (DbUpdateException)
            {
                // Dos entregas simultáneas del mismo mensaje: la otra ganó. Se descarta esta.
                db.ChannelInbounds.Remove(row);
            }
        }

        return accepted;
    }
}

/// <summary>
/// Atiende un mensaje en cola: lo pasa por el motor del chat y envía la respuesta por el mismo canal. La respuesta se guarda antes de enviarla,
/// así un fallo a mitad de envío se reintenta sin volver a preguntar al modelo ni repetir lo que ya llegó.
/// </summary>
public sealed class MetaInboxProcessor(
    IAppDbContext db, ChatService chat, IMetaGraph graph, ISecretProtector secrets, IAttachmentStore attachmentStore, IOptions<MetaOptions> options, TimeProvider clock)
{
    public const string NonTextReply = "Por ahora solo puedo leer mensajes de texto. Escríbeme tu pregunta.";
    public const string LimitReply = "En este momento no podemos seguir atendiendo este chat. Intenta de nuevo más tarde.";
    public const string TooLongReply = "Tu mensaje es muy largo. Envíalo en partes más cortas.";

    public async Task<InboundOutcome> ProcessAsync(Guid inboundId, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();

        // Aún no hay tenant: todo se busca sin filtro hasta que el motor del chat fije el del canal.
        var inbound = await db.ChannelInbounds.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == inboundId, ct);
        if (inbound is null)
        {
            return InboundOutcome.Dropped;
        }

        // Un mensaje que ya agotó sus intentos (por ejemplo, se reclama y el trabajador falla antes de anotarlo) no se atiende más.
        if (inbound.Attempts > o.MaxAttempts + 2)
        {
            return await FinishAsync(inbound, InboundStatus.Failed, "demasiados intentos", InboundOutcome.Failed, ct);
        }

        var channel = await db.Channels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == inbound.ChannelId, ct);
        if (channel is not { Status: ChannelStatus.Active })
        {
            return await FinishAsync(inbound, InboundStatus.Done, "canal inactivo", InboundOutcome.Dropped, ct);
        }

        if (inbound.ReplyText is null)
        {
            // Tope por contacto y hora: este mensaje es el n.º N de la última hora (cuenta los anteriores y a sí mismo, no los que lleguen después).
            // Corta bucles entre bots sin gastar IA.
            var recent = await db.ChannelInbounds.IgnoreQueryFilters()
                .CountAsync(i => i.ChannelId == channel.Id && i.ContactId == inbound.ContactId && i.CreatedAt > now.AddHours(-1) && i.CreatedAt <= inbound.CreatedAt, ct);
            if (recent > o.MaxMessagesPerContactPerHour)
            {
                return await FinishAsync(inbound, InboundStatus.Done, "tope por contacto", InboundOutcome.Dropped, ct);
            }

            var built = await BuildReplyAsync(inbound, channel, ct);
            if (built.Outcome is { } early)
            {
                return early == InboundOutcome.Retry
                    ? await RetryOrFailAsync(inbound, "el modelo no respondió", ct)
                    : await FinishAsync(inbound, InboundStatus.Done, "sin respuesta", early, ct);
            }

            if (string.IsNullOrEmpty(built.Reply))
            {
                // Atiende una persona: el mensaje ya quedó guardado en la conversación y el bot no responde.
                return await FinishAsync(inbound, InboundStatus.Done, "atiende una persona", InboundOutcome.Dropped, ct);
            }

            inbound.ReplyText = built.Reply;
            await db.SaveChangesAsync(ct);
        }

        return await SendAsync(inbound, channel, ct);
    }

    private async Task<(string? Reply, InboundOutcome? Outcome)> BuildReplyAsync(ChannelInbound inbound, Channel channel, CancellationToken ct)
    {
        var contact = new ChannelContact(channel.Id, inbound.ContactId, inbound.ContactName, channel.Kind);
        var incoming = ReadAttachments(inbound.AttachmentsJson);
        if (incoming.Count > 0)
        {
            // Los archivos se descargan y se guardan antes de contestar; el aviso de recibido solo sale si el contacto no escribió texto.
            var saved = await SaveAttachmentsAsync(channel, incoming, ct);
            var recorded = await chat.RecordAttachmentsFromChannelAsync(
                channel.ClientId, channel.BotId, contact, saved, acknowledge: inbound.Text is null, options.Value.AttachmentRetentionDays, ct);
            inbound.AttachmentsJson = null; // ya quedaron: un reintento no los guarda dos veces
            await db.SaveChangesAsync(ct);
            if (!recorded.IsSuccess)
            {
                return recorded.Error!.Code switch
                {
                    "limit_reached" => (LimitReply, null),
                    "unavailable" => (null, InboundOutcome.Retry),
                    _ => (null, InboundOutcome.Dropped),
                };
            }

            if (inbound.Text is null)
            {
                return (recorded.Value, null); // vacío = atiende una persona: no se responde
            }
        }

        if (inbound.Text is null)
        {
            return (NonTextReply, null);
        }

        var result = await chat.ReceiveFromChannelAsync(channel.ClientId, channel.BotId, contact, inbound.Text, ct);
        if (!result.IsSuccess)
        {
            return result.Error!.Code switch
            {
                "unavailable" => (null, InboundOutcome.Retry),
                "limit_reached" => (LimitReply, null),
                "validation_error" => (TooLongReply, null),
                _ => (null, InboundOutcome.Dropped),
            };
        }

        var reply = ReplyFormat.ForChannel(result.Value!.Reply, channel.Kind);
        if (result.Value.NewConversation)
        {
            // Ley 1581: al abrir una conversación se avisa qué política de datos aplica (si el negocio la tiene).
            var privacy = await db.Bots.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == channel.BotId).Select(b => b.PrivacyUrl).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(privacy))
            {
                reply = $"Al escribirnos aceptas que usemos tus datos para atenderte, según nuestra política: {privacy}\n\n{reply}";
            }
        }

        return (reply, null);
    }

    private static List<InboundAttachment> ReadAttachments(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<InboundAttachment>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Descarga cada archivo de Meta y lo guarda en el disco. Si uno falla, queda registrado como no disponible (el contacto sí lo envió).</summary>
    private async Task<IReadOnlyList<SavedAttachment>> SaveAttachmentsAsync(Channel channel, IReadOnlyList<InboundAttachment> incoming, CancellationToken ct)
    {
        var o = options.Value;
        string? token = null;
        try
        {
            token = secrets.Unprotect(channel.AccessTokenEncrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Sin token no hay descarga: los archivos se anotan como no disponibles.
        }

        var saved = new List<SavedAttachment>();
        foreach (var a in incoming.Take(Math.Max(1, o.MaxAttachmentsPerMessage)))
        {
            var id = Guid.CreateVersion7();
            var name = AttachmentTypes.SafeFileName(a.FileName);
            try
            {
                if (token is null)
                {
                    throw new MetaSendException("Sin token.", permanent: true);
                }

                var file = await graph.DownloadMediaAsync(channel.Kind, token, a, o.AttachmentMaxBytes, ct);
                await attachmentStore.SaveAsync(channel.ClientId, id, file.Content, ct);
                saved.Add(new SavedAttachment(id, a.Kind, AttachmentTypes.Normalize(file.MimeType ?? a.MimeType), name, file.Content.Length, true));
            }
            catch (Exception ex) when (ex is MetaSendException or IOException or UnauthorizedAccessException)
            {
                saved.Add(new SavedAttachment(id, a.Kind, AttachmentTypes.Normalize(a.MimeType), name, 0, false));
            }
        }

        return saved;
    }

    private async Task<InboundOutcome> SendAsync(ChannelInbound inbound, Channel channel, CancellationToken ct)
    {
        // En WhatsApp las líneas «Foto: https://…» se envían como imágenes después del texto; en los demás canales ya se quitaron.
        var photos = channel.Kind == ChannelKind.WhatsApp ? ReplyFormat.PhotoUrls(inbound.ReplyText!) : [];
        var parts = ChannelText.Split(channel.Kind == ChannelKind.WhatsApp ? ReplyFormat.WithoutPhotoLines(inbound.ReplyText!) : inbound.ReplyText!, ChannelText.MaxLength(channel.Kind));
        string token;
        try
        {
            token = secrets.Unprotect(channel.AccessTokenEncrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            channel.Status = ChannelStatus.TokenError;
            return await FinishAsync(inbound, InboundStatus.Failed, "token ilegible", InboundOutcome.Failed, ct);
        }

        try
        {
            for (var i = inbound.SentParts; i < parts.Count + photos.Count; i++)
            {
                if (i < parts.Count)
                {
                    await graph.SendTextAsync(channel.Kind, channel.ExternalId, token, inbound.ContactId, parts[i], ct);
                }
                else
                {
                    try
                    {
                        await graph.SendImageAsync(channel.Kind, channel.ExternalId, token, inbound.ContactId, photos[i - parts.Count], ct);
                    }
                    catch (MetaSendException ex) when (ex.Permanent && !ex.TokenRejected)
                    {
                        // Una foto que Meta no puede abrir (enlace roto, formato no admitido) no debe tumbar la respuesta: el texto ya salió.
                        inbound.SentParts = i + 1;
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }

                inbound.SentParts = i + 1;
                await db.SaveChangesAsync(ct); // lo enviado queda anotado: un reintento no lo repite
                if (channel.Kind == ChannelKind.WhatsApp)
                {
                    await db.RecordWhatsAppSentAsync(channel.BotId, channel.ClientId, UsagePeriod.Of(clock.GetUtcNow()), 1, clock.GetUtcNow(), ct);
                }
            }
        }
        catch (MetaSendException ex) when (ex.TokenRejected)
        {
            channel.Status = ChannelStatus.TokenError;
            return await FinishAsync(inbound, InboundStatus.Failed, "token rechazado por Meta", InboundOutcome.Failed, ct);
        }
        catch (MetaSendException ex) when (ex.Permanent)
        {
            return await FinishAsync(inbound, InboundStatus.Failed, "Meta rechazó el envío", InboundOutcome.Failed, ct);
        }
        catch (MetaSendException)
        {
            return await RetryOrFailAsync(inbound, "no se pudo enviar", ct);
        }

        return await FinishAsync(inbound, InboundStatus.Done, null, InboundOutcome.Done, ct);
    }

    private async Task<InboundOutcome> RetryOrFailAsync(ChannelInbound inbound, string reason, CancellationToken ct)
    {
        if (inbound.Attempts >= options.Value.MaxAttempts)
        {
            return await FinishAsync(inbound, InboundStatus.Failed, reason, InboundOutcome.Failed, ct);
        }

        inbound.Status = InboundStatus.Pending;
        inbound.NextAttemptAt = clock.GetUtcNow().AddSeconds(30 * Math.Max(1, inbound.Attempts));
        inbound.LockedUntil = null;
        inbound.LastError = reason;
        await db.SaveChangesAsync(ct);
        return InboundOutcome.Retry;
    }

    /// <summary>Cierra el mensaje y borra su contenido: solo quedan los metadatos para descartar reintentos y contar el tope por contacto.</summary>
    private async Task<InboundOutcome> FinishAsync(ChannelInbound inbound, InboundStatus status, string? reason, InboundOutcome outcome, CancellationToken ct)
    {
        inbound.Status = status;
        inbound.LastError = reason;
        inbound.Text = null;
        inbound.AttachmentsJson = null;
        inbound.ReplyText = null;
        inbound.LockedUntil = null;
        inbound.ProcessedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return outcome;
    }
}
