namespace Pixela.Domain.Agency;

/// <summary>
/// Configuración de la agencia (Pixel DC). Hay una sola fila: Pixela es de una única agencia. Reemplaza lo que en FacilCorreo vivía en
/// variables de entorno (modelo, precios del modelo, retención) para que el <c>.env</c> solo lleve la llave maestra y la base.
/// </summary>
public class AgencySettings
{
    /// <summary>Siempre <see cref="SingletonId"/>.</summary>
    public int Id { get; set; } = SingletonId;

    public const int SingletonId = 1;

    public string Name { get; set; } = "Pixel DC";

    /// <summary>Logo de la agencia (https), para el panel y como marca por defecto del widget.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Color por defecto del widget (#rrggbb). Cada cliente y cada bot pueden cambiarlo.</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Textos del widget por defecto (lista cerrada de claves: placeholder, send, typing, consent…).</summary>
    public Dictionary<string, string> Texts { get; set; } = [];

    /// <summary>Modelo de Claude por defecto; cada bot puede elegir otro.</summary>
    public string DefaultModel { get; set; } = "claude-haiku-5-5";

    /// <summary>Esfuerzo de razonamiento (low, medium, high).</summary>
    public string Effort { get; set; } = "low";

    /// <summary>Precio de referencia del modelo, para estimar el costo de cada respuesta (USD por millón de tokens).</summary>
    public decimal InputUsdPerMillion { get; set; } = 0.10m;

    public decimal OutputUsdPerMillion { get; set; } = 0.50m;

    /// <summary>Días que se conservan las conversaciones inactivas.</summary>
    public int ChatRetentionDays { get; set; } = 90;

    /// <summary>Dirección pública del panel (https://bots.pixeldc.com): con ella se arman los enlaces de invitación y la URL del webhook de Meta.</summary>
    public string? PublicBaseUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
