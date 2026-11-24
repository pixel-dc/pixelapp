using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pixela.Api.Auth;
using Pixela.Api.Endpoints;
using Pixela.Application.Chat;
using Pixela.Application.Users;
using Pixela.Infrastructure;
using Pixela.Infrastructure.Http;
using Pixela.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Solo la llave maestra y la conexión viven fuera de la base: sin ellas no arranca.
StartupChecks.Validate(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = null;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddPixelaInfrastructure(builder.Configuration);

builder.Services
    .AddAuthentication(SessionDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionDefaults.Scheme, null);
Policies.Add(builder.Services.AddAuthorizationBuilder());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(PanelEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = http.RequestServices.GetRequiredService<IOptions<PanelOptions>>().Value.LoginRateLimitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Chat público (anónimo): por IP. Las sesiones y la configuración son baratas; los mensajes cuestan IA.
    o.AddPolicy(ChatEndpoints.SessionRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = http.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.SessionsPerMinutePerIp, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(ChatEndpoints.PollRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = http.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.PollsPerMinutePerIp, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(ChatEndpoints.MessageRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = http.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.MessagesPerMinutePerIp, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddProxyForwarding(builder.Configuration);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

app.UseProxyForwarding(builder.Configuration);
app.UseWebPanel();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPanel();
app.MapAgency();
app.MapBots();
app.MapChat();
app.MapMeta();

await app.RunAsync();

/// <summary>Punto de entrada; público para las pruebas de integración (<c>WebApplicationFactory</c>).</summary>
public partial class Program;
