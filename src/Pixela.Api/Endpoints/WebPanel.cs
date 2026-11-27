using System.Net;
using Microsoft.Extensions.FileProviders;
using Pixela.Application.Clients;

namespace Pixela.Api.Endpoints;

/// <summary>
/// La API también sirve el panel web (la carpeta <c>wwwroot</c> con el build de Vite). Así en el servidor hay un solo proceso detrás del
/// proxy y Caddy solo pone el HTTPS. Las rutas del panel (<c>/clientes</c>, <c>/chatbots/…</c>) devuelven <c>index.html</c>;
/// las de la API nunca: una ruta de API que no existe sigue siendo un 404.
/// </summary>
public static class WebPanel
{
    private static readonly string[] ApiPrefixes = ["/panel", "/chat", "/meta", "/widget", "/health", "/tls"];

    /// <summary>Carpeta del build del panel; null si no existe (en desarrollo el panel lo sirve Vite).</summary>
    public static string? Root(WebApplication app)
    {
        var root = app.Configuration["Web:Root"] is { Length: > 0 } configured ? configured : Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(root) ? root : null;
    }

    public static void UseWebPanel(this WebApplication app)
    {
        if (Root(app) is not { } root)
        {
            return; // en desarrollo el panel lo sirve Vite
        }

        var files = new PhysicalFileProvider(root);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            // Los archivos de /assets llevan una huella en el nombre: se pueden cachear un año. index.html nunca.
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        });

        var index = files.GetFileInfo("index.html");
        string? html = null;
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (!HttpMethods.IsGet(context.Request.Method) || ApiPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) || !index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            if (context.RequestServices.GetRequiredService<ClientHost>().Current is { } site)
            {
                // En el acceso de un cliente, la app se instala con su nombre (iPhone lo toma del título y de apple-mobile-web-app-title).
                html ??= await File.ReadAllTextAsync(index.PhysicalPath!, context.RequestAborted);
                await context.Response.WriteAsync(Brand(html, site.Name), context.RequestAborted);
                return;
            }

            await context.Response.SendFileAsync(index);
        });
    }

    /// <summary>El index.html con el nombre del cliente como título de la página y de la app instalada.</summary>
    public static string Brand(string html, string name)
    {
        var safe = WebUtility.HtmlEncode(name);
        var start = html.IndexOf("<title>", StringComparison.Ordinal);
        var end = html.IndexOf("</title>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            html = html[..(start + 7)] + safe + html[end..];
        }

        return html.Replace("content=\"PixelApp\"", $"content=\"{safe}\"", StringComparison.Ordinal);
    }
}
