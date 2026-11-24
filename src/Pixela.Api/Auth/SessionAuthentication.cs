using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pixela.Application.Abstractions;
using Pixela.Application.Users;
using Pixela.Domain.Users;

namespace Pixela.Api.Auth;

public static class SessionDefaults
{
    public const string Scheme = "Session";
    public const string CookieName = "px_session";
    public const string SessionIdClaim = "px:session_id";
    public const string UserIdClaim = "px:user_id";
    public const string ClientIdClaim = "px:client_id";
    public const string RoleClaim = "px:role";

    /// <summary>Cabecera obligatoria en peticiones que modifican datos con cookie (defensa CSRF adicional a SameSite=Strict).</summary>
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "Pixela";

    /// <summary>La agencia elige con qué cliente trabaja enviando su id en esta cabecera (el selector del panel).</summary>
    public const string ClientHeader = "X-Client-Id";

    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}

/// <summary>Políticas del panel, de menor a mayor permiso.</summary>
public static class Policies
{
    /// <summary>Cualquier sesión (también el operador): bandeja, perfil, cuenta.</summary>
    public const string Session = "panel:session";

    /// <summary>Administrador del cliente o agencia: conocimiento, conversaciones, contactos, uso y equipo del cliente.</summary>
    public const string ClientAdmin = "panel:client-admin";

    /// <summary>Agencia: clientes, assets, bots, llaves de Claude, apps de Meta, canales, conexiones y configuración.</summary>
    public const string Agency = "panel:agency";

    /// <summary>Dueño de la agencia: el equipo de la agencia.</summary>
    public const string AgencyOwner = "panel:agency-owner";

    public static void Add(Microsoft.AspNetCore.Authorization.AuthorizationBuilder builder) => builder
        .AddPolicy(Session, p => p.RequireClaim(SessionDefaults.SessionIdClaim))
        .AddPolicy(ClientAdmin, p => p.RequireClaim(SessionDefaults.RoleClaim, nameof(UserRole.AgencyOwner), nameof(UserRole.AgencyAdmin), nameof(UserRole.ClientAdmin)))
        .AddPolicy(Agency, p => p.RequireClaim(SessionDefaults.RoleClaim, nameof(UserRole.AgencyOwner), nameof(UserRole.AgencyAdmin)))
        .AddPolicy(AgencyOwner, p => p.RequireClaim(SessionDefaults.RoleClaim, nameof(UserRole.AgencyOwner)));
}

/// <summary>
/// Autentica la cookie del panel y fija el alcance de datos de la petición: un usuario de un cliente ve solo su cliente; la agencia ve todos,
/// o solo el que elija con <see cref="SessionDefaults.ClientHeader"/>.
/// </summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AuthService auth,
    ClientScope scope)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string ScopeErrorItem = "px:client_scope_error";
    private const string CsrfErrorItem = "px:csrf_error";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionDefaults.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        // Con cookie, toda petición que modifica datos debe traer la cabecera: un formulario de otro sitio no puede añadirla.
        if (!SessionDefaults.IsSafeMethod(Request.Method) && Request.Headers[SessionDefaults.CsrfHeader] != SessionDefaults.CsrfValue)
        {
            Context.Items[CsrfErrorItem] = true;
            return AuthenticateResult.Fail("csrf");
        }

        var principal = await auth.ValidateSessionAsync(token, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("session_invalid");
        }

        if (!await ApplyScopeAsync(principal))
        {
            Context.Items[ScopeErrorItem] = true;
            return AuthenticateResult.Fail("client_scope_invalid");
        }

        var claims = new List<Claim>
        {
            new(SessionDefaults.SessionIdClaim, principal.SessionId.ToString()),
            new(SessionDefaults.UserIdClaim, principal.UserId.ToString()),
            new(SessionDefaults.RoleClaim, principal.Role.ToString()),
        };
        if (principal.ClientId is { } clientId)
        {
            claims.Add(new Claim(SessionDefaults.ClientIdClaim, clientId.ToString()));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    /// <summary>Fija el alcance. False si la cabecera del selector no se puede atender (cliente inexistente, o un cliente pidiendo otro).</summary>
    private async Task<bool> ApplyScopeAsync(SessionPrincipal principal)
    {
        var raw = Request.Headers[SessionDefaults.ClientHeader].ToString();
        Guid? requested = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return false;
            }

            requested = parsed;
        }

        if (principal.ClientId is { } own)
        {
            // Un usuario de un cliente siempre trabaja en el suyo; pedir otro es un error, no una forma de cambiar.
            scope.SetClient(own);
            return requested is null || requested == own;
        }

        if (requested is { } clientId)
        {
            if (!await auth.ClientExistsAsync(clientId, Context.RequestAborted))
            {
                return false;
            }

            scope.SetClient(clientId);
        }
        else
        {
            scope.SetAllClients();
        }

        return true;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(CsrfErrorItem))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Response.WriteAsJsonAsync(new { error = new { code = "csrf", message = $"Falta la cabecera {SessionDefaults.CsrfHeader}." } });
        }

        if (Context.Items.ContainsKey(ScopeErrorItem))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Response.WriteAsJsonAsync(new { error = new { code = "client_scope_invalid", message = "Ese cliente no existe o no puedes trabajar con él." } });
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = new { code = "unauthorized", message = "Sesión ausente, vencida o inválida." } });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = new { code = "forbidden", message = "No tienes permiso para esta operación." } });
    }
}
