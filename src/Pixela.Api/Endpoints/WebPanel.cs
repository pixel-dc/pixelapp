using Microsoft.Extensions.FileProviders;

namespace Pixela.Api.Endpoints;

/// <summary>
/// La API también sirve el panel web (la carpeta <c>wwwroot</c> con el build de Vite). Así en el servidor hay un solo proceso detrás del
/// proxy y Caddy solo pone el HTTPS. Las rutas del panel (<c>/clientes</c>, <c>/chatbots/…</c>) devuelven <c>index.html</c>;
/// las de la API nunca: una ruta de API que no existe sigue siendo un 404.
/// </summary>
public static class WebPanel
{
    private static readonly string[] ApiPrefixes = ["/panel", "/chat", "/meta", "/widget", "/health"];

    public static void UseWebPanel(this WebApplication app)
    {
        var root = app.Configuration["Web:Root"] is { Length: > 0 } configured ? configured : Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(root))
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
            await context.Response.SendFileAsync(index);
        });
    }
}
