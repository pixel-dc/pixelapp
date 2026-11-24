using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Application.Clients;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;
using Pixela.Domain.Users;
using Pixela.Infrastructure.Persistence;
using Pixela.Infrastructure.Security;

namespace Pixela.Migrator;

/// <summary>Qué migrar y desde dónde.</summary>
/// <param name="SourceConnection">Base de FacilCorreo (basta un usuario de solo lectura).</param>
/// <param name="SourceMasterKey">Llave maestra de FacilCorreo (<c>Secrets__MasterKey</c> de su servidor): descifra los secretos para volver a cifrarlos con la de Pixela.</param>
/// <param name="Tenants">Cuentas de FacilCorreo a migrar: id, slug o nombre exacto.</param>
/// <param name="AgencyTenant">Opcional: cuenta de FacilCorreo que es la propia agencia (Pixel DC). Sus dueños y administradores pasan al equipo de la agencia.</param>
/// <param name="SourceMetaAppSecret">Opcional: clave secreta de la app de Meta global de FacilCorreo, para los bots cuyos canales usaban esa app.</param>
/// <param name="SourceAttachments">Carpeta de adjuntos de FacilCorreo (<c>Meta__AttachmentsPath</c>); null = no se copian archivos.</param>
/// <param name="TargetAttachments">Carpeta de adjuntos de Pixela.</param>
/// <param name="DryRun">Hace todo dentro de una transacción y la deshace: sirve para ver el informe sin tocar nada.</param>
public sealed record ImportOptions(
    string SourceConnection,
    string SourceMasterKey,
    IReadOnlyList<string> Tenants,
    string? AgencyTenant = null,
    string? SourceMetaAppSecret = null,
    string? SourceAttachments = null,
    string? TargetAttachments = null,
    bool DryRun = false);

/// <summary>Resultado: cuánto se migró y lo que alguien debe hacer a mano después.</summary>
public sealed class ImportReport
{
    public Dictionary<string, int> Counts { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> ToDo { get; } = [];

    public void Add(string what, int n = 1) => Counts[what] = Counts.GetValueOrDefault(what) + n;
}

/// <summary>
/// Copia de FacilCorreo a Pixela todo lo de unas cuentas concretas (Don Porko y Pixel DC): cliente, asset, bots con su conocimiento, conexiones,
/// canales, conversaciones, mensajes, adjuntos, contactos, consumo y usuarios. Conserva los ids y las claves públicas de los bots (los widgets ya
/// instalados siguen funcionando al cambiar el dominio) y vuelve a cifrar cada secreto con la llave maestra de Pixela. Nunca escribe en FacilCorreo.
/// </summary>
public sealed class FacilCorreoImporter(AppDbContext db, ISecretProtector target, TimeProvider clock)
{
    public async Task<ImportReport> ImportAsync(ImportOptions options, CancellationToken ct)
    {
        var report = new ImportReport();
        var source = new AesGcmSecretProtector(Options.Create(new SecretsOptions { MasterKey = options.SourceMasterKey }));
        await using var src = new NpgsqlConnection(options.SourceConnection);
        await src.OpenAsync(ct);
        // La fuente solo se lee: una transacción de solo lectura lo garantiza aunque el usuario tuviera más permisos.
        await using var readOnly = await src.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Exec(src, "SET TRANSACTION READ ONLY", ct);

        var tenants = new List<SourceTenant>();
        foreach (var key in options.Tenants.Concat(options.AgencyTenant is null ? [] : [options.AgencyTenant]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var t = await FindTenantAsync(src, key, ct) ?? throw new ImportException($"No existe en FacilCorreo una cuenta con id, slug o nombre «{key}».");
            if (tenants.All(x => x.Id != t.Id))
            {
                tenants.Add(t);
            }
        }

        var agencyId = options.AgencyTenant is null ? (Guid?)null : tenants.First(t => Matches(t, options.AgencyTenant)).Id;
        var files = new List<(Guid From, Guid To, Guid Id)>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var t in tenants)
        {
            if (await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Id == t.Id, ct))
            {
                throw new ImportException($"«{t.Name}» ya fue migrado (existe el cliente {t.Id}). No se migra dos veces.");
            }

            await ImportTenantAsync(src, source, t, t.Id == agencyId, options, report, files, ct);
        }

        if (options.DryRun)
        {
            await tx.RollbackAsync(ct);
            report.Warnings.Add("Simulación: no se guardó nada.");
            return report;
        }

        await tx.CommitAsync(ct);
        CopyFiles(options, files, report);
        return report;
    }

    private async Task ImportTenantAsync(
        NpgsqlConnection src, AesGcmSecretProtector source, SourceTenant t, bool isAgency, ImportOptions options, ImportReport report, List<(Guid, Guid, Guid)> files, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var clientId = t.Id;

        // Cliente: marca del widget y precio/tope que tenía en FacilCorreo; lo demás queda ilimitado.
        var brand = await One(src, "SELECT \"LogoUrl\", \"PrimaryColor\", \"Texts\"::text FROM agency_brand WHERE \"AgencyId\" = @id", r => (Logo: r.Str(0), Color: r.Str(1), Texts: r.Str(2)), ct, ("id", t.Id));
        var billing = await One(src, "SELECT \"PricePerConversation\", \"MonthlyConversationCap\" FROM client_chat_settings WHERE \"ClientId\" = @id", r => (Price: r.Dec(0), Cap: r.Int(1)), ct, ("id", t.Id));
        var slug = string.IsNullOrWhiteSpace(t.Slug) ? ClientService.Slugify(t.Name) : t.Slug!;
        if (await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Slug == slug, ct))
        {
            slug = $"{slug}-{t.Id.ToString("N")[..6]}";
        }

        db.Clients.Add(new Client
        {
            Id = clientId,
            Name = t.Name,
            Slug = slug,
            Status = t.Status is "Suspended" or "Closed" ? ClientStatus.Suspended : ClientStatus.Active,
            LogoUrl = brand.Logo,
            PrimaryColor = brand.Color,
            Texts = brand.Texts is { Length: > 2 } json ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [],
            PricePerConversation = billing.Price,
            MonthlyConversationCap = billing.Cap,
            CreatedAt = t.CreatedAt,
        });
        var asset = new Asset { Id = Guid.CreateVersion7(), ClientId = clientId, Name = t.Name, Description = "Migrado de FacilCorreo", CreatedAt = now };
        db.Assets.Add(asset);
        report.Add("clientes");
        report.Add("assets");

        // Llave de Claude: la de la cuenta o, si no tenía, la de su agencia. Pasa a cada bot (en Pixela la llave es por bot).
        var aiKey = Decrypt(source, t.AiKeyEncrypted, $"llave de Claude de {t.Name}", report)
            ?? (t.ParentId is { } parent
                ? Decrypt(source, await Scalar<string>(src, "SELECT \"AiKeyEncrypted\" FROM tenants WHERE \"Id\" = @id", ct, ("id", parent)), $"llave de Claude de la agencia de {t.Name}", report)
                : null);

        var bots = await Many(src, "SELECT * FROM bots WHERE \"TenantId\" = @id ORDER BY \"CreatedAt\"", r => r.Row(), ct, ("id", t.Id));
        var channelSecrets = await Many(src, "SELECT \"BotId\", \"AppSecretEncrypted\" FROM channels WHERE \"TenantId\" = @id", r => (Bot: r.Guid(0), Secret: r.Str(1)), ct, ("id", t.Id));
        foreach (var b in bots)
        {
            var botId = b.Guid("Id");
            db.Bots.Add(new Bot
            {
                Id = botId,
                ClientId = clientId,
                AssetId = asset.Id,
                PublicKey = b.Str("PublicKey")!,
                Name = b.Str("Name")!,
                SystemPrompt = b.Str("SystemPrompt"),
                Tone = b.Str("Tone"),
                WelcomeMessage = b.Str("WelcomeMessage"),
                Farewell = b.Str("Farewell"),
                HandoffMessage = b.Str("HandoffMessage"),
                HandoffReturnHours = b.Int("HandoffReturnHours"),
                IconUpdatedAt = b.Time("IconUpdatedAt"),
                BusinessName = b.Str("BusinessName"),
                QuickGreeting = b.Str("QuickGreeting"),
                QuickFarewell = b.Str("QuickFarewell"),
                QuickClosing = b.Str("QuickClosing"),
                CallToAction = b.Str("CallToAction"),
                Address = b.Str("Address"),
                Latitude = b.Dbl("Latitude"),
                Longitude = b.Dbl("Longitude"),
                WhatsAppNumber = b.Str("WhatsAppNumber"),
                Color = b.Str("Color"),
                PrivacyUrl = b.Str("PrivacyUrl"),
                AllowedOrigins = b.Strings("AllowedOrigins"),
                Model = b.Str("Model"),
                IsActive = b.Bool("IsActive"),
                CreatedAt = b.Time("CreatedAt")!.Value,
            });

            // App de Meta: la clave propia de sus canales (si todos usan la misma) o la global de FacilCorreo.
            var secrets = channelSecrets.Where(c => c.Bot == botId).Select(c => Decrypt(source, c.Secret, "clave de la app de Meta de un canal", report)).ToList();
            var own = secrets.Where(s => s is not null).Distinct().ToList();
            var appSecret = own.Count == 1 ? own[0] : null;
            if (own.Count > 1)
            {
                report.Warnings.Add($"El bot «{b.Str("Name")}» tenía canales con apps de Meta distintas; en Pixela un bot usa una sola app. Pon la clave en Integraciones.");
            }
            else if (appSecret is null && secrets.Count > 0)
            {
                appSecret = string.IsNullOrWhiteSpace(options.SourceMetaAppSecret) ? null : options.SourceMetaAppSecret;
            }

            db.BotSecrets.Add(new BotSecrets
            {
                BotId = botId,
                ClientId = clientId,
                ClaudeApiKeyEncrypted = aiKey is null ? null : target.Protect(aiKey),
                ClaudeApiKeyHint = aiKey is { Length: >= 4 } ? aiKey[^4..] : null,
                ClaudeApiKeySetAt = aiKey is null ? null : now,
                MetaAppSecretEncrypted = appSecret is null ? null : target.Protect(appSecret),
                MetaAppSecretHint = appSecret is { Length: >= 4 } ? appSecret[^4..] : null,
                MetaVerifyTokenEncrypted = target.Protect(BotService.NewVerifyToken()),
                UpdatedAt = now,
            });
            report.Add("bots");
            if (aiKey is null)
            {
                report.ToDo.Add($"«{b.Str("Name")}»: no tenía llave de Claude. Crea una para este bot en Anthropic y ponla en Integraciones.");
            }
            else
            {
                report.ToDo.Add($"«{b.Str("Name")}»: quedó con la llave de Claude de la cuenta en FacilCorreo. Para auditar su consumo aparte, crea una llave propia del bot en Anthropic y cámbiala en Integraciones.");
            }

            if (secrets.Count > 0)
            {
                report.ToDo.Add(appSecret is null
                    ? $"«{b.Str("Name")}»: tiene canales de Meta pero ninguna clave de app. Pon la clave secreta de su app en Integraciones; sin ella no recibe mensajes."
                    : $"«{b.Str("Name")}»: en su app de Meta cambia la URL del webhook y el token de verificación por los de Pixela (pestaña Integraciones).");
            }
        }

        await CopyIconsAsync(src, t, clientId, report, ct);
        await CopyKnowledgeAsync(src, source, t, clientId, report, ct);
        await CopyConnectionsAndChannelsAsync(src, source, t, clientId, report, ct);
        await CopyConversationsAsync(src, t, clientId, report, files, ct);
        await CopyUsageAsync(src, t, clientId, bots.Select(b => b.Guid("Id")).ToList(), report, ct);
        await CopyUsersAsync(src, source, t, clientId, isAgency, report, ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task CopyIconsAsync(NpgsqlConnection src, SourceTenant t, Guid clientId, ImportReport report, CancellationToken ct)
    {
        foreach (var i in await Many(src, "SELECT \"BotId\", \"MimeType\", \"Content\", \"UpdatedAt\" FROM bot_icons WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            db.BotIcons.Add(new BotIcon { BotId = i.Guid("BotId"), ClientId = clientId, MimeType = i.Str("MimeType")!, Content = (byte[])i["Content"]!, UpdatedAt = i.Time("UpdatedAt")!.Value });
            report.Add("íconos");
        }
    }

    private async Task CopyKnowledgeAsync(NpgsqlConnection src, AesGcmSecretProtector source, SourceTenant t, Guid clientId, ImportReport report, CancellationToken ct)
    {
        foreach (var s in await Many(src, "SELECT * FROM knowledge_sources WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            var secret = Decrypt(source, s.Str("SecretEncrypted"), $"llave de Supabase de «{s.Str("Title")}»", report);
            db.KnowledgeSources.Add(new KnowledgeSource
            {
                Id = s.Guid("Id"),
                ClientId = clientId,
                BotId = s.Guid("BotId"),
                Kind = Enum.Parse<KnowledgeKind>(s.Str("Kind")!),
                Title = s.Str("Title")!,
                SourceUrl = s.Str("SourceUrl"),
                SourceTable = s.Str("SourceTable"),
                SourceColumns = s.Str("SourceColumns"),
                SecretEncrypted = secret is null ? null : target.Protect(secret),
                LinkTemplate = s.Str("LinkTemplate"),
                ImageColumn = s.Str("ImageColumn"),
                RefreshMinutes = s.Int("RefreshMinutes"),
                SyncedAt = s.Time("SyncedAt"),
                Content = s.Str("Content"),
                Status = Enum.Parse<KnowledgeStatus>(s.Str("Status")!),
                Error = s.Str("Error"),
                CreatedAt = s.Time("CreatedAt")!.Value,
            });
            report.Add("fuentes de conocimiento");
        }

        foreach (var c in await Many(src, "SELECT \"Id\", \"BotId\", \"SourceId\", \"Text\" FROM knowledge_chunks WHERE \"TenantId\" = @id", r => (Id: r.Guid(0), Bot: r.Guid(1), Source: r.Guid(2), Text: r.Str(3)!), ct, ("id", t.Id)))
        {
            db.KnowledgeChunks.Add(new KnowledgeChunk { Id = c.Id, ClientId = clientId, BotId = c.Bot, SourceId = c.Source, Text = c.Text });
            report.Add("fragmentos de conocimiento");
        }
    }

    private async Task CopyConnectionsAndChannelsAsync(NpgsqlConnection src, AesGcmSecretProtector source, SourceTenant t, Guid clientId, ImportReport report, CancellationToken ct)
    {
        foreach (var c in await Many(src, "SELECT * FROM data_connections WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            var secret = Decrypt(source, c.Str("SecretEncrypted"), $"token de la conexión «{c.Str("Name")}»", report);
            db.DataConnections.Add(new DataConnection
            {
                Id = c.Guid("Id"),
                ClientId = clientId,
                BotId = c.Guid("BotId"),
                Name = c.Str("Name")!,
                Url = c.Str("Url")!,
                SecretEncrypted = secret is null ? null : target.Protect(secret),
                AllowedTools = c.Str("AllowedTools") ?? "",
                IsActive = c.Bool("IsActive"),
                LastError = c.Str("LastError"),
                CreatedAt = c.Time("CreatedAt")!.Value,
            });
            report.Add("conexiones de datos");
        }

        foreach (var c in await Many(src, "SELECT * FROM channels WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            var token = Decrypt(source, c.Str("AccessTokenEncrypted"), $"token del canal «{c.Str("DisplayName")}»", report);
            if (token is null)
            {
                report.Warnings.Add($"El canal «{c.Str("DisplayName")}» no tenía un token legible; se migró como «Token rechazado» para que pongas uno nuevo.");
            }

            db.Channels.Add(new Channel
            {
                Id = c.Guid("Id"),
                ClientId = clientId,
                BotId = c.Guid("BotId"),
                Kind = Enum.Parse<ChannelKind>(c.Str("Kind")!),
                ExternalId = c.Str("ExternalId")!,
                DisplayName = c.Str("DisplayName")!,
                AccessTokenEncrypted = target.Protect(token ?? "ilegible"),
                Status = token is null ? ChannelStatus.TokenError : Enum.Parse<ChannelStatus>(c.Str("Status")!),
                CreatedAt = c.Time("CreatedAt")!.Value,
            });
            report.Add("canales");
        }
    }

    private async Task CopyConversationsAsync(NpgsqlConnection src, SourceTenant t, Guid clientId, ImportReport report, List<(Guid, Guid, Guid)> files, CancellationToken ct)
    {
        foreach (var c in await Many(src, "SELECT * FROM conversations WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            db.Conversations.Add(new Conversation
            {
                Id = c.Guid("Id"),
                ClientId = clientId,
                BotId = c.Guid("BotId"),
                VisitorId = c.Str("VisitorId"),
                Status = Enum.Parse<ConversationStatus>(c.Str("Status")!),
                Origin = c.Str("Origin"),
                ChannelId = c.NGuid("ChannelId"),
                ContactId = c.Str("ContactId"),
                ContactName = c.Str("ContactName"),
                NeedsAttention = c.Bool("NeedsAttention"),
                HumanActive = c.Bool("HumanActive"),
                HumanActivityAt = c.Time("HumanActivityAt"),
                MessageCount = c.Int("MessageCount") ?? 0,
                CreatedAt = c.Time("CreatedAt")!.Value,
                LastMessageAt = c.Time("LastMessageAt")!.Value,
            });
            report.Add("conversaciones");
        }

        foreach (var m in await Many(src, "SELECT * FROM chat_messages WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = m.Guid("Id"),
                ClientId = clientId,
                ConversationId = m.Guid("ConversationId"),
                Role = m.Str("Role")!,
                Content = m.Str("Content")!,
                TokensIn = m.Int("TokensIn") ?? 0,
                TokensOut = m.Int("TokensOut") ?? 0,
                CostMicros = m.Long("CostMicros") ?? 0,
                Model = m.Str("Model"),
                CreatedAt = m.Time("CreatedAt")!.Value,
            });
            report.Add("mensajes");
        }

        foreach (var a in await Many(src, "SELECT * FROM chat_attachments WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            var id = a.Guid("Id");
            db.ChatAttachments.Add(new ChatAttachment
            {
                Id = id,
                ClientId = clientId,
                ConversationId = a.Guid("ConversationId"),
                MessageId = a.NGuid("MessageId"),
                Kind = a.Str("Kind")!,
                MimeType = a.Str("MimeType"),
                FileName = a.Str("FileName"),
                SizeBytes = a.Long("SizeBytes") ?? 0,
                Stored = a.Bool("Stored"),
                CreatedAt = a.Time("CreatedAt")!.Value,
                ExpiresAt = a.Time("ExpiresAt")!.Value,
            });
            if (a.Bool("Stored"))
            {
                files.Add((t.Id, clientId, id));
            }

            report.Add("adjuntos");
        }

        foreach (var l in await Many(src, "SELECT * FROM leads WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            db.Leads.Add(new Lead
            {
                Id = l.Guid("Id"),
                ClientId = clientId,
                BotId = l.Guid("BotId"),
                ConversationId = l.NGuid("ConversationId"),
                Name = l.Str("Name")!,
                Email = l.Str("Email"),
                Phone = l.Str("Phone"),
                Interest = l.Str("Interest"),
                ConsentAt = l.Time("ConsentAt")!.Value,
                CreatedAt = l.Time("CreatedAt")!.Value,
            });
            report.Add("contactos");
        }
    }

    /// <summary>
    /// FacilCorreo guardaba el consumo por cuenta y mes; Pixela lo guarda por bot. Con un solo bot pasa entero a él; con varios, a su bot más antiguo
    /// (los mensajes de meses viejos ya se purgaron y no permiten repartirlo con exactitud) y queda anotado en el informe.
    /// </summary>
    private async Task CopyUsageAsync(NpgsqlConnection src, SourceTenant t, Guid clientId, List<Guid> bots, ImportReport report, CancellationToken ct)
    {
        var rows = await Many(src, "SELECT * FROM chat_usage WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id));
        if (rows.Count == 0)
        {
            return;
        }

        if (bots.Count == 0)
        {
            report.Warnings.Add($"«{t.Name}» tenía consumo pero ningún bot: el consumo histórico no se migró.");
            return;
        }

        foreach (var u in rows)
        {
            db.ChatUsages.Add(new ChatUsage
            {
                BotId = bots[0],
                ClientId = clientId,
                Period = u.Int("Period")!.Value,
                Conversations = u.Int("Conversations") ?? 0,
                ConversationsWhatsApp = u.Int("ConversationsWhatsApp") ?? 0,
                ConversationsMessenger = u.Int("ConversationsMessenger") ?? 0,
                ConversationsInstagram = u.Int("ConversationsInstagram") ?? 0,
                WhatsAppMessagesSent = u.Int("WhatsAppMessagesSent") ?? 0,
                Messages = u.Int("Messages") ?? 0,
                TokensIn = u.Long("TokensIn") ?? 0,
                TokensOut = u.Long("TokensOut") ?? 0,
                CostMicros = u.Long("CostMicros") ?? 0,
                UpdatedAt = u.Time("UpdatedAt")!.Value,
            });
            report.Add("meses de consumo");
        }

        if (bots.Count > 1)
        {
            report.Warnings.Add($"«{t.Name}» tenía {bots.Count} bots: su consumo histórico ({rows.Count} meses) quedó en el más antiguo, porque FacilCorreo no lo separaba por bot.");
        }
    }

    /// <summary>
    /// Usuarios: conservan su contraseña (mismo formato Argon2id) y su 2FA (el secreto se vuelve a cifrar). Los códigos de recuperación no se pueden
    /// conservar (dependían de una llave de FacilCorreo): cada quien genera nuevos desactivando y activando su 2FA.
    /// </summary>
    private async Task CopyUsersAsync(NpgsqlConnection src, AesGcmSecretProtector source, SourceTenant t, Guid clientId, bool isAgency, ImportReport report, CancellationToken ct)
    {
        foreach (var u in await Many(src, "SELECT * FROM users WHERE \"TenantId\" = @id", r => r.Row(), ct, ("id", t.Id)))
        {
            var email = u.Str("Email")!;
            var role = (u.Str("Role"), isAgency) switch
            {
                ("SuperAdmin", _) => (UserRole?)null,
                ("Owner", true) => UserRole.AgencyOwner,
                ("Admin", true) => UserRole.AgencyAdmin,
                ("Owner" or "Admin", false) => UserRole.ClientAdmin,
                _ => UserRole.Operator,
            };
            if (role is null)
            {
                continue;
            }

            if (await db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == email, ct) || db.Users.Local.Any(x => x.Email == email))
            {
                report.Warnings.Add($"{email} ya tiene usuario en Pixela: no se migró ese usuario.");
                continue;
            }

            var totp = Decrypt(source, u.Str("TotpSecretEncrypted"), $"segundo factor de {email}", report);
            var userId = u.Guid("Id");
            db.Users.Add(new User
            {
                Id = userId,
                ClientId = role.Value.IsAgency() ? null : clientId,
                Email = email,
                PasswordHash = u.Str("PasswordHash")!,
                Role = role.Value,
                IsActive = u.Bool("IsActive"),
                TotpSecretEncrypted = totp is null ? null : target.Protect(totp),
                TotpEnabled = totp is not null && u.Bool("TotpEnabled"),
                TotpLastStep = u.Long("TotpLastStep"),
                CreatedAt = u.Time("CreatedAt")!.Value,
                LastLoginAt = u.Time("LastLoginAt"),
                PasswordChangedAt = u.Time("PasswordChangedAt"),
                DisplayName = u.Str("DisplayName"),
                PreferencesJson = u.Str("PreferencesJson"),
                AvatarUpdatedAt = u.Time("AvatarUpdatedAt"),
            });
            report.Add("usuarios");
            if (u.Bool("TotpEnabled") && u.Strings("RecoveryCodeHashes").Count > 0)
            {
                report.ToDo.Add($"{email}: conserva su verificación en dos pasos, pero sus códigos de recuperación no se migran. Que genere nuevos (desactivar y activar el 2FA en Mi cuenta).");
            }

            if (await One(src, "SELECT \"MimeType\", \"Content\", \"UpdatedAt\" FROM user_avatars WHERE \"UserId\" = @id", r => (Mime: r.Str(0), Content: r.IsDBNull(1) ? null : (byte[])r[1], At: r.Time(2)), ct, ("id", userId)) is { Mime: { } mime, Content: { } content, At: { } at })
            {
                db.UserAvatars.Add(new UserAvatar { UserId = userId, MimeType = mime, Content = content, UpdatedAt = at });
            }
        }
    }

    private static void CopyFiles(ImportOptions options, List<(Guid From, Guid To, Guid Id)> files, ImportReport report)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SourceAttachments) || string.IsNullOrWhiteSpace(options.TargetAttachments))
        {
            report.ToDo.Add($"Hay {files.Count} archivos de adjuntos por copiar: vuelve a indicar las carpetas de origen y destino, o cópialos a mano (<origen>/<cuenta>/<id> → <destino>/<cliente>/<id>).");
            return;
        }

        foreach (var (from, to, id) in files)
        {
            var origin = Path.Combine(options.SourceAttachments, from.ToString("N"), id.ToString("N"));
            var destination = Path.Combine(options.TargetAttachments, to.ToString("N"), id.ToString("N"));
            if (!File.Exists(origin))
            {
                report.Add("archivos ausentes en el origen");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(origin, destination, overwrite: false);
            report.Add("archivos copiados");
        }
    }

    private static string? Decrypt(AesGcmSecretProtector source, string? encrypted, string what, ImportReport report)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return null;
        }

        try
        {
            return source.Unprotect(encrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            report.Warnings.Add($"No se pudo descifrar {what} (¿es la llave maestra correcta de FacilCorreo?). Quedó vacío.");
            return null;
        }
    }

    private static bool Matches(SourceTenant t, string key) =>
        t.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Slug, key, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(key, StringComparison.OrdinalIgnoreCase);

    private static async Task<SourceTenant?> FindTenantAsync(NpgsqlConnection src, string key, CancellationToken ct)
    {
        var all = await Many(src, "SELECT \"Id\", \"Name\", \"Slug\", \"Status\", \"ParentId\", \"AiKeyEncrypted\", \"CreatedAt\" FROM tenants", r =>
            new SourceTenant(r.Guid(0), r.Str(1)!, r.Str(2), r.Str(3)!, r.NGuid(4), r.Str(5), r.Time(6)!.Value), ct);
        var found = all.Where(t => Matches(t, key)).ToList();
        return found.Count > 1 ? throw new ImportException($"«{key}» coincide con varias cuentas de FacilCorreo; usa su id.") : found.SingleOrDefault();
    }

    private sealed record SourceTenant(Guid Id, string Name, string? Slug, string Status, Guid? ParentId, string? AiKeyEncrypted, DateTimeOffset CreatedAt);

    private static async Task Exec(NpgsqlConnection src, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, src);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> Scalar<T>(NpgsqlConnection src, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(src, sql, args);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<T> One<T>(NpgsqlConnection src, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Name, object Value)[] args)
    {
        var rows = await Many(src, sql, map, ct, args);
        return rows.Count > 0 ? rows[0] : default!;
    }

    private static async Task<List<T>> Many<T>(NpgsqlConnection src, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(src, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }

        return list;
    }

    private static NpgsqlCommand Command(NpgsqlConnection src, string sql, (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, src);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }
}

public sealed class ImportException(string message) : Exception(message);

/// <summary>Lectura cómoda de filas de la base de origen (los nulos llegan como null).</summary>
internal static class Reading
{
    public static Dictionary<string, object?> Row(this DbDataReader r)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < r.FieldCount; i++)
        {
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }

        return row;
    }

    // Columnas que el FacilCorreo desplegado aún no tiene (esquema más antiguo) se leen como nulas.
    private static object? Get(Dictionary<string, object?> row, string c) => row.TryGetValue(c, out var v) ? v : null;

    public static string? Str(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static Guid Guid(this DbDataReader r, int i) => r.GetGuid(i);

    public static Guid? NGuid(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static int? Int(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static decimal? Dec(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);

    public static DateTimeOffset? Time(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public static string? Str(this Dictionary<string, object?> row, string c) => Get(row, c) as string;

    public static Guid Guid(this Dictionary<string, object?> row, string c) => (Guid)Get(row, c)!;

    public static Guid? NGuid(this Dictionary<string, object?> row, string c) => Get(row, c) as Guid?;

    public static int? Int(this Dictionary<string, object?> row, string c) => Get(row, c) is null ? null : Convert.ToInt32(Get(row, c), System.Globalization.CultureInfo.InvariantCulture);

    public static long? Long(this Dictionary<string, object?> row, string c) => Get(row, c) is null ? null : Convert.ToInt64(Get(row, c), System.Globalization.CultureInfo.InvariantCulture);

    public static double? Dbl(this Dictionary<string, object?> row, string c) => Get(row, c) as double?;

    public static bool Bool(this Dictionary<string, object?> row, string c) => Get(row, c) is true;

    public static DateTimeOffset? Time(this Dictionary<string, object?> row, string c) => Get(row, c) switch
    {
        null => null,
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        var other => throw new InvalidCastException($"{c}: {other.GetType()}"),
    };

    public static List<string> Strings(this Dictionary<string, object?> row, string c) => Get(row, c) is string[] a ? [.. a] : [];
}
