using Pixela.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

StartupChecks.Validate(builder.Configuration);
builder.Services.AddPixelaInfrastructure(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
