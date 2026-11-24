using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pixela.Application.Abstractions;
using Pixela.Application.Clients;
using Pixela.Application.Common;
using Pixela.Domain.Agency;

namespace Pixela.Application.Agency;

/// <summary>Los campos que no vienen (null) no se tocan; un texto vacío borra los opcionales.</summary>
public sealed record AgencySettingsRequest(
    string? Name = null,
    string? LogoUrl = null,
    string? PrimaryColor = null,
    Dictionary<string, string>? Texts = null,
    string? DefaultModel = null,
    string? Effort = null,
    decimal? InputUsdPerMillion = null,
    decimal? OutputUsdPerMillion = null,
    int? ChatRetentionDays = null,
    string? PublicBaseUrl = null);

public sealed record AgencySettingsView(
    string Name, string? LogoUrl, string? PrimaryColor, IReadOnlyDictionary<string, string> Texts, string DefaultModel, string Effort,
    decimal InputUsdPerMillion, decimal OutputUsdPerMillion, int ChatRetentionDays, string? PublicBaseUrl, DateTimeOffset UpdatedAt);

/// <summary>
/// Configuración general de la agencia (lo que en FacilCorreo vivía en variables de entorno): marca por defecto del widget, modelo, esfuerzo y
/// precios de referencia de Claude, retención y dirección pública del panel.
/// </summary>
public sealed partial class AgencySettingsService(IAppDbContext db, AgencySettingsReader reader, TimeProvider clock)
{
    public static readonly string[] Efforts = ["low", "medium", "high"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9.\\-]{2,99}$")]
    private static partial Regex ModelPattern();

    public async Task<AgencySettingsView> GetAsync(CancellationToken ct) => ToView(await reader.GetAsync(ct));

    public async Task<ServiceResult<AgencySettingsView>> UpdateAsync(AgencySettingsRequest r, CancellationToken ct)
    {
        var s = await db.AgencySettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new AgencySettings();
            db.AgencySettings.Add(s);
        }

        var fields = new Dictionary<string, string>();
        if (r.Name is not null)
        {
            var name = r.Name.Trim();
            if (name.Length is 0 or > 200)
            {
                fields["name"] = "Escribe el nombre de la agencia.";
            }

            s.Name = name;
        }

        if (r.LogoUrl is not null)
        {
            var url = r.LogoUrl.Trim();
            if (url.Length > 0 && !IsHttps(url))
            {
                fields["logo_url"] = "El logo debe ser una dirección https.";
            }

            s.LogoUrl = url.Length == 0 ? null : url;
        }

        if (r.PrimaryColor is not null)
        {
            var color = r.PrimaryColor.Trim();
            if (color.Length > 0 && !ColorPattern().IsMatch(color))
            {
                fields["primary_color"] = "Usa un color #rrggbb.";
            }

            s.PrimaryColor = color.Length == 0 ? null : color.ToLowerInvariant();
        }

        if (r.Texts is not null)
        {
            var texts = new Dictionary<string, string>();
            foreach (var (key, value) in r.Texts)
            {
                var v = value?.Trim() ?? "";
                if (!ClientService.TextKeys.Contains(key) || v.Length > 300)
                {
                    fields["texts"] = $"Texto no válido: {key}.";
                }
                else if (v.Length > 0)
                {
                    texts[key] = v;
                }
            }

            s.Texts = texts;
        }

        if (r.DefaultModel is not null)
        {
            var model = r.DefaultModel.Trim();
            if (!ModelPattern().IsMatch(model))
            {
                fields["default_model"] = "Escribe el identificador del modelo de Claude (por ejemplo claude-haiku-5-5).";
            }

            s.DefaultModel = model;
        }

        if (r.Effort is not null)
        {
            var effort = r.Effort.Trim().ToLowerInvariant();
            if (!Efforts.Contains(effort))
            {
                fields["effort"] = "Valores: low, medium, high.";
            }

            s.Effort = effort;
        }

        if (r.InputUsdPerMillion is { } input)
        {
            if (input is < 0 or > 1000)
            {
                fields["input_usd_per_million"] = "Precio no válido.";
            }

            s.InputUsdPerMillion = input;
        }

        if (r.OutputUsdPerMillion is { } output)
        {
            if (output is < 0 or > 1000)
            {
                fields["output_usd_per_million"] = "Precio no válido.";
            }

            s.OutputUsdPerMillion = output;
        }

        if (r.ChatRetentionDays is { } days)
        {
            if (days is < 1 or > 3650)
            {
                fields["chat_retention_days"] = "Entre 1 y 3.650 días.";
            }

            s.ChatRetentionDays = days;
        }

        if (r.PublicBaseUrl is not null)
        {
            var url = r.PublicBaseUrl.Trim().TrimEnd('/');
            if (url.Length > 0 && (!IsHttps(url) || new Uri(url).AbsolutePath != "/"))
            {
                fields["public_base_url"] = "Escribe solo la dirección https del panel, sin ruta (por ejemplo https://bots.pixeldc.com).";
            }

            s.PublicBaseUrl = url.Length == 0 ? null : url;
        }

        if (fields.Count > 0)
        {
            return ServiceResult<AgencySettingsView>.Fail("validation_error", fields.Values.First(), fields);
        }

        s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        reader.Invalidate();
        return ServiceResult<AgencySettingsView>.Ok(ToView(s));
    }

    private static bool IsHttps(string url) =>
        url.Length <= 2000 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static AgencySettingsView ToView(AgencySettings s) =>
        new(s.Name, s.LogoUrl, s.PrimaryColor, s.Texts ?? [], s.DefaultModel, s.Effort, s.InputUsdPerMillion, s.OutputUsdPerMillion, s.ChatRetentionDays, s.PublicBaseUrl, s.UpdatedAt);
}
