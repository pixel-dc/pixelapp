using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Domain.Agency;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;

namespace Pixela.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IClientScope scope)
    : DbContext(options), IAppDbContext
{
    public DbSet<AgencySettings> AgencySettings => Set<AgencySettings>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotSecrets> BotSecrets => Set<BotSecrets>();
    public DbSet<BotIcon> BotIcons => Set<BotIcon>();
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<DataConnection> DataConnections => Set<DataConnection>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelInbound> ChannelInbounds => Set<ChannelInbound>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<ChatUsage> ChatUsages => Set<ChatUsage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    // Propiedades del contexto: EF las evalúa en cada consulta (filtro global por cliente).
    private Guid CurrentClientId => scope.ClientId;
    private bool AllClients => scope.AllClients;

    public Task RecordChatUsageAsync(Guid botId, Guid clientId, int period, int conversations, int messages, long tokensIn, long tokensOut, long costMicros, DateTimeOffset now, CancellationToken ct, ChannelKind? channel = null)
    {
        var wa = channel == ChannelKind.WhatsApp ? conversations : 0;
        var ms = channel == ChannelKind.Messenger ? conversations : 0;
        var ig = channel == ChannelKind.Instagram ? conversations : 0;
        return Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO chat_usage ("BotId", "ClientId", "Period", "Conversations", "ConversationsWhatsApp", "ConversationsMessenger", "ConversationsInstagram", "WhatsAppMessagesSent", "Messages", "TokensIn", "TokensOut", "CostMicros", "UpdatedAt")
            VALUES ({botId}, {clientId}, {period}, {conversations}, {wa}, {ms}, {ig}, 0, {messages}, {tokensIn}, {tokensOut}, {costMicros}, {now})
            ON CONFLICT ("BotId", "Period") DO UPDATE SET
                "Conversations" = chat_usage."Conversations" + EXCLUDED."Conversations",
                "ConversationsWhatsApp" = chat_usage."ConversationsWhatsApp" + EXCLUDED."ConversationsWhatsApp",
                "ConversationsMessenger" = chat_usage."ConversationsMessenger" + EXCLUDED."ConversationsMessenger",
                "ConversationsInstagram" = chat_usage."ConversationsInstagram" + EXCLUDED."ConversationsInstagram",
                "Messages" = chat_usage."Messages" + EXCLUDED."Messages",
                "TokensIn" = chat_usage."TokensIn" + EXCLUDED."TokensIn",
                "TokensOut" = chat_usage."TokensOut" + EXCLUDED."TokensOut",
                "CostMicros" = chat_usage."CostMicros" + EXCLUDED."CostMicros",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            ct);
    }

    public Task RecordWhatsAppSentAsync(Guid botId, Guid clientId, int period, int count, DateTimeOffset now, CancellationToken ct) =>
        Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO chat_usage ("BotId", "ClientId", "Period", "Conversations", "ConversationsWhatsApp", "ConversationsMessenger", "ConversationsInstagram", "WhatsAppMessagesSent", "Messages", "TokensIn", "TokensOut", "CostMicros", "UpdatedAt")
            VALUES ({botId}, {clientId}, {period}, 0, 0, 0, 0, {count}, 0, 0, 0, 0, {now})
            ON CONFLICT ("BotId", "Period") DO UPDATE SET
                "WhatsAppMessagesSent" = chat_usage."WhatsAppMessagesSent" + EXCLUDED."WhatsAppMessagesSent",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            ct);

    public async Task DeleteBotCascadeAsync(Guid botId, CancellationToken ct)
    {
        await using var transaction = await Database.BeginTransactionAsync(ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM chat_attachments WHERE \"ConversationId\" IN (SELECT \"Id\" FROM conversations WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM chat_messages WHERE \"ConversationId\" IN (SELECT \"Id\" FROM conversations WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM conversations WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM leads WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM channel_inbound WHERE \"ChannelId\" IN (SELECT \"Id\" FROM channels WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM channels WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM data_connections WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge_chunks WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge_sources WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bot_icons WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bot_secrets WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"DELETE FROM bots WHERE \"Id\" = {botId}", ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReassignBotClientAsync(Guid botId, Guid clientId, CancellationToken ct)
    {
        await using var transaction = await Database.BeginTransactionAsync(ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE bots SET \"ClientId\" = {clientId} WHERE \"Id\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE bot_secrets SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE bot_icons SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge_sources SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledge_chunks SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE data_connections SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE channels SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE channel_inbound SET \"ClientId\" = {clientId} WHERE \"ChannelId\" IN (SELECT \"Id\" FROM channels WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_messages SET \"ClientId\" = {clientId} WHERE \"ConversationId\" IN (SELECT \"Id\" FROM conversations WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_attachments SET \"ClientId\" = {clientId} WHERE \"ConversationId\" IN (SELECT \"Id\" FROM conversations WHERE \"BotId\" = {botId})", ct);
        await Database.ExecuteSqlInterpolatedAsync($"UPDATE leads SET \"ClientId\" = {clientId} WHERE \"BotId\" = {botId}", ct);
        await transaction.CommitAsync(ct);
    }

    public Task AcquireSetupLockAsync(CancellationToken ct) => Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7428101)", ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Sin filtro: una sola fila con la configuración de la agencia (la crea DatabaseInitializer).
        modelBuilder.Entity<AgencySettings>(e =>
        {
            e.ToTable("agency_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.LogoUrl).HasMaxLength(2000);
            e.Property(x => x.PrimaryColor).HasMaxLength(7);
            e.Property(x => x.Texts).HasColumnType("jsonb");
            e.Property(x => x.DefaultModel).HasMaxLength(100);
            e.Property(x => x.Effort).HasMaxLength(10);
            e.Property(x => x.InputUsdPerMillion).HasPrecision(18, 6);
            e.Property(x => x.OutputUsdPerMillion).HasPrecision(18, 6);
            e.Property(x => x.PublicBaseUrl).HasMaxLength(300);
            e.ToTable(t => t.HasCheckConstraint("ck_agency_settings_singleton", $"\"Id\" = {Domain.Agency.AgencySettings.SingletonId}"));
        });

        modelBuilder.Entity<Client>(e =>
        {
            e.ToTable("clients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LogoUrl).HasMaxLength(2000);
            e.Property(x => x.PrimaryColor).HasMaxLength(7);
            e.Property(x => x.Texts).HasColumnType("jsonb");
            e.Property(x => x.PricePerConversation).HasPrecision(18, 6);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Ignore(x => x.IsActive);
            e.HasQueryFilter(x => AllClients || x.Id == CurrentClientId);
        });

        modelBuilder.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.ClientId);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<Bot>(e =>
        {
            e.ToTable("bots");
            e.HasKey(x => x.Id);
            e.Property(x => x.PublicKey).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.SystemPrompt).HasMaxLength(8000);
            e.Property(x => x.Tone).HasMaxLength(200);
            e.Property(x => x.WelcomeMessage).HasMaxLength(1000);
            e.Property(x => x.Farewell).HasMaxLength(1000);
            e.Property(x => x.HandoffMessage).HasMaxLength(1000);
            e.Property(x => x.CallToAction).HasMaxLength(1000);
            e.Property(x => x.BusinessName).HasMaxLength(100);
            e.Property(x => x.QuickGreeting).HasMaxLength(600);
            e.Property(x => x.QuickFarewell).HasMaxLength(600);
            e.Property(x => x.QuickClosing).HasMaxLength(600);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.WhatsAppNumber).HasMaxLength(20);
            e.Property(x => x.Color).HasMaxLength(7);
            e.Property(x => x.PrivacyUrl).HasMaxLength(2000);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => x.PublicKey).IsUnique();
            e.HasIndex(x => x.ClientId);
            e.HasIndex(x => x.AssetId);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<BotSecrets>(e =>
        {
            e.ToTable("bot_secrets");
            e.HasKey(x => x.BotId);
            e.Property(x => x.ClaudeApiKeyEncrypted).HasMaxLength(2000);
            e.Property(x => x.ClaudeApiKeyHint).HasMaxLength(8);
            e.Property(x => x.MetaAppSecretEncrypted).HasMaxLength(2000);
            e.Property(x => x.MetaAppSecretHint).HasMaxLength(8);
            e.Property(x => x.MetaVerifyTokenEncrypted).HasMaxLength(2000);
            e.HasOne<Bot>().WithOne().HasForeignKey<BotSecrets>(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<BotIcon>(e =>
        {
            e.ToTable("bot_icons");
            e.HasKey(x => x.BotId);
            e.Property(x => x.MimeType).HasMaxLength(30);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<KnowledgeSource>(e =>
        {
            e.ToTable("knowledge_sources");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.SourceTable).HasMaxLength(63);
            e.Property(x => x.SourceColumns).HasMaxLength(500);
            e.Property(x => x.LinkTemplate).HasMaxLength(500);
            e.Property(x => x.ImageColumn).HasMaxLength(63);
            e.Property(x => x.SecretEncrypted).HasMaxLength(4000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => x.BotId);
            e.HasIndex(x => x.Status).HasFilter("\"Status\" = 'Pending'").HasDatabaseName("ix_knowledge_sources_pending");
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<KnowledgeChunk>(e =>
        {
            e.ToTable("knowledge_chunks");
            e.HasKey(x => x.Id);
            // Columna generada para la búsqueda de texto completo en español; vive solo en la base (propiedad en sombra).
            e.Property<NpgsqlTypes.NpgsqlTsVector>("Tsv").HasColumnType("tsvector")
                .HasComputedColumnSql("to_tsvector('spanish', \"Text\")", stored: true);
            e.HasIndex("Tsv").HasMethod("GIN");
            e.HasIndex(x => x.BotId);
            e.HasIndex(x => x.SourceId);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<DataConnection>(e =>
        {
            e.ToTable("data_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.SecretEncrypted).HasMaxLength(4000);
            e.Property(x => x.AllowedTools).HasMaxLength(2000);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.BotId);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.ToTable("channels");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.AccessTokenEncrypted).HasMaxLength(4000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Un número, página o cuenta pertenece a un solo chatbot: es la clave con la que se enruta cada webhook.
            e.HasIndex(x => new { x.Kind, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.BotId);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<ChannelInbound>(e =>
        {
            e.ToTable("channel_inbound");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalMessageId).HasMaxLength(200);
            e.Property(x => x.ContactId).HasMaxLength(160);
            e.Property(x => x.ContactName).HasMaxLength(120);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.ReplyText).HasMaxLength(8000);
            e.Property(x => x.LastError).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.HasIndex(x => new { x.ChannelId, x.ExternalMessageId }).IsUnique();
            e.HasIndex(x => new { x.ChannelId, x.ContactId, x.CreatedAt });
            // Índice parcial para la cola de entrada (FOR UPDATE SKIP LOCKED).
            e.HasIndex(x => x.NextAttemptAt).HasFilter("\"Status\" IN ('Pending', 'Processing')").HasDatabaseName("ix_channel_inbound_queue");
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.VisitorId).HasMaxLength(64);
            e.Property(x => x.Origin).HasMaxLength(300);
            e.Property(x => x.ContactId).HasMaxLength(160);
            e.Property(x => x.ContactName).HasMaxLength(120);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            // La conversación abierta de un contacto en su canal (ventana de 24 horas).
            e.HasIndex(x => new { x.ChannelId, x.ContactId, x.CreatedAt });
            e.HasIndex(x => new { x.BotId, x.LastMessageAt });
            e.HasIndex(x => x.LastMessageAt);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(10);
            e.Property(x => x.Content).HasMaxLength(8000);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<ChatAttachment>(e =>
        {
            e.ToTable("chat_attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.MimeType).HasMaxLength(100);
            e.Property(x => x.FileName).HasMaxLength(200);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<Lead>(e =>
        {
            e.ToTable("leads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Interest).HasMaxLength(500);
            e.HasIndex(x => new { x.BotId, x.CreatedAt });
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        // Consumo por bot y mes: sobrevive al borrado del bot (para auditar contra la consola de Anthropic), por eso sin llave foránea.
        modelBuilder.Entity<ChatUsage>(e =>
        {
            e.ToTable("chat_usage");
            e.HasKey(x => new { x.BotId, x.Period });
            e.HasIndex(x => new { x.ClientId, x.Period });
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        // Usuarios: los de la agencia tienen ClientId nulo y solo los ve la vista de agencia (o el inicio de sesión, sin filtro).
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.PasswordHash).HasMaxLength(300);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TotpSecretEncrypted).HasMaxLength(500);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.PreferencesJson).HasMaxLength(2000);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.ClientId);
            e.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            // Un rol de agencia nunca pertenece a un cliente, y uno de cliente siempre.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_users_role_client",
                "(\"Role\" IN ('AgencyOwner', 'AgencyAdmin') AND \"ClientId\" IS NULL) OR (\"Role\" IN ('ClientAdmin', 'Operator') AND \"ClientId\" IS NOT NULL)"));
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        // Sin filtro: se lee por usuario, y quien lo pide ya tiene acceso a ese usuario.
        modelBuilder.Entity<UserAvatar>(e =>
        {
            e.ToTable("user_avatars");
            e.HasKey(x => x.UserId);
            e.Property(x => x.MimeType).HasMaxLength(30);
            e.HasOne<User>().WithOne().HasForeignKey<UserAvatar>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<UserInvite>(e =>
        {
            e.ToTable("user_invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.ClientId, x.Email });
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });

        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => AllClients || x.ClientId == CurrentClientId);
        });
    }
}
