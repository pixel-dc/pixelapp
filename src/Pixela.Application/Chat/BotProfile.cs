using System.Globalization;
using System.Text;
using Pixela.Domain.Chat;

namespace Pixela.Application.Chat;

/// <summary>Datos de contacto y ubicación que el negocio configura en el bot; de ellos salen los enlaces que el bot comparte siempre igual.</summary>
public static class BotProfile
{
    public static bool TryParseCoordinates(string text, out double lat, out double lng)
    {
        lat = lng = 0;
        var parts = text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lng)
            && lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
    }

    public static string? CoordinatesText(Bot bot) =>
        bot.Latitude is { } lat && bot.Longitude is { } lng ? string.Create(CultureInfo.InvariantCulture, $"{lat}, {lng}") : null;

    /// <summary>Enlace de Google Maps: al punto exacto si hay coordenadas; si no, a la búsqueda de la dirección. No requiere llave de Google.</summary>
    public static string? MapsUrl(Bot bot)
    {
        if (bot.Latitude is { } lat && bot.Longitude is { } lng)
        {
            return string.Create(CultureInfo.InvariantCulture, $"https://www.google.com/maps/search/?api=1&query={lat},{lng}");
        }

        return string.IsNullOrWhiteSpace(bot.Address) ? null : "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(bot.Address.Trim());
    }

    /// <summary>Enlace que abre WhatsApp (la app o WhatsApp Web) con el negocio.</summary>
    public static string? WhatsAppUrl(Bot bot) => string.IsNullOrEmpty(bot.WhatsAppNumber) ? null : "https://wa.me/" + bot.WhatsAppNumber;

    /// <summary>Bloque del prompt con lo configurado. Vacío si el negocio no llenó nada.</summary>
    public static string PromptBlock(Bot bot)
    {
        var sb = new StringBuilder();
        if (MapsUrl(bot) is { } maps)
        {
            sb.Append("Ubicación del negocio (dato confirmado, aunque no esté en <contexto>)");
            if (!string.IsNullOrWhiteSpace(bot.Address))
            {
                sb.Append(": ").Append(bot.Address.Trim());
            }

            sb.Append(". Cuando pregunten dónde queda, cómo llegar o la dirección, responde con la dirección y SIEMPRE este enlace de Google Maps, copiado tal cual: ").Append(maps).Append('\n');
        }

        if (WhatsAppUrl(bot) is { } wa)
        {
            sb.Append("WhatsApp del negocio (dato confirmado): cuando pidan WhatsApp, un número de contacto, hablar con una persona o reservar por chat, da SIEMPRE este enlace, copiado tal cual: ").Append(wa).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(bot.CallToAction))
        {
            sb.Append("Llamado a la acción del negocio: cuando sea natural, cierra tu respuesta invitando a esto (sin repetirlo en cada mensaje): ").Append(bot.CallToAction.Trim()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(bot.Farewell))
        {
            sb.Append("Despedida del negocio: cuando el usuario se despida o agradezca y no haya más preguntas, termina con este mensaje (puedes adaptarlo al idioma del usuario): ").Append(bot.Farewell.Trim()).Append('\n');
        }

        return sb.ToString();
    }
}
