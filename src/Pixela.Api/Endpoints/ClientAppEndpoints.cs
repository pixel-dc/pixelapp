using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Pixela.Api.Auth;
using Pixela.Application.Clients;
using Pixela.Application.Common;
using Pixela.Application.Users;

namespace Pixela.Api.Endpoints;

/// <summary>
/// El acceso propio de cada cliente: <c>{slug}.{dominio}</c> se reconoce al principio de cada petición, y desde ahí el panel se instala como
/// una app con el nombre y el icono del cliente (manifiesto, icono de iPhone) y solo dejan entrar a sus usuarios y a la agencia.
/// También las suscripciones a notificaciones de cada dispositivo.
/// </summary>
public static class ClientAppEndpoints
{
    public const string DefaultName = "Pixelapp";
    private const string DefaultColor = "#1e1e2f";
    private const string DefaultIconFile = "pixela-icon.png";

    /// <summary>Reconoce el cliente del subdominio. Un subdominio que no es de un cliente activo no sirve nada (salvo el chat público y los webhooks).</summary>
    public static void UseClientHost(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var resolver = context.RequestServices.GetRequiredService<ClientHostResolver>();
            var (kind, slug) = resolver.Classify(context.Request.Host.Host);
            if (kind == HostKind.Client)
            {
                var client = await resolver.FindAsync(slug!, context.RequestAborted);
                var path = context.Request.Path;
                var shared = path.StartsWithSegments("/health") || path.StartsWithSegments("/tls");
                if (client is not { Active: true } && !shared)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync("<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>Acceso no disponible</title><p style=\"font:16px system-ui;margin:3rem auto;max-width:28rem;padding:0 1rem\">Este acceso no existe o no está activo. Pide el enlace correcto a quien te lo dio.</p>");
                    return;
                }

                context.RequestServices.GetRequiredService<ClientHost>().Current = client;
            }

            await next(context);
        });
    }

    public static void MapClientApps(this WebApplication app)
    {
        var root = WebPanel.Root(app);

        // Caddy pregunta antes de emitir un certificado bajo demanda: solo para el panel y los subdominios de clientes activos. Solo desde el propio servidor.
        app.MapGet("/tls/allow", async (string? domain, HttpContext http, ClientHostResolver hosts, CancellationToken ct) =>
            http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) && await hosts.AllowsCertificateAsync(domain, ct)
                ? Results.Ok()
                : Results.NotFound());

        app.MapGet("/manifest.webmanifest", (HttpContext http, ClientHost host) =>
        {
            var site = host.Current;
            var name = site?.Name ?? DefaultName;
            var version = site?.IconVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
            // Sin icono propio va el de la agencia (256 px). Android pide que el tamaño declarado sea el real.
            var size = site?.IconSize is { } own ? $"{own}x{own}" : "256x256";
            var manifest = new
            {
                id = "/",
                name,
                short_name = name.Length > 12 ? name[..12].TrimEnd() : name,
                start_url = "/bandeja",
                scope = "/",
                display = "standalone",
                background_color = DefaultColor,
                theme_color = site?.PrimaryColor ?? DefaultColor,
                lang = "es",
                icons = new[] { new { src = $"/app-icon.png?v={version}", sizes = size, type = "image/png", purpose = "any" } },
            };
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Text(JsonSerializer.Serialize(manifest), "application/manifest+json; charset=utf-8");
        });

        // El icono de la app: el del cliente en su acceso; si no tiene, el de la agencia. iPhone pide /apple-touch-icon.png por su cuenta.
        async Task<IResult> IconAsync(HttpContext http, ClientHost host, ClientAppService apps, string fallback, CancellationToken ct)
        {
            if (host.Current is { } site && await apps.GetIconAsync(site.Id, ct) is { } icon)
            {
                return PanelEndpoints.ServeImage(http, icon.Image, "public, max-age=3600");
            }

            var file = root is null ? null : Path.Combine(root, fallback);
            if (file is null || !File.Exists(file))
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(file, "image/png");
        }

        app.MapGet("/app-icon.png", (HttpContext http, ClientHost host, ClientAppService apps, CancellationToken ct) => IconAsync(http, host, apps, DefaultIconFile, ct));
        app.MapGet("/apple-touch-icon.png", (HttpContext http, ClientHost host, ClientAppService apps, CancellationToken ct) => IconAsync(http, host, apps, "apple-touch-icon.png", ct));

        // Marca del acceso: el panel la pide antes de iniciar sesión (nombre e icono en la entrada) y para saber si está en el acceso de un cliente.
        app.MapGet("/panel/branding", (ClientHost host, ClientHostResolver hosts) =>
        {
            var site = host.Current;
            return Results.Ok(new
            {
                name = site?.Name ?? DefaultName,
                client_id = site?.Id,
                slug = site?.Slug,
                color = site?.PrimaryColor,
                icon_url = $"/app-icon.png?v={site?.IconVersion ?? 0}",
                app_domain = hosts.Domain,
            });
        });

        var push = app.MapGroup("/panel/push").RequireAuthorization(Policies.Session);
        push.MapGet("/key", (PushService s) => Results.Ok(new { public_key = s.PublicKey }));
        push.MapPost("/subscriptions", async (PushSubscribeRequest r, HttpContext http, ClientHost host, PushService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.SubscribeAsync(PanelEndpoints.Principal(http), host.Current?.Id, r, http.Request.Headers.UserAgent.ToString(), ct), _ => new { ok = true }));
        push.MapPost("/subscriptions/remove", async (EndpointRequest r, HttpContext http, PushService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UnsubscribeAsync(PanelEndpoints.Principal(http), r.Endpoint, ct), _ => new { ok = true }));
        push.MapPost("/test", async (EndpointRequest r, HttpContext http, PushService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.SendTestAsync(PanelEndpoints.Principal(http), r.Endpoint, ct), _ => new { ok = true }));

        // Icono de la app de un cliente: lo sube la agencia.
        var icons = app.MapGroup("/panel/clients/{id:guid}/app-icon").RequireAuthorization(Policies.Agency);
        icons.MapPut("/", async (Guid id, IFormFile file, ClientAppService s, CancellationToken ct) =>
                PanelEndpoints.ToResult(await s.SetIconAsync(id, await PanelEndpoints.ReadImageAsync(file, ct), ct), _ => new { ok = true }))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ImageUpload.MaxBytes + (64 * 1024)));
        icons.MapGet("/", async (Guid id, HttpContext http, ClientAppService s, CancellationToken ct) =>
            await s.GetIconAsync(id, ct) is { } icon ? PanelEndpoints.ServeImage(http, icon.Image) : Results.NotFound());
        icons.MapDelete("/", async (Guid id, ClientAppService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.RemoveIconAsync(id, ct), _ => new { ok = true }));
    }
}

public sealed record EndpointRequest(string? Endpoint);
