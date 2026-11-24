using System.Net;
using System.Text;
using Pixela.Application.Chat;

namespace Pixela.Infrastructure.Chat;

/// <summary>
/// Lee una tabla de Supabase por su API REST (PostgREST) con la llave de solo lectura del negocio. El destino ya llegó validado como
/// <c>https://&lt;proyecto&gt;.supabase.co</c>; aquí no se siguen redirecciones y se limita el tamaño de la respuesta.
/// </summary>
public sealed class SupabaseCatalogFetcher : ICatalogFetcher, IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly HttpClient http;

    public SupabaseCatalogFetcher(HttpMessageHandler? handler = null)
    {
        http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
    }

    public void Dispose() => http.Dispose();

    /// <summary>Mensaje que devuelve PostgREST (por ejemplo "permission denied for table x"). Nunca contiene la llave.</summary>
    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? "" : $" Detalle: {(text.Length > 200 ? text[..200] : text)}";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException or InvalidOperationException)
        {
            return "";
        }
    }

    public async Task<string> FetchAsync(Uri projectUrl, string table, string? columns, string apiKey, CancellationToken ct)
    {
        var select = string.IsNullOrWhiteSpace(columns) ? "*" : columns;
        var url = new Uri(projectUrl, $"/rest/v1/{Uri.EscapeDataString(table)}?select={Uri.EscapeDataString(select).Replace("%2C", ",", StringComparison.Ordinal)}&limit={CatalogText.MaxRows + 1}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        // Las llaves nuevas (sb_publishable_…, sb_secret_…) no son JWT: solo van en "apikey". Las antiguas (anon, service_role) sí van también como Bearer.
        if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                break;
            case HttpStatusCode.Unauthorized:
                throw new InvalidOperationException("Supabase no reconoce la llave (401): revisa que sea la publishable/anon de ESE proyecto, completa y sin espacios." + await DetailAsync(response, ct));
            case HttpStatusCode.Forbidden:
                throw new InvalidOperationException("La llave es válida pero no tiene permiso para leer esa tabla (403): falta una política RLS de lectura o el permiso SELECT para el rol anon." + await DetailAsync(response, ct));
            case HttpStatusCode.NotFound:
                throw new InvalidOperationException("No existe esa tabla o vista en el proyecto (o no es visible con esa llave).");
            case HttpStatusCode.BadRequest:
                throw new InvalidOperationException("Supabase no entendió la consulta: revisa los nombres de las columnas.");
            default:
                throw new InvalidOperationException($"Supabase respondió {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new InvalidOperationException("La respuesta es demasiado grande: elige menos columnas o usa una vista más corta.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
