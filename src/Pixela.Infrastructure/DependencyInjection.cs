using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Chat;
using Pixela.Application.Clients;
using Pixela.Application.Users;
using Pixela.Application.Http;
using Pixela.Infrastructure.Chat;
using Pixela.Infrastructure.Maintenance;
using Pixela.Infrastructure.Persistence;
using Pixela.Infrastructure.Push;
using Pixela.Infrastructure.Security;

namespace Pixela.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPixelaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Default.");

        services.AddSingleton(TimeProvider.System);

        // ClientScope es scoped: una instancia por petición, compartida por la autenticación y el DbContext.
        services.AddScoped<ClientScope>();
        services.AddScoped<IClientScope>(sp => sp.GetRequiredService<ClientScope>());

        // jsonb con Dictionary<string,string> (textos de la marca) exige habilitar JSON dinámico.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build());
        services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.Section));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddMemoryCache();
        services.AddScoped<AgencySettingsReader>();
        services.AddScoped<AgencySettingsService>();
        services.AddScoped<ClientService>();

        // Acceso propio de cada cliente (su subdominio) y su app instalable.
        services.Configure<ClientAppsOptions>(configuration.GetSection(ClientAppsOptions.Section));
        services.AddScoped<ClientHost>();
        services.AddScoped<ClientHostResolver>();
        services.AddScoped<ClientAppService>();

        // Notificaciones: suscripciones de cada dispositivo y una cola que envía los avisos aparte del chat.
        services.AddSingleton<IWebPush, WebPushClient>();
        services.AddScoped<PushService>();
        services.AddScoped<ConversationAlertDelivery>();
        services.AddSingleton<ConversationAlertSender>();
        services.AddSingleton<IConversationAlerts>(sp => sp.GetRequiredService<ConversationAlertSender>());
        services.AddHostedService(sp => sp.GetRequiredService<ConversationAlertSender>());

        // Panel: sesiones, equipos y primer arranque.
        services.Configure<PanelOptions>(configuration.GetSection(PanelOptions.Section));
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<SetupService>();

        // Chat: ajustes técnicos con valores por defecto (nada secreto: llaves y apps de Meta son de cada bot y viven cifradas en la base).
        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.Section));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.Section));
        services.Configure<OutboundHttpOptions>(configuration.GetSection(OutboundHttpOptions.Section));

        services.AddScoped<BotService>();
        services.AddScoped<BotKeyService>();
        services.AddScoped<BotMetaAppService>();
        services.AddScoped<BotUsageService>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<DataConnectionService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<QuickReplyService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<ChatService>();
        services.AddScoped<MetaInboxService>();
        services.AddScoped<MetaInboxProcessor>();
        services.AddScoped<ChannelInboundClaimer>();
        services.AddScoped<IKnowledgeSearch, PostgresKnowledgeSearch>();
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IWebPageFetcher, SafeWebPageFetcher>();
        services.AddSingleton<ICatalogFetcher, SupabaseCatalogFetcher>();
        services.AddSingleton<IMcpClient, McpHttpClient>();
        services.AddSingleton<IMetaGraph, MetaGraphClient>();
        services.AddSingleton<IMetaAdmin, MetaAdminClient>();
        services.AddScoped<MetaConnectionService>();
        services.AddSingleton<IAttachmentStore, LocalAttachmentStore>();

        // Siempre Claude: cada bot pone su llave. Sin llave, ChatService responde con el modo extractivo (sin modelo y sin costo).
        services.AddSingleton<AnthropicGateway>();
        services.AddSingleton<ExtractiveGateway>();
        services.AddSingleton<IAiGateway>(sp => sp.GetRequiredService<AnthropicGateway>());
        services.AddSingleton<IAiKeyTester>(sp => sp.GetRequiredService<AnthropicGateway>());

        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.Section));
        services.AddSingleton<MaintenanceRunner>();

        return services;
    }
}
