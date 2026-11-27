using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pixela.Application.Abstractions;

namespace Pixela.Application.Clients;

/// <summary>Acceso propio de cada cliente: <c>{slug}.{Domain}</c> (p. ej. <c>don-porko.pixelapp.pixeldc.com</c>).</summary>
public sealed class ClientAppsOptions
{
    public const string Section = "ClientApps";

    /// <summary>
    /// Dominio del panel bajo el que cuelga el subdominio de cada cliente. Vacío = sin accesos por cliente (todo es el panel general).
    /// En desarrollo, <c>localhost</c> permite probar con <c>{slug}.localhost:5173</c>.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Desde dónde firma las notificaciones (lo exige Apple: una dirección https o un correo con <c>mailto:</c>). Vacío = https://{Domain}.
    /// </summary>
    public string? PushContact { get; set; }
}

/// <summary>El cliente dueño del subdominio por el que entra la petición.</summary>
/// <param name="IconVersion">Versión del icono propio de su app; null = usa el de la agencia.</param>
/// <param name="IconSize">Lado en píxeles de ese icono.</param>
public sealed record HostClient(Guid Id, string Name, string Slug, string? PrimaryColor, long? IconVersion, bool Active, int? IconSize = null);

public enum HostKind
{
    /// <summary>El panel general (o un dominio que no es de accesos por cliente): la agencia ve todos los clientes.</summary>
    Main,

    /// <summary>Un subdominio de cliente.</summary>
    Client,
}

/// <summary>Lo que se sabe del host de la petición actual. Lo fija el middleware al principio de cada petición.</summary>
public sealed class ClientHost
{
    /// <summary>El cliente del subdominio; null en el panel general.</summary>
    public HostClient? Current { get; set; }
}

/// <summary>Traduce el host a un cliente. Los datos del cliente se guardan un minuto en memoria: cada petición los necesita.</summary>
public sealed class ClientHostResolver(IAppDbContext db, IMemoryCache cache, IOptions<ClientAppsOptions> options)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Qué es este host: el panel general, o el subdominio de un cliente con su nombre corto. Un host con más de un nivel bajo el dominio
    /// (<c>a.b.dominio</c>) no es de nadie: se trata como un subdominio inexistente.
    /// </summary>
    public (HostKind Kind, string? Slug) Classify(string? host)
    {
        var domain = Normalize(options.Value.Domain);
        var name = Normalize(host);
        if (domain is null || name is null || name == domain || !name.EndsWith("." + domain, StringComparison.Ordinal))
        {
            return (HostKind.Main, null);
        }

        var label = name[..^(domain.Length + 1)];
        return (HostKind.Client, label.Contains('.', StringComparison.Ordinal) ? "" : label);
    }

    /// <summary>El cliente con ese nombre corto (activo o no); null si no existe.</summary>
    public async Task<HostClient?> FindAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 40)
        {
            return null;
        }

        if (cache.TryGetValue(CacheKey(slug), out HostClient? cached))
        {
            return cached;
        }

        var found = await db.Clients.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Slug == slug)
            .Select(c => new HostClient(
                c.Id, c.Name, c.Slug, c.PrimaryColor, c.AppIconUpdatedAt == null ? null : c.AppIconUpdatedAt.Value.UtcTicks, c.Status == Pixela.Domain.Clients.ClientStatus.Active,
                db.ClientAppIcons.Where(i => i.ClientId == c.Id).Select(i => (int?)i.Size).FirstOrDefault()))
            .FirstOrDefaultAsync(ct);
        cache.Set(CacheKey(slug), found, CacheFor);
        return found;
    }

    /// <summary>Olvida lo guardado de un cliente (tras cambiar su icono), para que el siguiente pedido lea lo nuevo.</summary>
    public void Forget(string slug) => cache.Remove(CacheKey(slug));

    /// <summary>¿Debe el proxy emitir un certificado para este host? Solo el dominio del panel y los subdominios de clientes activos.</summary>
    public async Task<bool> AllowsCertificateAsync(string? host, CancellationToken ct)
    {
        var domain = Normalize(options.Value.Domain);
        if (domain is null)
        {
            return false;
        }

        var (kind, slug) = Classify(host);
        if (kind == HostKind.Main)
        {
            return Normalize(host) == domain;
        }

        return await FindAsync(slug!, ct) is { Active: true };
    }

    /// <summary>Dominio bajo el que cuelgan los accesos de los clientes; null si no está configurado.</summary>
    public string? Domain => Normalize(options.Value.Domain);

    private static string CacheKey(string slug) => "client-host:" + slug;

    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        var colon = name.LastIndexOf(':');
        if (colon > 0 && !name.EndsWith(']'))
        {
            name = name[..colon];
        }

        return name.Length == 0 ? null : name;
    }
}
