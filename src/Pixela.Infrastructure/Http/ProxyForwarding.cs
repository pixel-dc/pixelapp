using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pixela.Infrastructure.Http;

/// <summary>
/// Confianza en el proxy inverso (Caddy). Detrás de él, <c>RemoteIpAddress</c> sería siempre la IP del proxy y
/// se rompería el límite de intentos por IP del inicio de sesión y del chat público.
/// Solo se leen X-Forwarded-* de proxies explícitamente de confianza: si no, cualquiera podría falsear su IP.
/// </summary>
public static class ProxyForwarding
{
    public const string Section = "Proxy";

    /// <summary>Apagado por defecto. Con <c>Proxy:Enabled=true</c> se confía en loopback más lo listado.</summary>
    public static IServiceCollection AddProxyForwarding(this IServiceCollection services, IConfiguration config)
    {
        var enabled = config.GetValue<bool>($"{Section}:Enabled");
        if (!enabled)
        {
            return services;
        }

        var proxies = config.GetSection($"{Section}:KnownProxies").Get<string[]>() ?? [];
        var networks = config.GetSection($"{Section}:KnownNetworks").Get<string[]>() ?? [];

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Un solo salto de proxy: ignora cadenas X-Forwarded-For que el cliente haya añadido por su cuenta.
            o.ForwardLimit = 1;
            foreach (var p in proxies)
            {
                o.KnownProxies.Add(IPAddress.Parse(p));
            }

            foreach (var n in networks)
            {
                o.KnownIPNetworks.Add(ParseNetwork(n));
            }
        });
        return services;
    }

    public static IApplicationBuilder UseProxyForwarding(this IApplicationBuilder app, IConfiguration config)
    {
        // Debe ir antes de todo lo que lea la IP o el esquema (límite de tasa, autenticación, redirección HTTPS).
        return config.GetValue<bool>($"{Section}:Enabled") ? app.UseForwardedHeaders() : app;
    }

    private static System.Net.IPNetwork ParseNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix))
        {
            throw new InvalidOperationException($"Proxy:KnownNetworks contiene un CIDR inválido: '{cidr}'.");
        }

        return new System.Net.IPNetwork(ip, prefix);
    }
}
