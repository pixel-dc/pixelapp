using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pixela.Api.Auth;
using Pixela.Application.Abstractions;
using Pixela.Application.Common;
using Pixela.Application.Users;
using Pixela.Domain.Users;

namespace Pixela.Api.Endpoints;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record TwoFactorLoginRequest(string? Challenge, string? Code, string? RecoveryCode);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CodeRequest(string? Code);

public sealed record DisableTwoFactorRequest(string? Password, string? Code, string? RecoveryCode);

/// <summary>Panel: primer arranque, sesión, cuenta propia y equipos.</summary>
public static class PanelEndpoints
{
    public const string LoginRateLimit = "panel-login";

    public static void MapPanel(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup("/panel");
        panel.AddEndpointFilter(RequireCsrfHeaderAsync);

        // Primer arranque: mientras no hay usuarios, el panel pide crear al dueño de la agencia.
        panel.MapGet("/setup", async (SetupService s, CancellationToken ct) => Results.Ok(new { pending = await s.IsPendingAsync(ct) }));
        panel.MapPost("/setup", async (SetupRequest r, SetupService s, CancellationToken ct) =>
            ToResult(await s.CreateOwnerAsync(r, ct), _ => new { ok = true }, StatusCodes.Status201Created)).RequireRateLimiting(LoginRateLimit);

        var auth = panel.MapGroup("/auth");
        auth.MapPost("/login", async (LoginRequest r, HttpContext http, AuthService s, IOptions<PanelOptions> o, CancellationToken ct) =>
            Respond(await s.LoginAsync(r.Email, r.Password, Client(http), ct), http, o.Value)).RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/login/2fa", async (TwoFactorLoginRequest r, HttpContext http, AuthService s, IOptions<PanelOptions> o, CancellationToken ct) =>
            Respond(await s.CompleteTwoFactorAsync(r.Challenge, r.Code, r.RecoveryCode, Client(http), ct), http, o.Value)).RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization(Policies.Session);
        auth.MapPost("/accept-invite", async (AcceptInviteRequest r, UserService users, CancellationToken ct) =>
            ToResult(await users.AcceptInviteAsync(r, ct), m => new { user = m })).RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/reset-password", async (ResetPasswordRequest r, UserService users, CancellationToken ct) =>
            ToResult(await users.ResetPasswordAsync(r, ct), _ => new { ok = true })).RequireRateLimiting(LoginRateLimit);

        var me = panel.MapGroup("/me").RequireAuthorization(Policies.Session);
        me.MapGet("/", async (HttpContext http, AuthService s, CancellationToken ct) =>
            await s.GetUserAsync(Principal(http).UserId, ct) is { } u ? Results.Ok(new { user = u }) : Results.Unauthorized());
        me.MapPut("/profile", async (ProfileRequest r, HttpContext http, ProfileService s, CancellationToken ct) =>
            ToResult(await s.UpdateProfileAsync(Principal(http), r, ct), _ => new { ok = true }));
        me.MapPut("/preferences", async (PreferencesRequest r, HttpContext http, ProfileService s, CancellationToken ct) =>
            ToResult(await s.SavePreferencesAsync(Principal(http), r, ct), _ => new { ok = true }));
        me.MapPut("/avatar", async (IFormFile file, HttpContext http, ProfileService s, CancellationToken ct) =>
                ToResult(await s.SetAvatarAsync(Principal(http), await ReadImageAsync(file, ct), ct), _ => new { ok = true }))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ImageUpload.MaxBytes + (64 * 1024)));
        me.MapDelete("/avatar", async (HttpContext http, ProfileService s, CancellationToken ct) =>
            ToResult(await s.RemoveAvatarAsync(Principal(http), ct), _ => new { ok = true }));
        me.MapPost("/password", async (ChangePasswordRequest r, HttpContext http, AuthService s, CancellationToken ct) =>
            ToResult(await s.ChangePasswordAsync(Principal(http), r.CurrentPassword, r.NewPassword, ct), _ => new { ok = true }));
        me.MapPost("/2fa/setup", async (HttpContext http, AuthService s, CancellationToken ct) =>
            ToResult(await s.BeginTotpSetupAsync(Principal(http), ct), t => new { secret = t.Secret, otpauth_uri = t.Uri }));
        me.MapPost("/2fa/enable", async (CodeRequest r, HttpContext http, AuthService s, CancellationToken ct) =>
            ToResult(await s.EnableTotpAsync(Principal(http), r.Code, ct), codes => new { recovery_codes = codes }));
        me.MapPost("/2fa/disable", async (DisableTwoFactorRequest r, HttpContext http, AuthService s, CancellationToken ct) =>
            ToResult(await s.DisableTotpAsync(Principal(http), r.Password, r.Code, r.RecoveryCode, ct), _ => new { ok = true }));

        // Fotos del equipo: solo de usuarios que la petición puede ver (mismo cliente, o la agencia).
        panel.MapGet("/avatars/{userId:guid}", async (Guid userId, HttpContext http, ProfileService s, CancellationToken ct) =>
            ServeImage(http, await s.GetAvatarAsync(userId, ct))).RequireAuthorization(Policies.Session);

        // Equipo del cliente elegido (el propio para un administrador de cliente; para la agencia, el del selector).
        var team = panel.MapGroup("/users").RequireAuthorization(Policies.ClientAdmin);
        MapTeam(team, scope => scope.AllClients ? null : scope.ClientId, requireClient: true);

        // Equipo de la agencia: lo ve la agencia; solo su dueño lo cambia (lo decide el servicio).
        var agencyTeam = panel.MapGroup("/agency/users").RequireAuthorization(Policies.Agency);
        MapTeam(agencyTeam, _ => null, requireClient: false);
    }

    private static void MapTeam(RouteGroupBuilder team, Func<IClientScope, Guid?> teamOf, bool requireClient)
    {
        IResult? Missing(IClientScope scope) => requireClient && teamOf(scope) is null
            ? Results.Json(new { error = new ServiceError("client_required", "Elige un cliente para ver su equipo.") }, statusCode: StatusCodes.Status400BadRequest)
            : null;

        team.MapGet("/", async (HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? ToResult(await s.ListAsync(Principal(http), teamOf(scope), ct), u => new { users = u }));
        team.MapGet("/invites", async (HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? ToResult(await s.ListInvitesAsync(Principal(http), teamOf(scope), ct), i => new { invites = i }));
        team.MapPost("/invites", async (InviteUserRequest r, HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? ToResult(await s.InviteAsync(Principal(http), teamOf(scope), r, ct),
                i => new { invite = new { i.Id, i.Email, i.Role, i.ExpiresAt }, token = i.Token, invite_path = $"/invitacion#token={i.Token}" }, StatusCodes.Status201Created));
        team.MapDelete("/invites/{id:guid}", async (Guid id, HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? NoContent(await s.RevokeInviteAsync(Principal(http), teamOf(scope), id, ct)));
        team.MapPost("/{id:guid}/reset-link", async (Guid id, ResetLinkRequest r, HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? ToResult(await s.CreateResetLinkAsync(Principal(http), teamOf(scope), id, r, ct),
                l => new { email = l.Email, token = l.Token, reset_path = $"/restablecer#token={l.Token}", reset_two_factor = l.ResetTwoFactor, expires_at = l.ExpiresAt }, StatusCodes.Status201Created));
        team.MapPatch("/{id:guid}", async (Guid id, UpdateUserRequest r, HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? ToResult(await s.UpdateAsync(Principal(http), teamOf(scope), id, r, ct), m => new { user = m }));
        team.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IClientScope scope, UserService s, CancellationToken ct) =>
            Missing(scope) ?? NoContent(await s.DeleteAsync(Principal(http), teamOf(scope), id, ct)));
    }

    private static async ValueTask<object?> RequireCsrfHeaderAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!SessionDefaults.IsSafeMethod(request.Method) && request.Headers[SessionDefaults.CsrfHeader] != SessionDefaults.CsrfValue)
        {
            return Results.Json(new { error = new ServiceError("csrf", $"Falta la cabecera {SessionDefaults.CsrfHeader}.") }, statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    private static IResult Respond(LoginResult result, HttpContext http, PanelOptions panel)
    {
        switch (result.Kind)
        {
            case LoginKind.Authenticated:
                http.Response.Cookies.Append(SessionDefaults.CookieName, result.SessionToken!, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = panel.SecureCookies,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = result.SessionExpiresAt,
                    IsEssential = true,
                });
                return Results.Ok(new { user = result.User });

            case LoginKind.TwoFactorRequired:
                return Results.Ok(new { two_factor_required = true, challenge = result.Challenge });

            default:
                // Mismo mensaje para correo inexistente, contraseña errónea, cuenta bloqueada, cliente suspendido o código incorrecto.
                return Results.Json(new { error = new ServiceError("invalid_credentials", "Credenciales incorrectas.") }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, AuthService auth, IOptions<PanelOptions> panel, CancellationToken ct)
    {
        await auth.LogoutAsync(Principal(http).SessionId, ct);
        http.Response.Cookies.Delete(SessionDefaults.CookieName, new CookieOptions { HttpOnly = true, Secure = panel.Value.SecureCookies, SameSite = SameSiteMode.Strict, Path = "/" });
        return Results.NoContent();
    }

    /// <summary>Lee la imagen subida con un tope: un archivo enorme se corta antes de llenar la memoria.</summary>
    internal static async Task<byte[]> ReadImageAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > ImageUpload.MaxBytes)
        {
            return [];
        }

        await using var stream = file.OpenReadStream();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    /// <summary>Imagen subida por un usuario: se sirve con su tipo real, sin que el navegador la interprete como otra cosa y sin permitir scripts.</summary>
    internal static IResult ServeImage(HttpContext http, StoredImage? image, string cache = "private, max-age=3600")
    {
        if (image is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        http.Response.Headers["Cache-Control"] = cache;
        return Results.File(image.Content, image.MimeType);
    }

    internal static IResult ToResult<T>(ServiceResult<T> result, Func<T, object> shape, int successStatus = StatusCodes.Status200OK)
    {
        if (result.IsSuccess)
        {
            return Results.Json(shape(result.Value!), statusCode: successStatus);
        }

        var status = result.Error!.Code switch
        {
            "validation_error" or "invalid_code" => StatusCodes.Status422UnprocessableEntity,
            "invalid_credentials" => StatusCodes.Status401Unauthorized,
            "already_enabled" or "invalid_state" or "user_exists" or "last_owner" or "slug_taken" or "already_in_use" => StatusCodes.Status409Conflict,
            "not_found" => StatusCodes.Status404NotFound,
            "forbidden" => StatusCodes.Status403Forbidden,
            "limit_reached" => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new { error = result.Error }, statusCode: status);
    }

    internal static IResult NoContent(ServiceResult<bool> result) => result.IsSuccess ? Results.NoContent() : ToResult(result, _ => new { });

    internal static IResult NotFound(string message) =>
        Results.Json(new { error = new ServiceError("not_found", message) }, statusCode: StatusCodes.Status404NotFound);

    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    internal static SessionPrincipal Principal(HttpContext http) => new(
        Guid.Parse(http.User.FindFirstValue(SessionDefaults.SessionIdClaim)!),
        Guid.Parse(http.User.FindFirstValue(SessionDefaults.UserIdClaim)!),
        http.User.FindFirstValue(SessionDefaults.ClientIdClaim) is { } c ? Guid.Parse(c) : null,
        Enum.Parse<UserRole>(http.User.FindFirstValue(SessionDefaults.RoleClaim)!));
}
