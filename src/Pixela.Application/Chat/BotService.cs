using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

/// <summary>Los campos que no vienen (null) no se tocan; un texto vacío borra el valor opcional.</summary>
public sealed record BotRequest(
    string? Name,
    string? SystemPrompt,
    string? Tone,
    string? WelcomeMessage,
    string? Color,
    string? PrivacyUrl,
    List<string>? AllowedOrigins,
    bool? IsActive,
    string? Farewell = null,
    string? HandoffMessage = null,
    string? CallToAction = null,
    string? Address = null,
    string? Coordinates = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("whatsapp_number")] string? WhatsAppNumber = null,
    int? HandoffReturnHours = null);

public sealed record BotView(
    Guid Id, string PublicKey, string Name, string? SystemPrompt, string? Tone, string? WelcomeMessage, string? Color,
    string? PrivacyUrl, IReadOnlyList<string> AllowedOrigins, bool IsActive, DateTimeOffset CreatedAt,
    string? Farewell = null, string? HandoffMessage = null, string? CallToAction = null, string? Address = null, string? Coordinates = null, [property: System.Text.Json.Serialization.JsonPropertyName("whatsapp_number")] string? WhatsAppNumber = null,
    int HandoffReturnHours = ChatService.DefaultHandoffReturnHours, long? IconVersion = null, Guid? AssetId = null);

/// <param name="NeedingAttention">Conversaciones de canales de Meta que esperan a una persona del negocio (el bot no supo, o el contacto escribió mientras lo atendían).</param>
public sealed record BotList(IReadOnlyList<BotView> Bots, int NeedingAttention = 0);

/// <summary>Chatbots visibles para la petición (el aislamiento por cliente lo da el filtro global de EF Core).</summary>
public sealed partial class BotService(IAppDbContext db, ISecretProtector secrets, TimeProvider clock)
{
    private const int MaxOrigins = 20;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>Bots visibles; con <paramref name="assetId"/>, solo los de ese asset.</summary>
    public async Task<BotList> ListAsync(Guid? assetId, CancellationToken ct)
    {
        var query = db.Bots.AsNoTracking();
        if (assetId is { } asset)
        {
            query = query.Where(b => b.AssetId == asset);
        }

        var bots = await query.OrderBy(b => b.Name).ToListAsync(ct);
        var ids = bots.Select(b => b.Id).ToList();
        var attention = bots.Count == 0 ? 0 : await db.Conversations.CountAsync(c => c.NeedsAttention && ids.Contains(c.BotId), ct);
        return new BotList(bots.Select(ToView).ToList(), attention);
    }

    public async Task<ServiceResult<BotView>> GetAsync(Guid id, CancellationToken ct)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return bot is null ? ServiceResult<BotView>.Fail("not_found", "Chatbot no encontrado.") : ServiceResult<BotView>.Ok(ToView(bot));
    }

    public async Task<ServiceResult<bool>> SetIconAsync(Guid id, byte[] content, CancellationToken ct)
    {
        var (mime, error) = ImageUpload.Check(content);
        if (mime is null)
        {
            return ServiceResult<bool>.Fail("validation_error", error!, new Dictionary<string, string> { ["file"] = error! });
        }

        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bot is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Chatbot no encontrado.");
        }

        var now = clock.GetUtcNow();
        var icon = await db.BotIcons.FirstOrDefaultAsync(i => i.BotId == id, ct);
        if (icon is null)
        {
            db.BotIcons.Add(new BotIcon { BotId = id, ClientId = bot.ClientId, MimeType = mime, Content = content, UpdatedAt = now });
        }
        else
        {
            icon.MimeType = mime;
            icon.Content = content;
            icon.UpdatedAt = now;
        }

        bot.IconUpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> RemoveIconAsync(Guid id, CancellationToken ct)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bot is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Chatbot no encontrado.");
        }

        await db.BotIcons.Where(i => i.BotId == id).ExecuteDeleteAsync(ct);
        bot.IconUpdatedAt = null;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<StoredImage?> GetIconAsync(Guid id, CancellationToken ct) =>
        await db.BotIcons.AsNoTracking().Where(i => i.BotId == id).Select(i => new StoredImage(i.MimeType, i.Content, i.UpdatedAt)).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Crea un bot dentro de un asset (el cliente sale del asset). Nace con su token de verificación del webhook de Meta, distinto en cada bot,
    /// para que cada integración tenga el suyo sin que nadie lo invente.
    /// </summary>
    public async Task<ServiceResult<BotView>> CreateAsync(Guid assetId, BotRequest request, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return ServiceResult<BotView>.Fail("not_found", "Asset no encontrado.");
        }

        var now = clock.GetUtcNow();
        var bot = new Bot
        {
            Id = Guid.CreateVersion7(),
            ClientId = asset.ClientId,
            AssetId = asset.Id,
            PublicKey = NewPublicKey(),
            Name = "",
            CreatedAt = now,
        };
        if (Apply(bot, request, creating: true) is { } error)
        {
            return error;
        }

        db.Bots.Add(bot);
        db.BotSecrets.Add(new BotSecrets { BotId = bot.Id, ClientId = bot.ClientId, MetaVerifyTokenEncrypted = secrets.Protect(NewVerifyToken()), UpdatedAt = now });
        await db.SaveChangesAsync(ct);
        return ServiceResult<BotView>.Ok(ToView(bot));
    }

    public async Task<ServiceResult<BotView>> UpdateAsync(Guid id, BotRequest request, CancellationToken ct)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bot is null)
        {
            return ServiceResult<BotView>.Fail("not_found", "Chatbot no encontrado.");
        }

        if (Apply(bot, request, creating: false) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<BotView>.Ok(ToView(bot));
    }

    /// <summary>Borra el bot con todo lo que cuelga de él (conocimiento, conversaciones, mensajes y leads).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // Se comprueba con el filtro por cliente antes de borrar por SQL, que no lo aplica.
        if (!await db.Bots.AnyAsync(b => b.Id == id, ct))
        {
            return false;
        }

        await db.DeleteBotCascadeAsync(id, ct);
        return true;
    }

    private static ServiceResult<BotView>? Apply(Bot bot, BotRequest r, bool creating)
    {
        var fields = new Dictionary<string, string>();

        if (r.Name is not null || creating)
        {
            var name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200)
            {
                fields["name"] = "El nombre es obligatorio (máximo 200 caracteres).";
            }
            else
            {
                bot.Name = name;
            }
        }

        Text(fields, "system_prompt", r.SystemPrompt, 4000, v => bot.SystemPrompt = v);
        Text(fields, "tone", r.Tone, 200, v => bot.Tone = v);
        Text(fields, "welcome_message", r.WelcomeMessage, 500, v => bot.WelcomeMessage = v);
        Text(fields, "farewell", r.Farewell, 500, v => bot.Farewell = v);
        Text(fields, "handoff_message", r.HandoffMessage, 500, v => bot.HandoffMessage = v);
        Text(fields, "call_to_action", r.CallToAction, 500, v => bot.CallToAction = v);
        Text(fields, "address", r.Address, 300, v => bot.Address = v);

        if (r.HandoffReturnHours is { } hours)
        {
            if (hours is < 0 or > 24)
            {
                fields["handoff_return_hours"] = "Entre 0 y 24 horas (0 = que solo una persona la devuelva).";
            }
            else
            {
                bot.HandoffReturnHours = hours;
            }
        }

        if (r.Coordinates is not null)
        {
            var raw = r.Coordinates.Trim();
            if (raw.Length == 0)
            {
                bot.Latitude = null;
                bot.Longitude = null;
            }
            else if (BotProfile.TryParseCoordinates(raw, out var lat, out var lng))
            {
                bot.Latitude = lat;
                bot.Longitude = lng;
            }
            else
            {
                fields["coordinates"] = "Escribe latitud y longitud separadas por coma, por ejemplo 4.4683965, -75.2745043.";
            }
        }

        if (r.WhatsAppNumber is not null)
        {
            var digits = new string(r.WhatsAppNumber.Where(char.IsDigit).ToArray());
            if (r.WhatsAppNumber.Trim().Length == 0)
            {
                bot.WhatsAppNumber = null;
            }
            else if (digits.Length is < 8 or > 15)
            {
                fields["whatsapp_number"] = "Escribe el número con el indicativo del país, por ejemplo 573142979775.";
            }
            else
            {
                bot.WhatsAppNumber = digits;
            }
        }

        if (r.Color is not null)
        {
            var color = r.Color.Trim();
            if (color.Length == 0)
            {
                bot.Color = null;
            }
            else if (!ColorPattern().IsMatch(color))
            {
                fields["color"] = "Usa el formato #rrggbb.";
            }
            else
            {
                bot.Color = color.ToLowerInvariant();
            }
        }

        if (r.PrivacyUrl is not null)
        {
            var url = r.PrivacyUrl.Trim();
            if (url.Length == 0)
            {
                bot.PrivacyUrl = null;
            }
            else if (url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                fields["privacy_url"] = "Indica una dirección http(s) válida.";
            }
            else
            {
                bot.PrivacyUrl = url;
            }
        }

        if (r.AllowedOrigins is not null)
        {
            var origins = new List<string>();
            foreach (var raw in r.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                if (NormalizeOrigin(raw) is { } origin)
                {
                    if (!origins.Contains(origin))
                    {
                        origins.Add(origin);
                    }
                }
                else
                {
                    fields["allowed_origins"] = $"“{raw.Trim()}” no es un origen válido (por ejemplo https://minegocio.com).";
                    break;
                }
            }

            if (origins.Count > MaxOrigins)
            {
                fields["allowed_origins"] = $"Máximo {MaxOrigins} sitios.";
            }

            if (!fields.ContainsKey("allowed_origins"))
            {
                bot.AllowedOrigins = origins;
            }
        }

        if (r.IsActive is { } active)
        {
            bot.IsActive = active;
        }

        return fields.Count == 0 ? null : ServiceResult<BotView>.Fail("validation_error", fields.Values.First(), fields);
    }

    private static void Text(Dictionary<string, string> fields, string name, string? value, int max, Action<string?> set)
    {
        if (value is null)
        {
            return;
        }

        var text = value.Trim();
        if (text.Length > max)
        {
            fields[name] = $"Máximo {max} caracteres.";
            return;
        }

        set(text.Length == 0 ? null : text);
    }

    /// <summary>Deja solo esquema + host + puerto en minúsculas (sin ruta, consulta ni barra final); null si no es un origen.</summary>
    public static string? NormalizeOrigin(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath.Length > 1)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static string NewPublicKey() =>
        "bt_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Token de verificación del webhook de Meta: 32 bytes aleatorios en base64url (Meta lo acepta tal cual).</summary>
    public static string NewVerifyToken() =>
        "pxv_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static BotView ToView(Bot b) =>
        new(b.Id, b.PublicKey, b.Name, b.SystemPrompt, b.Tone, b.WelcomeMessage, b.Color, b.PrivacyUrl, b.AllowedOrigins, b.IsActive, b.CreatedAt,
            b.Farewell, b.HandoffMessage, b.CallToAction, b.Address, BotProfile.CoordinatesText(b), b.WhatsAppNumber, b.HandoffReturnHours ?? ChatService.DefaultHandoffReturnHours, b.IconUpdatedAt?.UtcTicks, b.AssetId);
}
