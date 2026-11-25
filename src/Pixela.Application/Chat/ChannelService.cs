using System.Text.RegularExpressions;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace Pixela.Application.Chat;

/// <param name="Kind">whatsapp, messenger o instagram (solo al crear).</param>
/// <param name="ExternalId"><c>phone_number_id</c>, id de la página o id de la cuenta de Instagram (solo al crear).</param>
/// <param name="AccessToken">Token de acceso de Meta. Al editar, si se envía reemplaza al anterior y reactiva el canal.</param>
/// <param name="Status">active o disabled (solo al editar).</param>
/// <param name="AccountId">ID del WABA de un canal de WhatsApp. Al editar, cadena vacía lo quita.</param>
public sealed record ChannelRequest(string? Kind, string? ExternalId, string? DisplayName, string? AccessToken, string? Status, string? AccountId = null);

/// <summary>Lo que el panel ve de un canal. El token nunca se devuelve, ni siquiera parcial.</summary>
public sealed record ChannelView(Guid Id, string Kind, string ExternalId, string DisplayName, string Status, DateTimeOffset CreatedAt, string? AccountId = null);

public sealed record ChannelList(IReadOnlyList<ChannelView> Channels);

/// <summary>Canales de Meta (WhatsApp, Messenger, Instagram) conectados a un chatbot. La app de Meta (su clave secreta) es la del bot.</summary>
public sealed partial class ChannelService(IAppDbContext db, ISecretProtector secrets, TimeProvider clock)
{
    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{3,100}$")]
    private static partial Regex ExternalIdPattern();

    public async Task<ServiceResult<ChannelList>> ListAsync(Guid botId, CancellationToken ct)
    {
        if (!await db.Bots.AnyAsync(b => b.Id == botId, ct))
        {
            return ServiceResult<ChannelList>.Fail("not_found", "Chatbot no encontrado.");
        }

        var channels = await db.Channels.AsNoTracking().Where(c => c.BotId == botId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return ServiceResult<ChannelList>.Ok(new ChannelList(channels.Select(ToView).ToList()));
    }

    public async Task<ServiceResult<ChannelView>> CreateAsync(Guid botId, ChannelRequest request, CancellationToken ct)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null)
        {
            return ServiceResult<ChannelView>.Fail("not_found", "Chatbot no encontrado.");
        }

        var fields = new Dictionary<string, string>();
        if (!TryKind(request.Kind, out var kind))
        {
            fields["kind"] = "Elige WhatsApp, Messenger o Instagram.";
        }

        var externalId = request.ExternalId?.Trim() ?? string.Empty;
        if (!ExternalIdPattern().IsMatch(externalId))
        {
            fields["external_id"] = "Escribe el identificador que da Meta (entre 3 y 100 letras, números o guiones).";
        }

        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            fields["display_name"] = "Escribe un nombre para reconocer el canal (máximo 200 caracteres).";
        }

        if (!ValidToken(request.AccessToken))
        {
            fields["access_token"] = "Pega el token de acceso de Meta completo (sin espacios).";
        }

        var accountId = request.AccountId?.Trim();
        if (!string.IsNullOrEmpty(accountId) && !ExternalIdPattern().IsMatch(accountId))
        {
            fields["account_id"] = "El ID de la cuenta de WhatsApp Business solo lleva letras, números y guiones.";
        }

        if (fields.Count > 0)
        {
            return ServiceResult<ChannelView>.Fail("validation_error", fields.Values.First(), fields);
        }

        // Un número, página o cuenta pertenece a un solo chatbot en toda la plataforma: de lo contrario Meta no sabría a quién entregar los mensajes.
        if (await db.Channels.IgnoreQueryFilters().AnyAsync(c => c.Kind == kind && c.ExternalId == externalId, ct))
        {
            return ServiceResult<ChannelView>.Fail("already_in_use", "Ese número, página o cuenta ya está conectado a un chatbot.");
        }

        var channel = new Channel
        {
            Id = Guid.CreateVersion7(),
            ClientId = bot.ClientId,
            BotId = botId,
            Kind = kind,
            ExternalId = externalId,
            DisplayName = name,
            AccessTokenEncrypted = secrets.Protect(request.AccessToken!.Trim()),
            ProviderAccountId = kind == ChannelKind.WhatsApp && !string.IsNullOrEmpty(accountId) ? accountId : null,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Channels.Add(channel);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ServiceResult<ChannelView>.Fail("already_in_use", "Ese número, página o cuenta ya está conectado a un chatbot.");
        }

        return ServiceResult<ChannelView>.Ok(ToView(channel));
    }

    public async Task<ServiceResult<ChannelView>> UpdateAsync(Guid botId, Guid channelId, ChannelRequest request, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.BotId == botId, ct);
        if (channel is null)
        {
            return ServiceResult<ChannelView>.Fail("not_found", "Canal no encontrado.");
        }

        var fields = new Dictionary<string, string>();
        if (request.DisplayName is not null)
        {
            var name = request.DisplayName.Trim();
            if (name.Length is 0 or > 200)
            {
                fields["display_name"] = "Escribe un nombre (máximo 200 caracteres).";
            }
            else
            {
                channel.DisplayName = name;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AccessToken))
        {
            if (!ValidToken(request.AccessToken))
            {
                fields["access_token"] = "Pega el token de acceso de Meta completo (sin espacios).";
            }
            else
            {
                channel.AccessTokenEncrypted = secrets.Protect(request.AccessToken.Trim());
                channel.Status = ChannelStatus.Active; // con un token nuevo, el canal vuelve a funcionar
            }
        }

        if (request.AccountId is not null)
        {
            var accountId = request.AccountId.Trim();
            if (accountId.Length == 0)
            {
                channel.ProviderAccountId = null;
            }
            else if (channel.Kind != ChannelKind.WhatsApp)
            {
                fields["account_id"] = "Solo los canales de WhatsApp llevan ID de cuenta.";
            }
            else if (!ExternalIdPattern().IsMatch(accountId))
            {
                fields["account_id"] = "El ID de la cuenta de WhatsApp Business solo lleva letras, números y guiones.";
            }
            else
            {
                channel.ProviderAccountId = accountId;
            }
        }

        if (request.Status is not null)
        {
            switch (request.Status.Trim().ToLowerInvariant())
            {
                case "active": channel.Status = ChannelStatus.Active; break;
                case "disabled": channel.Status = ChannelStatus.Disabled; break;
                default: fields["status"] = "Valores: active, disabled."; break;
            }
        }

        if (fields.Count > 0)
        {
            return ServiceResult<ChannelView>.Fail("validation_error", fields.Values.First(), fields);
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult<ChannelView>.Ok(ToView(channel));
    }

    public async Task<bool> DeleteAsync(Guid botId, Guid channelId, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.BotId == botId, ct);
        if (channel is null)
        {
            return false;
        }

        db.ChannelInbounds.RemoveRange(await db.ChannelInbounds.Where(i => i.ChannelId == channelId).ToListAsync(ct));
        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool TryKind(string? value, out ChannelKind kind)
    {
        kind = default;
        return value?.Trim().ToLowerInvariant() switch
        {
            "whatsapp" => Set(ChannelKind.WhatsApp, out kind),
            "messenger" => Set(ChannelKind.Messenger, out kind),
            "instagram" => Set(ChannelKind.Instagram, out kind),
            _ => false,
        };

        static bool Set(ChannelKind value, out ChannelKind kind)
        {
            kind = value;
            return true;
        }
    }

    private static bool ValidToken(string? token)
    {
        var t = token?.Trim();
        return t is { Length: >= 20 and <= 4000 } && !t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    private static ChannelView ToView(Channel c) => new(c.Id, c.Kind.ToString().ToLowerInvariant(), c.ExternalId, c.DisplayName, c.Status.ToString().ToLowerInvariant(), c.CreatedAt, c.ProviderAccountId);
}
