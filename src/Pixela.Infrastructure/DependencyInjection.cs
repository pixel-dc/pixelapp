using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Chat;
using Pixela.Application.Http;
using Pixela.Infrastructure.Chat;
using Pixela.Infrastructure.Persistence;
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

        // Chat: ajustes técnicos con valores por defecto (nada secreto: llaves y apps de Meta son de cada bot y viven cifradas en la base).
        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.Section));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.Section));
        services.Configure<OutboundHttpOptions>(configuration.GetSection(OutboundHttpOptions.Section));

        services.AddScoped<BotService>();
        services.AddScoped<BotKeyService>();
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
        services.AddSingleton<IAttachmentStore, LocalAttachmentStore>();

        // Siempre Claude: cada bot pone su llave. Sin llave, ChatService responde con el modo extractivo (sin modelo y sin costo).
        services.AddSingleton<AnthropicGateway>();
        services.AddSingleton<ExtractiveGateway>();
        services.AddSingleton<IAiGateway>(sp => sp.GetRequiredService<AnthropicGateway>());
        services.AddSingleton<IAiKeyTester>(sp => sp.GetRequiredService<AnthropicGateway>());

        return services;
    }
}
