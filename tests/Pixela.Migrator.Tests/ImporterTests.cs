using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Pixela.Application.Abstractions;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;
using Pixela.Infrastructure;
using Pixela.Infrastructure.Persistence;
using Pixela.Infrastructure.Security;
using Testcontainers.PostgreSql;

namespace Pixela.Migrator.Tests;

/// <summary>
/// Una base con el esquema real de FacilCorreo (script de sus migraciones, en <c>facilcorreo-schema.sql</c>) y datos de ejemplo cifrados con su llave:
/// la agencia Pixel DC con su propio bot y su cliente Don Porko. Cada prueba migra a una base de Pixela nueva.
/// </summary>
public sealed class SourceFixture : IAsyncLifetime
{
    public static readonly string SourceKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    public static readonly string TargetKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    public const string AgencyAiKey = "sk-ant-api03-agencia-pixeldc-1111";
    public const string PorkoAppSecret = "secreto-app-meta-don-porko-2222";
    public const string PorkoChannelToken = "EAAB-token-whatsapp-don-porko";
    public const string SupabaseKey = "eyJ-anon-key-supabase";
    public const string TotpSecret = "JBSWY3DPEHPK3PXP";

    public static readonly Guid Agency = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Porko = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Other = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid AgencyBot = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid PorkoBot = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid PorkoConversation = Guid.Parse("30000000-0000-0000-0000-000000000001");
    public static readonly Guid PorkoAttachment = Guid.Parse("40000000-0000-0000-0000-000000000001");
    public static readonly Guid PorkoOwner = Guid.Parse("50000000-0000-0000-0000-000000000002");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public string SourceConnection { get; private set; } = "";
    public string SourceAttachments { get; } = Path.Combine(Path.GetTempPath(), "px-mig-src-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        SourceConnection = await CreateDatabaseAsync("facilcorreo");
        await using var conn = new NpgsqlConnection(SourceConnection);
        await conn.OpenAsync();
        await Exec(conn, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "facilcorreo-schema.sql")));

        var s = new AesGcmSecretProtector(Options.Create(new SecretsOptions { MasterKey = SourceKey }));
        var hash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA==$aGFzaA==";
        await Exec(conn, $$"""
            INSERT INTO tenants ("Id","Name","Status","DailyLimit","CreatedAt","Kind","Slug","AiKeyEncrypted") VALUES
              ('{{Agency}}','Pixel DC','Active',500,now(),'Agency','pixel-dc','{{s.Protect(AgencyAiKey)}}'),
              ('{{Porko}}','Don Porko','Active',500,now(),'Client','don-porko',NULL),
              ('{{Other}}','Otro cliente','Active',500,now(),'Client','otro',NULL);
            UPDATE tenants SET "ParentId" = '{{Agency}}' WHERE "Id" IN ('{{Porko}}','{{Other}}');
            INSERT INTO agency_brand ("AgencyId","LogoUrl","PrimaryColor","Texts","UpdatedAt") VALUES ('{{Porko}}','https://donporko.test/logo.png','#c0392b','{"send":"Mandar"}',now());
            INSERT INTO client_chat_settings ("ClientId","AgencyId","PricePerConversation","MonthlyConversationCap","UpdatedAt") VALUES ('{{Porko}}','{{Agency}}',150,NULL,now());
            INSERT INTO bots ("Id","TenantId","PublicKey","Name","WelcomeMessage","AllowedOrigins","IsActive","CreatedAt") VALUES
              ('{{AgencyBot}}','{{Agency}}','bt_pixeldc','Asistente Pixel DC',NULL,'{}',true,now()),
              ('{{PorkoBot}}','{{Porko}}','bt_donporko','Porky','¡Hola!','{https://donporko.test}',true,now());
            INSERT INTO bot_icons ("BotId","TenantId","MimeType","Content","UpdatedAt") VALUES ('{{PorkoBot}}','{{Porko}}','image/png','\x89504e47',now());
            INSERT INTO knowledge_sources ("Id","TenantId","BotId","Kind","Title","Status","CreatedAt","SourceUrl","SourceTable","SecretEncrypted") VALUES
              ('60000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoBot}}','Faq','Horarios','Indexed',now(),NULL,NULL,NULL),
              ('60000000-0000-0000-0000-000000000002','{{Porko}}','{{PorkoBot}}','Supabase','Menú','Indexed',now(),'https://abc.supabase.co','platos','{{s.Protect(SupabaseKey)}}');
            INSERT INTO knowledge_chunks ("Id","TenantId","BotId","SourceId","Text") VALUES
              ('61000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoBot}}','60000000-0000-0000-0000-000000000001','Pregunta: ¿Horario?\nRespuesta: De 12 a 22.');
            INSERT INTO data_connections ("Id","TenantId","BotId","Name","Url","SecretEncrypted","AllowedTools","IsActive","CreatedAt") VALUES
              ('62000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoBot}}','Pedidos','https://mcp.donporko.test','{{s.Protect("mcp-token")}}','buscar',true,now());
            INSERT INTO channels ("Id","TenantId","BotId","Kind","ExternalId","DisplayName","AccessTokenEncrypted","AppSecretEncrypted","Status","CreatedAt") VALUES
              ('63000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoBot}}','WhatsApp','phone123','Línea de pedidos','{{s.Protect(PorkoChannelToken)}}','{{s.Protect(PorkoAppSecret)}}','Active',now());
            INSERT INTO conversations ("Id","TenantId","BotId","Status","MessageCount","CreatedAt","LastMessageAt","ChannelId","ContactId","NeedsAttention","HumanActive") VALUES
              ('{{PorkoConversation}}','{{Porko}}','{{PorkoBot}}','Bot',2,now(),now(),'63000000-0000-0000-0000-000000000001','573001112233',false,false);
            INSERT INTO chat_messages ("Id","TenantId","ConversationId","Role","Content","TokensIn","TokensOut","CostMicros","CreatedAt") VALUES
              ('64000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoConversation}}','user','¿Horario?',0,0,0,now()),
              ('64000000-0000-0000-0000-000000000002','{{Porko}}','{{PorkoConversation}}','assistant','De 12 a 22.',900,80,130,now());
            INSERT INTO chat_attachments ("Id","TenantId","ConversationId","Kind","SizeBytes","Stored","CreatedAt","ExpiresAt") VALUES
              ('{{PorkoAttachment}}','{{Porko}}','{{PorkoConversation}}','image',3,true,now(),now() + interval '7 days');
            INSERT INTO leads ("Id","TenantId","BotId","ConversationId","Name","Phone","ConsentAt","CreatedAt") VALUES
              ('65000000-0000-0000-0000-000000000001','{{Porko}}','{{PorkoBot}}','{{PorkoConversation}}','Ana','3001112233',now(),now());
            INSERT INTO chat_usage ("TenantId","Period","Conversations","Messages","TokensIn","TokensOut","CostMicros","UpdatedAt","ConversationsWhatsApp") VALUES
              ('{{Porko}}',202610,40,120,50000,4000,9000,now(),25),('{{Porko}}',202611,12,30,9000,800,1300,now(),5);
            INSERT INTO users ("Id","TenantId","Email","PasswordHash","Role","IsActive","TotpEnabled","TotpSecretEncrypted","RecoveryCodeHashes","FailedLogins","CreatedAt") VALUES
              ('50000000-0000-0000-0000-000000000001','{{Agency}}','dueno@pixeldc.test','{{hash}}','Owner',true,false,NULL,'{}',0,now()),
              ('{{PorkoOwner}}','{{Porko}}','dueno@donporko.test','{{hash}}','Owner',true,true,'{{s.Protect(TotpSecret)}}','{abc,def}',0,now()),
              ('50000000-0000-0000-0000-000000000003','{{Porko}}','mesero@donporko.test','{{hash}}','Operator',true,false,NULL,'{}',0,now());
            """);

        Directory.CreateDirectory(Path.Combine(SourceAttachments, Porko.ToString("N")));
        await File.WriteAllBytesAsync(Path.Combine(SourceAttachments, Porko.ToString("N"), PorkoAttachment.ToString("N")), [1, 2, 3]);
    }

    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var admin = new NpgsqlConnection(_postgres.GetConnectionString());
        await admin.OpenAsync();
        await Exec(admin, $"CREATE DATABASE \"{name}\"");
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        try
        {
            Directory.Delete(SourceAttachments, recursive: true);
        }
        catch (IOException)
        {
            // Carpeta temporal: si no se puede borrar, la limpia el sistema.
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class ImporterTests(SourceFixture source) : IClassFixture<SourceFixture>
{
    private readonly string _targetAttachments = Path.Combine(Path.GetTempPath(), "px-mig-dst-" + Guid.NewGuid().ToString("N"));

    /// <summary>Una base de Pixela nueva, migrada, con su llave maestra.</summary>
    private async Task<ServiceProvider> TargetAsync()
    {
        var connection = await source.CreateDatabaseAsync("pixela_" + Guid.NewGuid().ToString("N")[..12]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connection,
            ["Secrets:MasterKey"] = SourceFixture.TargetKey,
        }).Build();
        var services = new ServiceCollection().AddLogging().AddPixelaInfrastructure(configuration).BuildServiceProvider();
        await DatabaseInitializer.InitializeAsync(services);
        return services;
    }

    private static async Task<ImportReport> RunAsync(ServiceProvider services, ImportOptions options)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        var importer = new FacilCorreoImporter(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<ISecretProtector>(), TimeProvider.System);
        return await importer.ImportAsync(options, CancellationToken.None);
    }

    private ImportOptions Options(string key = "", bool dryRun = false) =>
        new(source.SourceConnection, key.Length > 0 ? key : SourceFixture.SourceKey, ["Don Porko"], "Pixel DC", null, source.SourceAttachments, _targetAttachments, dryRun);

    private static async Task<T> WithDbAsync<T>(ServiceProvider services, Func<AppDbContext, ISecretProtector, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<ISecretProtector>());
    }

    [Fact]
    public async Task Migrates_everything_of_Don_Porko_and_Pixel_DC_with_secrets_reencrypted()
    {
        await using var services = await TargetAsync();

        var report = await RunAsync(services, Options());

        Assert.Equal(2, report.Counts["clientes"]);
        Assert.Equal(2, report.Counts["bots"]);
        await WithDbAsync(services, async (db, secrets) =>
        {
            // Cliente con su marca y su precio; sin tope = ilimitado. El otro cliente de la agencia no se toca.
            var porko = await db.Clients.SingleAsync(c => c.Id == SourceFixture.Porko);
            Assert.Equal(("don-porko", "#c0392b", 150m, (int?)null), (porko.Slug, porko.PrimaryColor, porko.PricePerConversation!.Value, porko.MonthlyConversationCap));
            Assert.Equal("Mandar", porko.Texts["send"]);
            Assert.False(await db.Clients.AnyAsync(c => c.Id == SourceFixture.Other));

            // El bot conserva id y clave pública (los widgets instalados siguen funcionando) y queda en un asset del cliente.
            var bot = await db.Bots.SingleAsync(b => b.Id == SourceFixture.PorkoBot);
            Assert.Equal("bt_donporko", bot.PublicKey);
            Assert.Equal(["https://donporko.test"], bot.AllowedOrigins);
            Assert.True(await db.Assets.AnyAsync(a => a.Id == bot.AssetId && a.ClientId == SourceFixture.Porko));

            // Secretos: descifrables con la llave de Pixela. Don Porko no tenía llave propia: hereda la de su agencia.
            var botSecrets = await db.BotSecrets.SingleAsync(s => s.BotId == SourceFixture.PorkoBot);
            Assert.Equal(SourceFixture.AgencyAiKey, secrets.Unprotect(botSecrets.ClaudeApiKeyEncrypted!));
            Assert.Equal(SourceFixture.PorkoAppSecret, secrets.Unprotect(botSecrets.MetaAppSecretEncrypted!));
            Assert.StartsWith("pxv_", secrets.Unprotect(botSecrets.MetaVerifyTokenEncrypted), StringComparison.Ordinal);
            var channel = await db.Channels.SingleAsync(c => c.BotId == SourceFixture.PorkoBot);
            Assert.Equal((SourceFixture.PorkoChannelToken, ChannelStatus.Active), (secrets.Unprotect(channel.AccessTokenEncrypted), channel.Status));
            var supabase = await db.KnowledgeSources.SingleAsync(k => k.Kind == KnowledgeKind.Supabase);
            Assert.Equal(SourceFixture.SupabaseKey, secrets.Unprotect(supabase.SecretEncrypted!));
            Assert.Equal("mcp-token", secrets.Unprotect((await db.DataConnections.SingleAsync()).SecretEncrypted!));

            // Conocimiento, conversaciones, mensajes, adjuntos, contactos e ícono.
            Assert.Contains("De 12 a 22", (await db.KnowledgeChunks.SingleAsync()).Text, StringComparison.Ordinal);
            Assert.Equal(2, await db.ChatMessages.CountAsync(m => m.ConversationId == SourceFixture.PorkoConversation));
            Assert.Equal("Ana", (await db.Leads.SingleAsync()).Name);
            Assert.True(await db.BotIcons.AnyAsync(i => i.BotId == SourceFixture.PorkoBot));
            Assert.True(File.Exists(Path.Combine(_targetAttachments, SourceFixture.Porko.ToString("N"), SourceFixture.PorkoAttachment.ToString("N"))));

            // Consumo por mes, ahora del bot.
            var usage = await db.ChatUsages.Where(u => u.BotId == SourceFixture.PorkoBot).OrderBy(u => u.Period).ToListAsync();
            Assert.Equal([(202610, 40, 25), (202611, 12, 5)], usage.Select(u => (u.Period, u.Conversations, u.ConversationsWhatsApp)));

            // Usuarios: el dueño de la agencia pasa al equipo de la agencia; los del cliente, a su cliente. Contraseña y 2FA se conservan.
            var agencyOwner = await db.Users.SingleAsync(u => u.Email == "dueno@pixeldc.test");
            Assert.Equal((UserRole.AgencyOwner, (Guid?)null), (agencyOwner.Role, agencyOwner.ClientId));
            var porkoOwner = await db.Users.SingleAsync(u => u.Id == SourceFixture.PorkoOwner);
            Assert.Equal((UserRole.ClientAdmin, (Guid?)SourceFixture.Porko, true), (porkoOwner.Role, porkoOwner.ClientId, porkoOwner.TotpEnabled));
            Assert.Equal(SourceFixture.TotpSecret, secrets.Unprotect(porkoOwner.TotpSecretEncrypted!));
            Assert.StartsWith("$argon2id$", porkoOwner.PasswordHash, StringComparison.Ordinal);
            Assert.Equal(UserRole.Operator, (await db.Users.SingleAsync(u => u.Email == "mesero@donporko.test")).Role);

            // La agencia también es un cliente, con su propio bot y su propia llave.
            var agencyBot = await db.BotSecrets.SingleAsync(s => s.BotId == SourceFixture.AgencyBot);
            Assert.Equal(SourceFixture.AgencyAiKey, secrets.Unprotect(agencyBot.ClaudeApiKeyEncrypted!));
            Assert.Equal(SourceFixture.Agency, (await db.Bots.SingleAsync(b => b.Id == SourceFixture.AgencyBot)).ClientId);
            return 0;
        });

        Assert.Contains(report.ToDo, t => t.Contains("códigos de recuperación", StringComparison.Ordinal));
        Assert.Contains(report.ToDo, t => t.Contains("webhook", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_account_is_never_migrated_twice()
    {
        await using var services = await TargetAsync();
        await RunAsync(services, Options());

        var error = await Assert.ThrowsAsync<ImportException>(() => RunAsync(services, Options()));

        Assert.Contains("ya fue migrado", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_reports_without_saving_anything()
    {
        await using var services = await TargetAsync();

        var report = await RunAsync(services, Options(dryRun: true));

        Assert.Equal(2, report.Counts["bots"]);
        Assert.False(await WithDbAsync(services, (db, _) => db.Clients.AnyAsync()));
        Assert.False(Directory.Exists(_targetAttachments));
    }

    [Fact]
    public async Task With_the_wrong_source_key_secrets_are_left_empty_and_channels_marked_for_a_new_token()
    {
        await using var services = await TargetAsync();

        var report = await RunAsync(services, Options(key: Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())));

        Assert.Contains(report.Warnings, w => w.Contains("llave maestra correcta", StringComparison.Ordinal));
        await WithDbAsync(services, async (db, _) =>
        {
            Assert.Null((await db.BotSecrets.SingleAsync(s => s.BotId == SourceFixture.PorkoBot)).ClaudeApiKeyEncrypted);
            Assert.Equal(ChannelStatus.TokenError, (await db.Channels.SingleAsync()).Status);
            return 0;
        });
    }

    [Fact]
    public async Task An_unknown_account_stops_before_writing()
    {
        await using var services = await TargetAsync();

        await Assert.ThrowsAsync<ImportException>(() => RunAsync(services, Options() with { Tenants = ["No existe"] }));
        Assert.False(await WithDbAsync(services, (db, _) => db.Clients.AnyAsync()));
    }
}
