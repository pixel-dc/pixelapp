using Pixela.Domain.Clients;

namespace Pixela.Application.Clients;

/// <summary>Topes vigentes de un cliente. Lo que el cliente no tenga configurado es ilimitado.</summary>
/// <param name="MonthlyConversations">Conversaciones al mes entre todos sus bots; null = sin tope.</param>
/// <param name="MessagesPerConversation">Mensajes por conversación (siempre hay un techo de seguridad contra bucles).</param>
public sealed record EffectiveLimits(int? MonthlyConversations, int MessagesPerConversation)
{
    /// <summary>Techo cuando el cliente no fija tope de mensajes: alto para no estorbar, pero corta un bucle entre bots.</summary>
    public const int SafetyMessagesPerConversation = 1000;

    public static EffectiveLimits For(Client client) =>
        new(client.MonthlyConversationCap, client.MessagesPerConversationCap ?? SafetyMessagesPerConversation);
}
