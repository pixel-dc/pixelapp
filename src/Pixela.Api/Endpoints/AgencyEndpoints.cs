using Pixela.Api.Auth;
using Pixela.Application.Abstractions;
using Pixela.Application.Agency;
using Pixela.Application.Clients;

namespace Pixela.Api.Endpoints;

public sealed record MoveBotRequest(Guid AssetId);

/// <summary>Lo que solo maneja la agencia: su configuración, los clientes y los assets. Un cliente ve su ficha y sus assets, sin cambiarlos.</summary>
public static class AgencyEndpoints
{
    public static void MapAgency(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup("/panel");

        var settings = panel.MapGroup("/agency/settings").RequireAuthorization(Policies.Agency);
        settings.MapGet("/", async (AgencySettingsService s, CancellationToken ct) => Results.Ok(new { settings = await s.GetAsync(ct) }));
        settings.MapPut("/", async (AgencySettingsRequest r, AgencySettingsService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAsync(r, ct), v => new { settings = v }));

        // Clientes: la agencia los ve todos (o el del selector); un administrador de cliente, solo el suyo.
        var clients = panel.MapGroup("/clients").RequireAuthorization(Policies.ClientAdmin);
        clients.MapGet("/", async (ClientService s, CancellationToken ct) => Results.Ok(new { clients = await s.ListAsync(ct) }));
        clients.MapGet("/{id:guid}", async (Guid id, ClientService s, CancellationToken ct) => PanelEndpoints.ToResult(await s.GetAsync(id, ct), c => new { client = c }));
        clients.MapPost("/", async (ClientRequest r, ClientService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.CreateAsync(r, ct), c => new { client = c }, StatusCodes.Status201Created)).RequireAuthorization(Policies.Agency);
        clients.MapPatch("/{id:guid}", async (Guid id, ClientRequest r, ClientService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAsync(id, r, ct), c => new { client = c })).RequireAuthorization(Policies.Agency);
        clients.MapDelete("/{id:guid}", async (Guid id, ClientService s, CancellationToken ct) =>
            PanelEndpoints.NoContent(await s.DeleteAsync(id, ct))).RequireAuthorization(Policies.Agency);

        var assets = panel.MapGroup("/assets").RequireAuthorization(Policies.ClientAdmin);
        assets.MapGet("/", async (Guid? clientId, ClientService s, CancellationToken ct) => Results.Ok(new { assets = await s.ListAssetsAsync(clientId, ct) }));
        assets.MapPost("/", async (AssetRequest r, IClientScope scope, ClientService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.CreateAssetAsync(r with { ClientId = r.ClientId ?? (scope.AllClients ? null : scope.ClientId) }, ct), a => new { asset = a }, StatusCodes.Status201Created))
            .RequireAuthorization(Policies.Agency);
        assets.MapPatch("/{id:guid}", async (Guid id, AssetRequest r, ClientService s, CancellationToken ct) =>
            PanelEndpoints.ToResult(await s.UpdateAssetAsync(id, r, ct), a => new { asset = a })).RequireAuthorization(Policies.Agency);
        assets.MapDelete("/{id:guid}", async (Guid id, ClientService s, CancellationToken ct) =>
            PanelEndpoints.NoContent(await s.DeleteAssetAsync(id, ct))).RequireAuthorization(Policies.Agency);
    }
}
