using Microsoft.EntityFrameworkCore;
using Pixela.Domain.Agency;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Application.Abstractions;

public interface IAppDbContext
{
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }

    DbSet<AgencySettings> AgencySettings { get; }
    DbSet<Client> Clients { get; }
    DbSet<Asset> Assets { get; }
    DbSet<Bot> Bots { get; }
    DbSet<BotSecrets> BotSecrets { get; }
    DbSet<BotIcon> BotIcons { get; }
    DbSet<KnowledgeSource> KnowledgeSources { get; }
    DbSet<KnowledgeChunk> KnowledgeChunks { get; }
    DbSet<DataConnection> DataConnections { get; }
    DbSet<Channel> Channels { get; }
    DbSet<ChannelInbound> ChannelInbounds { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ChatMessage> ChatMessages { get; }
    DbSet<ChatAttachment> ChatAttachments { get; }
    DbSet<Lead> Leads { get; }
    DbSet<ChatUsage> ChatUsages { get; }
    DbSet<User> Users { get; }
    DbSet<UserAvatar> UserAvatars { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<UserInvite> UserInvites { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }

    /// <summary>Suma al consumo del bot en el periodo de forma atómica (crea la fila si no existe).</summary>
    Task RecordChatUsageAsync(Guid botId, Guid clientId, int period, int conversations, int messages, long tokensIn, long tokensOut, long costMicros, DateTimeOffset now, CancellationToken ct, ChannelKind? channel = null);

    /// <summary>Suma mensajes enviados por WhatsApp al consumo del mes del bot (de forma atómica; crea la fila si no existe).</summary>
    Task RecordWhatsAppSentAsync(Guid botId, Guid clientId, int period, int count, DateTimeOffset now, CancellationToken ct);

    /// <summary>Borra un bot y todo lo que cuelga de él (el consumo histórico se conserva). SQL directo, sin filtro: quien llama ya comprobó el acceso.</summary>
    Task DeleteBotCascadeAsync(Guid botId, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
