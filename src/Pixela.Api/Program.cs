using Pixela.Infrastructure;
using Pixela.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

StartupChecks.Validate(builder.Configuration);
builder.Services.AddPixelaInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

/// <summary>Punto de entrada; público para las pruebas de integración (<c>WebApplicationFactory</c>).</summary>
public partial class Program;
