using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Common;
using Pixela.Domain.Chat;

namespace Pixela.Application.Chat;

/// <param name="Status">ok, warning o error.</param>
/// <param name="ChannelId">Canal al que se refiere; null si es del bot en general.</param>
/// <param name="Fixable">Pixela puede corregirlo sola con «Reparar».</param>
/// <param name="Hint">Qué hacer a mano cuando Pixela no puede corregirlo.</param>
public sealed record ConnectionCheck(string Id, Guid? ChannelId, string Title, string Status, string? Detail, bool Fixable, string? Hint);

public sealed record ConnectionReport(string WebhookUrl, bool Healthy, bool CanRepair, IReadOnlyList<ConnectionCheck> Checks);

/// <param name="Success">La corrección se aplicó.</param>
public sealed record AppliedFix(string Title, bool Success, string? Message);

public sealed record RepairResult(IReadOnlyList<AppliedFix> Applied, ConnectionReport Report);

/// <summary>
/// Revisa y corrige la conexión de un bot con Meta: token, webhook de la app, suscripción de la app al WABA o a la página y token de página de
/// Messenger. Es lo que antes se hacía a mano en Meta for Developers y Business Suite. Cada corrección es idempotente.
/// </summary>
public sealed class MetaConnectionService(IAppDbContext db, ISecretProtector secrets, IMetaAdmin meta, AgencySettingsReader agency, TimeProvider clock)
{
    private const string Ok = "ok";
    private const string Warning = "warning";
    private const string Error = "error";
    private static readonly string[] MessageFields = ["messages"];
    private static readonly string[] WhatsAppFields = ["messages", "smb_message_echoes"];
    private static readonly string[] PageFields = ["messages", "messaging_postbacks"];

    /// <summary>Una comprobación y, si Pixela puede corregirla, la acción que lo hace (devuelve un mensaje de error o null si salió bien).</summary>
    private sealed record Probe(ConnectionCheck Check, string? FixTitle = null, Func<CancellationToken, Task<string?>>? Fix = null);

    public async Task<ServiceResult<ConnectionReport>> DiagnoseAsync(Guid botId, CancellationToken ct)
    {
        var (probes, webhookUrl) = await ProbeAsync(botId, ct);
        return probes is null
            ? ServiceResult<ConnectionReport>.Fail("not_found", "Chatbot no encontrado.")
            : ServiceResult<ConnectionReport>.Ok(ToReport(webhookUrl, probes));
    }

    public async Task<ServiceResult<RepairResult>> RepairAsync(Guid botId, CancellationToken ct)
    {
        var (probes, _) = await ProbeAsync(botId, ct);
        if (probes is null)
        {
            return ServiceResult<RepairResult>.Fail("not_found", "Chatbot no encontrado.");
        }

        var applied = new List<AppliedFix>();
        foreach (var p in probes.Where(p => p.Check.Status != Ok && p.Fix is not null))
        {
            string? failure;
            try
            {
                failure = await p.Fix!(ct);
            }
            catch (MetaAdminException ex)
            {
                failure = ex.Message;
            }

            applied.Add(new AppliedFix(p.FixTitle ?? p.Check.Title, failure is null, failure));
        }

        // Se vuelve a leer todo de Meta: el informe final es el estado real, no lo que se supone que quedó.
        var (after, url) = await ProbeAsync(botId, ct);
        return ServiceResult<RepairResult>.Ok(new RepairResult(applied, ToReport(url, after ?? [])));
    }

    private static ConnectionReport ToReport(string webhookUrl, List<Probe> probes) =>
        new(webhookUrl, probes.All(p => p.Check.Status == Ok), probes.Any(p => p.Check.Status != Ok && p.Fix is not null), probes.Select(p => p.Check).ToList());

    private async Task<(List<Probe>? Probes, string WebhookUrl)> ProbeAsync(Guid botId, CancellationToken ct)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        var s = bot is null ? null : await db.BotSecrets.AsNoTracking().FirstOrDefaultAsync(x => x.BotId == botId, ct);
        if (bot is null || s is null)
        {
            return (null, "");
        }

        var probes = new List<Probe>();
        var baseUrl = (await agency.GetAsync(ct)).PublicBaseUrl?.TrimEnd('/');
        var webhookUrl = (baseUrl ?? "") + BotMetaAppService.WebhookPath + bot.PublicKey;
        if (string.IsNullOrEmpty(baseUrl))
        {
            probes.Add(Fail("public_url", null, "Dirección pública de Pixela", "No está configurada, y Meta necesita una dirección completa para el webhook.", "Escríbela en Ajustes de la agencia (por ejemplo https://pixelapp.pixeldc.com)."));
            return (probes, webhookUrl);
        }

        var verifyToken = secrets.Unprotect(s.MetaVerifyTokenEncrypted);
        var appSecret = s.MetaAppSecretEncrypted is { Length: > 0 } enc ? secrets.Unprotect(enc) : null;
        if (appSecret is null)
        {
            probes.Add(Fail("app_secret", null, "Clave secreta de la app de Meta", "No está guardada: sin ella Pixela rechaza todos los mensajes de Meta.", "Pégala en la pestaña Integraciones del bot."));
        }

        var channels = await db.Channels.Where(c => c.BotId == botId && c.Status != ChannelStatus.Disabled).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        if (channels.Count == 0)
        {
            probes.Add(new Probe(new ConnectionCheck("channels", null, "Canales", Warning, "Este chatbot todavía no tiene canales de Meta conectados.", false, "Conecta uno con «Conectar canal».")));
            return (probes, webhookUrl);
        }

        // 1. Token de cada canal. De aquí sale también la app de Meta (cada bot trae la suya).
        var tokens = new Dictionary<Guid, (string Token, MetaTokenInfo? Info)>();
        string? appId = null;
        foreach (var ch in channels)
        {
            var token = secrets.Unprotect(ch.AccessTokenEncrypted);
            MetaTokenInfo? info = null;
            try
            {
                info = await meta.DebugTokenAsync(token, ct);
                if (!info.IsValid)
                {
                    probes.Add(Fail("token", ch.Id, $"{Name(ch)}: token", "Meta rechazó el token (venció o se revocó).", "Genera uno nuevo y pégalo con «Cambiar token»."));
                    info = null;
                }
                else
                {
                    appId ??= info.AppId;
                    var soon = info.ExpiresAt is { } at && at - clock.GetUtcNow() < TimeSpan.FromDays(7);
                    probes.Add(Pass("token", ch.Id, $"{Name(ch)}: token", soon ? Warning : Ok, soon ? $"Vence el {info.ExpiresAt:yyyy-MM-dd}." : null,
                        soon ? "Genera uno que no venza (usuario del sistema) y cámbialo." : null));
                }
            }
            catch (MetaAdminException ex)
            {
                probes.Add(Fail("token", ch.Id, $"{Name(ch)}: token", $"No se pudo revisar el token: {ex.Message}", "Revisa que el token esté completo y que Meta esté disponible."));
            }

            tokens[ch.Id] = (token, info);
        }

        // 2. Webhook de la app para cada tipo de canal: debe apuntar a la dirección de este bot y recibir mensajes.
        if (appSecret is not null && appId is not null)
        {
            await ProbeAppWebhookAsync(probes, channels, appId, appSecret, webhookUrl, verifyToken, ct);
        }
        else if (appSecret is not null)
        {
            probes.Add(Fail("app_webhook", null, "Webhook de la app de Meta", "No se pudo saber a qué app de Meta pertenecen los tokens, así que no se revisó.", "Arregla primero los tokens de arriba."));
        }

        // 3. Por canal: WhatsApp (número y suscripción al WABA) y Messenger (token de página y suscripción a la página).
        foreach (var ch in channels)
        {
            var (token, info) = tokens[ch.Id];
            if (info is null)
            {
                continue;
            }

            if (ch.Kind == ChannelKind.WhatsApp)
            {
                await ProbeWhatsAppAsync(probes, ch, token, appId, webhookUrl, verifyToken, ct);
            }
            else if (ch.Kind == ChannelKind.Messenger)
            {
                await ProbeMessengerAsync(probes, ch, token, info, appId, ct);
            }

            var since = clock.GetUtcNow().AddHours(-24);
            var failures = db.ChannelInbounds.AsNoTracking().Where(i => i.ChannelId == ch.Id && i.Status == InboundStatus.Failed && i.CreatedAt >= since);
            var failed = await failures.CountAsync(ct);
            if (failed > 0)
            {
                var last = await failures.OrderByDescending(i => i.CreatedAt).Select(i => i.LastError).FirstOrDefaultAsync(ct);
                probes.Add(new Probe(new ConnectionCheck("delivery", ch.Id, $"{Name(ch)}: envíos", Warning, $"{failed} mensaje(s) no se pudieron enviar en las últimas 24 horas." + (string.IsNullOrWhiteSpace(last) ? "" : $" Último motivo: {last}."), false,
                    "Si es Messenger, normalmente es un token que no es de la página; si es WhatsApp, revisa el token del canal.")));
            }
        }

        return (probes, webhookUrl);
    }

    private async Task ProbeAppWebhookAsync(List<Probe> probes, List<Channel> channels, string appId, string appSecret, string webhookUrl, string verifyToken, CancellationToken ct)
    {
        IReadOnlyList<MetaAppSubscription> subs;
        try
        {
            subs = await meta.GetAppSubscriptionsAsync(appId, appSecret, ct);
        }
        catch (MetaAdminException ex)
        {
            probes.Add(Fail("app_webhook", null, "Webhook de la app de Meta", $"No se pudo leer: {ex.Message}", "Comprueba que la clave secreta guardada es la de la app de Meta de este bot."));
            return;
        }

        foreach (var kind in channels.Select(c => c.Kind).Distinct())
        {
            var obj = MetaObject(kind);
            var title = $"Webhook de la app: {Label(kind)}";
            var sub = subs.FirstOrDefault(x => x.Topic == obj);
            var required = kind == ChannelKind.WhatsApp ? WhatsAppFields : MessageFields;
            if (sub is { Active: true } && string.Equals(sub.CallbackUrl, webhookUrl, StringComparison.Ordinal) && required.All(sub.Fields.Contains))
            {
                probes.Add(Pass("app_webhook", null, title, Ok, null, null));
                continue;
            }

            var detail = sub is null ? "La app no tiene webhook para este tipo de canal."
                : !string.Equals(sub.CallbackUrl, webhookUrl, StringComparison.Ordinal) ? "El webhook apunta a otra dirección, así que Meta no entrega los mensajes a este bot."
                : !sub.Fields.Contains("messages") ? "El webhook no está suscrito al campo «messages»."
                : !required.All(sub.Fields.Contains) ? "El webhook no está suscrito a «smb_message_echoes»: lo que escribes desde el teléfono (WhatsApp Business) no llega a Pixela."
                : "El webhook está inactivo.";
            var fields = (sub?.Fields ?? []).Union(required).ToList();
            probes.Add(new Probe(
                new ConnectionCheck("app_webhook", null, title, Error, detail, true, null),
                $"Fijar el webhook de la app ({Label(kind)})",
                async token =>
                {
                    await meta.SetAppSubscriptionAsync(appId, appSecret, obj, webhookUrl, verifyToken, fields, token);
                    return null;
                }));
        }
    }

    private async Task ProbeWhatsAppAsync(List<Probe> probes, Channel ch, string token, string? appId, string webhookUrl, string verifyToken, CancellationToken ct)
    {
        var name = Name(ch);
        try
        {
            var phone = await meta.GetPhoneAsync(ch.ExternalId, token, ct);
            var connected = string.Equals(phone.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase);
            probes.Add(Pass("phone", ch.Id, $"{name}: número", connected ? Ok : Warning,
                connected ? phone.DisplayPhoneNumber : $"El número figura como «{phone.Status ?? "desconocido"}» en Meta.", connected ? null : "Revísalo en WhatsApp Manager."));

            // Una dirección propia del número (o de su WABA) manda sobre la de la app: si apunta a otro lado, Meta no entrega aquí.
            var wrong = phone.Overrides.Where(o => !string.Equals(o.Value, webhookUrl, StringComparison.Ordinal)).ToList();
            if (wrong.Count > 0)
            {
                probes.Add(new Probe(
                    new ConnectionCheck("phone_webhook", ch.Id, $"{name}: webhook del número", Error, "El número tiene una dirección de webhook propia que no es la de este bot.", true, null),
                    $"Fijar el webhook del número ({name})",
                    async c =>
                    {
                        await meta.SetPhoneWebhookAsync(ch.ExternalId, token, webhookUrl, verifyToken, c);
                        return null;
                    }));
            }
        }
        catch (MetaAdminException ex)
        {
            probes.Add(Fail("phone", ch.Id, $"{name}: número", $"No se pudo leer el número: {ex.Message}", "Comprueba el ID del número y que el token tenga permisos sobre la cuenta de WhatsApp."));
        }

        var waba = ch.ProviderAccountId;
        if (string.IsNullOrWhiteSpace(waba))
        {
            probes.Add(new Probe(new ConnectionCheck("waba", ch.Id, $"{name}: cuenta de WhatsApp (WABA)", Warning, "Falta el ID de la cuenta de WhatsApp Business. Sin él no se puede comprobar que la app esté suscrita a ella, que es lo que hace que Meta envíe los mensajes.", false,
                "Escríbelo en el canal (Meta lo muestra en WhatsApp → Configuración de la API).")));
            return;
        }

        try
        {
            var subscribed = await meta.GetSubscribedAppsAsync(waba, token, ct);
            if (appId is not null && subscribed.Any(a => a.AppId == appId))
            {
                probes.Add(Pass("waba", ch.Id, $"{name}: app suscrita a la cuenta", Ok, null, null));
                return;
            }

            probes.Add(new Probe(
                new ConnectionCheck("waba", ch.Id, $"{name}: app suscrita a la cuenta", Error, "La app no está suscrita a la cuenta de WhatsApp: Meta no envía ningún mensaje.", true, null),
                $"Suscribir la app a la cuenta de WhatsApp ({name})",
                async c =>
                {
                    await meta.SubscribeAppAsync(waba, token, null, c);
                    return null;
                }));
        }
        catch (MetaAdminException ex)
        {
            probes.Add(Fail("waba", ch.Id, $"{name}: app suscrita a la cuenta", $"No se pudo leer la cuenta: {ex.Message}", "Comprueba el ID del WABA y que el token tenga la cuenta asignada."));
        }
    }

    private async Task ProbeMessengerAsync(List<Probe> probes, Channel ch, string token, MetaTokenInfo info, string? appId, CancellationToken ct)
    {
        var name = Name(ch);
        var pageToken = token;
        if (!string.Equals(info.Type, "PAGE", StringComparison.OrdinalIgnoreCase))
        {
            // Messenger solo envía con el token de la página. Si el guardado es de un usuario del sistema con la página asignada, se pide el de la página.
            string? fetched = null;
            string? why = null;
            try
            {
                fetched = await meta.GetPageAccessTokenAsync(ch.ExternalId, token, ct);
            }
            catch (MetaAdminException ex)
            {
                why = ex.Message;
            }

            if (fetched is null)
            {
                probes.Add(Fail("page_token", ch.Id, $"{name}: token de la página", $"El token guardado es de tipo {info.Type ?? "desconocido"} y Messenger solo envía con el token de la página. {why}".TrimEnd(),
                    "En Configuración del negocio asigna la página al usuario del sistema (con permisos de mensajes) y vuelve a verificar; o pega el token de la página con «Cambiar token»."));
                return;
            }

            pageToken = fetched;
            var newToken = fetched;
            probes.Add(new Probe(
                new ConnectionCheck("page_token", ch.Id, $"{name}: token de la página", Error, $"El token guardado es de tipo {info.Type ?? "desconocido"} y Messenger solo envía con el token de la página.", true, null),
                $"Cambiar por el token de la página ({name})",
                async c =>
                {
                    ch.AccessTokenEncrypted = secrets.Protect(newToken);
                    ch.Status = ChannelStatus.Active;
                    await db.SaveChangesAsync(c);
                    return null;
                }));
        }
        else
        {
            probes.Add(Pass("page_token", ch.Id, $"{name}: token de la página", Ok, null, null));
        }

        var token2 = pageToken;
        try
        {
            var subscribed = await meta.GetSubscribedAppsAsync(ch.ExternalId, token2, ct);
            var mine = appId is null ? null : subscribed.FirstOrDefault(a => a.AppId == appId);
            if (mine is not null && mine.Fields.Contains("messages"))
            {
                probes.Add(Pass("page_subscription", ch.Id, $"{name}: app suscrita a la página", Ok, null, null));
                return;
            }

            probes.Add(new Probe(
                new ConnectionCheck("page_subscription", ch.Id, $"{name}: app suscrita a la página", Error, "La app no recibe los mensajes de la página.", true, null),
                $"Suscribir la app a la página ({name})",
                async c =>
                {
                    await meta.SubscribeAppAsync(ch.ExternalId, token2, PageFields, c);
                    return null;
                }));
        }
        catch (MetaAdminException ex)
        {
            probes.Add(Fail("page_subscription", ch.Id, $"{name}: app suscrita a la página", $"No se pudo leer la página: {ex.Message}", "Comprueba el ID de la página y que el token tenga acceso a ella."));
        }
    }

    private static string MetaObject(ChannelKind kind) => kind switch
    {
        ChannelKind.WhatsApp => "whatsapp_business_account",
        ChannelKind.Messenger => "page",
        _ => "instagram",
    };

    private static string Label(ChannelKind kind) => kind switch
    {
        ChannelKind.WhatsApp => "WhatsApp",
        ChannelKind.Messenger => "Messenger",
        _ => "Instagram",
    };

    private static string Name(Channel c) => string.IsNullOrWhiteSpace(c.DisplayName) ? Label(c.Kind) : c.DisplayName;

    private static Probe Pass(string id, Guid? channel, string title, string status, string? detail, string? hint) =>
        new(new ConnectionCheck(id, channel, title, status, detail, false, hint));

    private static Probe Fail(string id, Guid? channel, string title, string detail, string hint) =>
        new(new ConnectionCheck(id, channel, title, Error, detail, false, hint));
}
