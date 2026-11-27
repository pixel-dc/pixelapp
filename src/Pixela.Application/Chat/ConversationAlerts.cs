using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Users;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;

namespace Pixela.Application.Chat;

/// <summary>Algo en una conversación que una persona del negocio debería ver ya.</summary>
public sealed record ConversationAlert(Guid ClientId, Guid BotId, Guid ConversationId, string Title, string Body);

/// <summary>
/// Cola de avisos: quien guarda el mensaje del contacto solo deja el aviso y sigue; el envío a los teléfonos ocurre aparte, para que el chat
/// no espere a los servicios de notificaciones.
/// </summary>
public interface IConversationAlerts
{
    void Publish(ConversationAlert alert);
}

/// <summary>Arma el texto de los avisos: quién escribe, por dónde y qué dijo.</summary>
public static class ConversationAlertText
{
    public const int MaxBody = 140;

    /// <summary>
    /// Conversación nueva: «Nueva conversación por WhatsApp» y quién escribe. Si no, el contacto y su canal en el título; en el cuerpo, lo que
    /// escribió.
    /// </summary>
    public static ConversationAlert Build(Conversation conversation, ChannelKind? kind, string text, bool isNew)
    {
        var channel = kind switch
        {
            ChannelKind.WhatsApp => "WhatsApp",
            ChannelKind.Messenger => "Messenger",
            ChannelKind.Instagram => "Instagram",
            _ => conversation.ChannelId is null ? "el chat web" : "un canal",
        };
        var who = string.IsNullOrWhiteSpace(conversation.ContactName)
            ? conversation.ChannelId is null ? "Visitante del chat web" : "Contacto nuevo"
            : conversation.ContactName.Trim();
        var said = Shorten(text);
        return isNew
            ? new ConversationAlert(conversation.ClientId, conversation.BotId, conversation.Id, $"Nueva conversación por {channel}", $"{who}: {said}")
            : new ConversationAlert(conversation.ClientId, conversation.BotId, conversation.Id, $"{who} · {Capitalize(channel)}", said);
    }

    private static string Shorten(string text)
    {
        var t = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxBody ? t : t[..(MaxBody - 1)].TrimEnd() + "…";
    }

    private static string Capitalize(string s) => s.StartsWith("el ", StringComparison.Ordinal) ? "Chat web" : s;
}

/// <summary>
/// Entrega un aviso a los dispositivos que deben verlo: los de los usuarios de ese cliente y los de la agencia que lo siguen (desde el acceso
/// de ese cliente, o desde el panel general, que sigue a todos). Nunca a usuarios de otro cliente.
/// </summary>
public sealed class ConversationAlertDelivery(IAppDbContext db, IWebPush push, TimeProvider clock)
{
    /// <summary>Tras tantos fallos seguidos, la suscripción se da por perdida.</summary>
    public const int MaxFailures = 10;

    public async Task<int> DeliverAsync(ConversationAlert alert, CancellationToken ct)
    {
        var active = await db.Clients.IgnoreQueryFilters().AsNoTracking().AnyAsync(c => c.Id == alert.ClientId && c.Status == ClientStatus.Active, ct);
        if (!active)
        {
            return 0;
        }

        var targets = await (
            from s in db.PushSubscriptions
            join u in db.Users.IgnoreQueryFilters() on s.UserId equals u.Id
            where u.IsActive
                && (s.ClientId == alert.ClientId || s.ClientId == null)
                && (u.ClientId == null || u.ClientId == alert.ClientId)
            select s).ToListAsync(ct);
        if (targets.Count == 0)
        {
            return 0;
        }

        // El mismo aviso por conversación: en el teléfono el nuevo reemplaza al anterior en vez de acumularse.
        var tag = alert.ConversationId.ToString("N");
        var payload = PushService.Payload(alert.Title, alert.Body, tag, $"/bandeja?bot={alert.BotId}&c={alert.ConversationId}");
        var now = clock.GetUtcNow();
        var sent = 0;
        foreach (var s in targets)
        {
            var outcome = await push.SendAsync(new PushTarget(s.Endpoint, s.P256dh, s.Auth), payload, tag, ct);
            switch (outcome)
            {
                case PushOutcome.Sent:
                    s.LastSentAt = now;
                    s.Failures = 0;
                    sent++;
                    break;
                case PushOutcome.Gone:
                    db.PushSubscriptions.Remove(s);
                    break;
                default:
                    if (++s.Failures >= MaxFailures)
                    {
                        db.PushSubscriptions.Remove(s);
                    }

                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }
}
