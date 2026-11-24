var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Punto de entrada; público para las pruebas de integración (<c>WebApplicationFactory</c>).</summary>
public partial class Program;
