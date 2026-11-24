using System.Net;
using System.Net.Sockets;
using Pixela.Application.Http;

namespace Pixela.Infrastructure.Http;

/// <summary>
/// Conexión endurecida contra SSRF para los <c>HttpClient</c> que llaman a direcciones de usuarios: resuelve el DNS ella misma y solo
/// conecta a IPs públicas (también frena el DNS rebinding, porque la comprobación ocurre al conectar).
/// </summary>
public static class SafeConnect
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowPrivate, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await System.Net.Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException("No se pudo resolver el host.");
        }

        // Si cualquiera de las IPs es privada se rechaza todo: no se deja elegir al atacante.
        if (!allowPrivate && addresses.Any(a => !SsrfGuard.IsPublic(a)))
        {
            throw new HttpRequestException("Destino bloqueado: el host resuelve a una dirección no pública.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException("No se pudo conectar.", last);
    }
}
