using System.Net;
using System.Net.Sockets;

namespace Pixela.Application.Http;

/// <summary>Llamadas salientes a direcciones que escribe un usuario (páginas web del conocimiento, servidores MCP).</summary>
public sealed class OutboundHttpOptions
{
    public const string Section = "OutboundHttp";

    /// <summary>Permite URLs http (solo desarrollo y pruebas).</summary>
    public bool AllowHttp { get; set; }

    /// <summary>Permite destinos en redes privadas/loopback (solo desarrollo y pruebas). En producción debe ser false (SSRF).</summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>Defensa contra SSRF: una URL que escribe un usuario (página web, servidor MCP) jamás debe poder apuntar a la red interna ni a los metadatos de la nube.</summary>
public static class SsrfGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC // fc00::/7 (ULA)
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)); // 2001:db8::/32 (documentación)
        }

        var v = address.GetAddressBytes();
        return !(v[0] == 10
            || (v[0] == 172 && v[1] is >= 16 and <= 31)
            || (v[0] == 192 && v[1] == 168)
            || v[0] == 127
            || v[0] == 0
            || (v[0] == 169 && v[1] == 254) // link-local y metadatos de la nube (169.254.169.254)
            || (v[0] == 100 && v[1] is >= 64 and <= 127) // CGNAT
            || (v[0] == 192 && v[1] == 0 && v[2] == 0) // IETF
            || (v[0] == 198 && v[1] is 18 or 19) // benchmarking
            || v[0] >= 224); // multicast y reservadas
    }
}

public static class OutboundUrlValidator
{
    /// <summary>Devuelve un mensaje de error, o null si la URL es aceptable.</summary>
    public static string? Validate(string? url, OutboundHttpOptions options)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2000 || url.Any(char.IsControl))
        {
            return "La URL no es válida.";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "La URL no es válida.";
        }

        if (uri.Scheme != "https" && !(options.AllowHttp && uri.Scheme == "http"))
        {
            return "La URL debe usar https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "La URL no puede incluir credenciales.";
        }

        if (options.AllowPrivateNetworks)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || !host.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            return "La URL debe apuntar a un host público.";
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !SsrfGuard.IsPublic(ip)
            ? "La URL debe apuntar a un host público."
            : null;
    }
}
