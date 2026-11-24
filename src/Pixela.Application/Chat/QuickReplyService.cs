using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

public sealed record QuickRepliesView(string? BusinessName, string Greeting, string Farewell, string Closing, QuickRepliesDefaults Defaults);

public sealed record QuickRepliesDefaults(string Greeting, string Farewell, string Closing);

/// <summary>Los tres textos se guardan tal cual; vacío = volver al texto por defecto.</summary>
public sealed record SetQuickRepliesRequest(string? BusinessName, string? Greeting, string? Farewell, string? Closing);

public sealed record QuickReplyRequest(string? Kind);

/// <summary>
/// Respuestas rápidas de quien atiende una conversación (saludo, despedida y cierre). Son de cada chatbot: un grupo con restaurantes y supermercados
/// tiene un guion distinto en cada uno. Llevan {operador} (quien atiende) y {establecimiento} (el nombre del negocio de ese chatbot).
/// </summary>
public sealed partial class QuickReplyService(IAppDbContext db, ConversationService conversations)
{
    public const int MaxTextLength = 600;
    public const int MaxNameLength = 100;

    public static readonly QuickRepliesDefaults Defaults = new(
        "Hola, soy {operador} de {establecimiento}. ¿En qué te puedo ayudar?",
        "Gracias por escribirnos a {establecimiento}. Que tengas un excelente día. — {operador}",
        "Damos por finalizada esta conversación. Si necesitas algo más, escríbenos de nuevo. — {establecimiento}");

    private static readonly string[] Placeholders = ["operador", "establecimiento"];

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Token();

    public async Task<ServiceResult<QuickRepliesView>> GetAsync(Guid botId, CancellationToken ct)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        return bot is null
            ? ServiceResult<QuickRepliesView>.Fail("not_found", "Chatbot no encontrado.")
            : ServiceResult<QuickRepliesView>.Ok(new QuickRepliesView(bot.BusinessName, bot.QuickGreeting ?? Defaults.Greeting, bot.QuickFarewell ?? Defaults.Farewell, bot.QuickClosing ?? Defaults.Closing, Defaults));
    }

    public async Task<ServiceResult<QuickRepliesView>> SetAsync(Guid botId, SetQuickRepliesRequest request, CancellationToken ct)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null)
        {
            return ServiceResult<QuickRepliesView>.Fail("not_found", "Chatbot no encontrado.");
        }

        var errors = new Dictionary<string, string>();
        var name = Clean(request.BusinessName);
        if (name is { Length: > MaxNameLength })
        {
            errors["business_name"] = $"Máximo {MaxNameLength} caracteres.";
        }

        var greeting = Check("greeting", request.Greeting, errors);
        var farewell = Check("farewell", request.Farewell, errors);
        var closing = Check("closing", request.Closing, errors);
        if (errors.Count > 0)
        {
            return ServiceResult<QuickRepliesView>.Fail("validation_error", "Revisa los textos.", errors);
        }

        bot.BusinessName = name;
        bot.QuickGreeting = greeting;
        bot.QuickFarewell = farewell;
        bot.QuickClosing = closing;
        await db.SaveChangesAsync(ct);
        return await GetAsync(botId, ct);
    }

    /// <summary>
    /// Envía una respuesta rápida: arma el texto con el nombre de quien atiende y del establecimiento. «closing» además devuelve la conversación al chatbot.
    /// </summary>
    public async Task<ServiceResult<bool>> SendAsync(Guid actorUserId, Guid botId, Guid conversationId, string? kind, CancellationToken ct)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Chatbot no encontrado.");
        }

        var template = kind?.ToLowerInvariant() switch
        {
            "greeting" => bot.QuickGreeting ?? Defaults.Greeting,
            "farewell" => bot.QuickFarewell ?? Defaults.Farewell,
            "closing" => bot.QuickClosing ?? Defaults.Closing,
            _ => null,
        };
        if (template is null)
        {
            return ServiceResult<bool>.Fail("validation_error", "Respuesta rápida desconocida.");
        }

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == actorUserId).Select(u => new { u.DisplayName, u.Email }).FirstOrDefaultAsync(ct);
        var operatorName = !string.IsNullOrWhiteSpace(user?.DisplayName) ? user!.DisplayName!.Trim() : user?.Email.Split('@')[0] ?? "el equipo";
        var clientName = await db.Clients.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == bot.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var establishment = !string.IsNullOrWhiteSpace(bot.BusinessName) ? bot.BusinessName.Trim() : clientName ?? bot.Name;

        var text = Render(template, operatorName, establishment);
        var sent = await conversations.ReplyAsync(botId, conversationId, new AgentReplyRequest(text), ct);
        if (!sent.IsSuccess || !string.Equals(kind, "closing", StringComparison.OrdinalIgnoreCase))
        {
            return sent;
        }

        return await conversations.ReleaseAsync(botId, conversationId, ct);
    }

    public static string Render(string template, string operatorName, string establishment) =>
        template.Replace("{operador}", operatorName, StringComparison.Ordinal).Replace("{establecimiento}", establishment, StringComparison.Ordinal);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Check(string field, string? value, Dictionary<string, string> errors)
    {
        var text = Clean(value);
        if (text is null)
        {
            return null;
        }

        if (text.Length > MaxTextLength)
        {
            errors[field] = $"Máximo {MaxTextLength} caracteres.";
        }
        else if (Token().Matches(text).Select(m => m.Groups[1].Value).FirstOrDefault(t => !Placeholders.Contains(t)) is { } unknown)
        {
            errors[field] = $"«{{{unknown}}}» no existe. Usa {{operador}} y {{establecimiento}}.";
        }

        return text;
    }
}
