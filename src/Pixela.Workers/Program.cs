using Pixela.Infrastructure;
using Pixela.Workers;

var builder = Host.CreateApplicationBuilder(args);

StartupChecks.Validate(builder.Configuration);
builder.Services.AddPixelaInfrastructure(builder.Configuration);
builder.Services.AddHostedService<KnowledgeIndexWorker>();

var host = builder.Build();
await host.RunAsync();
