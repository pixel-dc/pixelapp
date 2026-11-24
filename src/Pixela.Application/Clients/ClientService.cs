using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Pixela.Domain.Clients;

namespace Pixela.Application.Clients;

/// <summary>Los campos que no vienen (null) no se tocan. En topes y precio, 0 o un texto vacío vuelven a «ilimitado / sin cobro».</summary>
public sealed record ClientRequest(
    string? Name,
    string? Slug,
    string? Status = null,
    string? LogoUrl = null,
    string? PrimaryColor = null,
    Dictionary<string, string>? Texts = null,
    decimal? PricePerConversation = null,
    string? Currency = null,
    int? MonthlyConversationCap = null,
    int? MessagesPerConversationCap = null);

public sealed record ClientView(
    Guid Id, string Name, string Slug, string Status, string? LogoUrl, string? PrimaryColor, IReadOnlyDictionary<string, string> Texts,
    decimal? PricePerConversation, string Currency, int? MonthlyConversationCap, int? MessagesPerConversationCap, DateTimeOffset CreatedAt,
    int Assets = 0, int Bots = 0, int ConversationsThisMonth = 0);

public sealed record AssetRequest(Guid? ClientId, string? Name, string? Description);

public sealed record AssetView(Guid Id, Guid ClientId, string Name, string? Description, DateTimeOffset CreatedAt, int Bots = 0);

/// <summary>
/// Clientes y assets. La agencia crea y configura clientes (marca del widget, precio y topes: sin configurar = ilimitado) y sus assets.
/// Un usuario de un cliente solo ve el suyo (filtro global por cliente).
/// </summary>
public sealed partial class ClientService(IAppDbContext db, TimeProvider clock)
{
    public static readonly string[] TextKeys = ["placeholder", "send", "typing", "lead_link", "lead_name", "lead_email", "lead_phone", "lead_interest", "consent", "thanks"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public async Task<IReadOnlyList<ClientView>> ListAsync(CancellationToken ct)
    {
        var clients = await db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var period = UsagePeriod.Of(clock.GetUtcNow());
        var assets = await db.Assets.AsNoTracking().GroupBy(a => a.ClientId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var bots = await db.Bots.AsNoTracking().GroupBy(b => b.ClientId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var usage = await db.ChatUsages.AsNoTracking().Where(u => u.Period == period).GroupBy(u => u.ClientId)
            .Select(g => new { g.Key, Count = g.Sum(u => u.Conversations) }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return clients.Select(c => ToView(c) with
        {
            Assets = assets.GetValueOrDefault(c.Id),
            Bots = bots.GetValueOrDefault(c.Id),
            ConversationsThisMonth = usage.GetValueOrDefault(c.Id),
        }).ToList();
    }

    public async Task<ServiceResult<ClientView>> GetAsync(Guid id, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return NotFound<ClientView>();
        }

        var period = UsagePeriod.Of(clock.GetUtcNow());
        return ServiceResult<ClientView>.Ok(ToView(client) with
        {
            Assets = await db.Assets.CountAsync(a => a.ClientId == id, ct),
            Bots = await db.Bots.CountAsync(b => b.ClientId == id, ct),
            ConversationsThisMonth = await db.ChatUsages.Where(u => u.ClientId == id && u.Period == period).SumAsync(u => (int?)u.Conversations, ct) ?? 0,
        });
    }

    public async Task<ServiceResult<ClientView>> CreateAsync(ClientRequest request, CancellationToken ct)
    {
        var client = new Client { Id = Guid.CreateVersion7(), Name = "", Slug = "", CreatedAt = clock.GetUtcNow() };
        if (Apply(client, request, creating: true) is { } error)
        {
            return error;
        }

        if (await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Slug == client.Slug, ct))
        {
            return SlugTaken();
        }

        db.Clients.Add(client);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return SlugTaken();
        }

        return ServiceResult<ClientView>.Ok(ToView(client));
    }

    public async Task<ServiceResult<ClientView>> UpdateAsync(Guid id, ClientRequest request, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return NotFound<ClientView>();
        }

        var previousSlug = client.Slug;
        if (Apply(client, request, creating: false) is { } error)
        {
            return error;
        }

        if (client.Slug != previousSlug && await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Slug == client.Slug && c.Id != id, ct))
        {
            return SlugTaken();
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<ClientView>.Ok(ToView(client));
    }

    /// <summary>Solo se borra un cliente vacío (sin assets ni usuarios): para dejar de atenderlo sin perder datos, se suspende.</summary>
    public async Task<ServiceResult<bool>> DeleteAsync(Guid id, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return NotFound<bool>();
        }

        if (await db.Assets.AnyAsync(a => a.ClientId == id, ct) || await db.Users.IgnoreQueryFilters().AnyAsync(u => u.ClientId == id, ct))
        {
            return ServiceResult<bool>.Fail("invalid_state", "El cliente tiene assets o usuarios. Bórralos primero, o suspende el cliente.");
        }

        db.UserInvites.RemoveRange(await db.UserInvites.IgnoreQueryFilters().Where(i => i.ClientId == id).ToListAsync(ct));
        db.Clients.Remove(client);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    // ---- Assets ----

    public async Task<IReadOnlyList<AssetView>> ListAssetsAsync(Guid? clientId, CancellationToken ct)
    {
        var query = db.Assets.AsNoTracking();
        if (clientId is { } c)
        {
            query = query.Where(a => a.ClientId == c);
        }

        var assets = await query.OrderBy(a => a.Name).ToListAsync(ct);
        var ids = assets.Select(a => a.Id).ToList();
        var bots = await db.Bots.AsNoTracking().Where(b => ids.Contains(b.AssetId)).GroupBy(b => b.AssetId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return assets.Select(a => ToView(a) with { Bots = bots.GetValueOrDefault(a.Id) }).ToList();
    }

    public async Task<ServiceResult<AssetView>> CreateAssetAsync(AssetRequest request, CancellationToken ct)
    {
        if (request.ClientId is not { } clientId || !await db.Clients.AnyAsync(c => c.Id == clientId, ct))
        {
            return ServiceResult<AssetView>.Fail("validation_error", "Elige el cliente del asset.", new Dictionary<string, string> { ["client_id"] = "Cliente no encontrado." });
        }

        var asset = new Asset { Id = Guid.CreateVersion7(), ClientId = clientId, Name = "", CreatedAt = clock.GetUtcNow() };
        if (ApplyAsset(asset, request, creating: true) is { } error)
        {
            return error;
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);
        return ServiceResult<AssetView>.Ok(ToView(asset));
    }

    public async Task<ServiceResult<AssetView>> UpdateAssetAsync(Guid id, AssetRequest request, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return ServiceResult<AssetView>.Fail("not_found", "Asset no encontrado.");
        }

        if (ApplyAsset(asset, request, creating: false) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<AssetView>.Ok(ToView(asset));
    }

    /// <summary>Solo se borra un asset sin bots (los bots se borran o se mueven a otro asset antes).</summary>
    public async Task<ServiceResult<bool>> DeleteAssetAsync(Guid id, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Asset no encontrado.");
        }

        if (await db.Bots.AnyAsync(b => b.AssetId == id, ct))
        {
            return ServiceResult<bool>.Fail("invalid_state", "El asset tiene chatbots. Bórralos o muévelos a otro asset primero.");
        }

        db.Assets.Remove(asset);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Mueve un bot a otro asset del mismo cliente o de otro. Todo lo suyo (conocimiento, canales, conversaciones, contactos, secretos) pasa
    /// con él; el consumo de meses anteriores se queda con el cliente donde ocurrió.
    /// </summary>
    public async Task<ServiceResult<bool>> MoveBotAsync(Guid botId, Guid assetId, CancellationToken ct)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == botId, ct);
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (bot is null || asset is null)
        {
            return ServiceResult<bool>.Fail("not_found", "Chatbot o asset no encontrado.");
        }

        var from = bot.ClientId;
        var to = asset.ClientId;
        bot.AssetId = asset.Id;
        if (from == to)
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }

        await db.SaveChangesAsync(ct);
        await db.ReassignBotClientAsync(botId, to, ct);
        return ServiceResult<bool>.Ok(true);
    }

    private static ServiceResult<ClientView>? Apply(Client c, ClientRequest r, bool creating)
    {
        var fields = new Dictionary<string, string>();
        if (r.Name is not null || creating)
        {
            var name = r.Name?.Trim() ?? "";
            if (name.Length is 0 or > 200)
            {
                fields["name"] = "Escribe el nombre del cliente (máximo 200 caracteres).";
            }

            c.Name = name;
        }

        if (r.Slug is not null || creating)
        {
            var slug = (string.IsNullOrWhiteSpace(r.Slug) ? Slugify(c.Name) : r.Slug.Trim()).ToLowerInvariant();
            if (!SlugPattern().IsMatch(slug))
            {
                fields["slug"] = "Solo minúsculas, números y guiones (máximo 40).";
            }

            c.Slug = slug;
        }

        if (r.Status is not null)
        {
            switch (r.Status.Trim().ToLowerInvariant())
            {
                case "active": c.Status = ClientStatus.Active; break;
                case "suspended": c.Status = ClientStatus.Suspended; break;
                default: fields["status"] = "Valores: active, suspended."; break;
            }
        }

        if (r.LogoUrl is not null)
        {
            var url = r.LogoUrl.Trim();
            if (url.Length > 0 && (url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
            {
                fields["logo_url"] = "El logo debe ser una dirección https.";
            }

            c.LogoUrl = url.Length == 0 ? null : url;
        }

        if (r.PrimaryColor is not null)
        {
            var color = r.PrimaryColor.Trim();
            if (color.Length > 0 && !ColorPattern().IsMatch(color))
            {
                fields["primary_color"] = "Usa un color #rrggbb.";
            }

            c.PrimaryColor = color.Length == 0 ? null : color.ToLowerInvariant();
        }

        if (r.Texts is not null)
        {
            var texts = new Dictionary<string, string>();
            foreach (var (key, value) in r.Texts)
            {
                if (!TextKeys.Contains(key))
                {
                    fields["texts"] = $"Texto desconocido: {key}.";
                    continue;
                }

                var v = value?.Trim() ?? "";
                if (v.Length > 300)
                {
                    fields["texts"] = "Cada texto admite hasta 300 caracteres.";
                }
                else if (v.Length > 0)
                {
                    texts[key] = v;
                }
            }

            c.Texts = texts;
        }

        if (r.PricePerConversation is { } price)
        {
            if (price < 0 || price > 1_000_000)
            {
                fields["price_per_conversation"] = "Precio no válido.";
            }

            c.PricePerConversation = price == 0 ? null : price;
        }

        if (r.Currency is not null)
        {
            var currency = r.Currency.Trim().ToUpperInvariant();
            if (!CurrencyPattern().IsMatch(currency))
            {
                fields["currency"] = "Usa un código de moneda de tres letras (COP, USD…).";
            }

            c.Currency = currency;
        }

        if (r.MonthlyConversationCap is { } cap)
        {
            if (cap < 0)
            {
                fields["monthly_conversation_cap"] = "El tope no puede ser negativo.";
            }

            c.MonthlyConversationCap = cap == 0 ? null : cap;
        }

        if (r.MessagesPerConversationCap is { } perConversation)
        {
            if (perConversation < 0 || perConversation > 10_000)
            {
                fields["messages_per_conversation_cap"] = "Entre 1 y 10.000 (0 = sin tope).";
            }

            c.MessagesPerConversationCap = perConversation == 0 ? null : perConversation;
        }

        return fields.Count == 0 ? null : ServiceResult<ClientView>.Fail("validation_error", fields.Values.First(), fields);
    }

    private static ServiceResult<AssetView>? ApplyAsset(Asset a, AssetRequest r, bool creating)
    {
        if (r.Name is not null || creating)
        {
            var name = r.Name?.Trim() ?? "";
            if (name.Length is 0 or > 200)
            {
                return ServiceResult<AssetView>.Fail("validation_error", "Escribe el nombre del asset (máximo 200 caracteres).", new Dictionary<string, string> { ["name"] = "Nombre requerido." });
            }

            a.Name = name;
        }

        if (r.Description is not null)
        {
            var description = r.Description.Trim();
            if (description.Length > 1000)
            {
                return ServiceResult<AssetView>.Fail("validation_error", "La descripción admite hasta 1.000 caracteres.", new Dictionary<string, string> { ["description"] = "Demasiado larga." });
            }

            a.Description = description.Length == 0 ? null : description;
        }

        return null;
    }

    /// <summary>Nombre corto a partir del nombre: sin tildes, minúsculas y guiones.</summary>
    public static string Slugify(string name)
    {
        var normalized = name.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        var slug = Regex.Replace(new string(chars.ToArray()), "-{2,}", "-").Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("not_found", "Cliente no encontrado.");

    private static ServiceResult<ClientView> SlugTaken() =>
        ServiceResult<ClientView>.Fail("slug_taken", "Ya existe un cliente con ese nombre corto.", new Dictionary<string, string> { ["slug"] = "En uso." });

    private static ClientView ToView(Client c) =>
        new(c.Id, c.Name, c.Slug, c.Status.ToString().ToLowerInvariant(), c.LogoUrl, c.PrimaryColor, c.Texts ?? [],
            c.PricePerConversation, c.Currency, c.MonthlyConversationCap, c.MessagesPerConversationCap, c.CreatedAt);

    private static AssetView ToView(Asset a) => new(a.Id, a.ClientId, a.Name, a.Description, a.CreatedAt);
}
