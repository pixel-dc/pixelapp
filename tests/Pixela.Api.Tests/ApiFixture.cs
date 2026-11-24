using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Pixela.Api.Tests;

/// <summary>API real contra un PostgreSQL real (contenedor). Las migraciones se aplican al arrancar.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Program.cs lee la configuración al construir el host: las variables de entorno llegan a tiempo.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Secrets__MasterKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    /// <summary>Un DbContext con el alcance indicado: sin cliente = vista de agencia (todos); <paramref name="unset"/> = sin alcance.</summary>
    public (AsyncServiceScope Scope, AppDbContext Db) Database(Guid? clientId = null, bool unset = false)
    {
        var scope = Services.CreateAsyncScope();
        var clientScope = scope.ServiceProvider.GetRequiredService<ClientScope>();
        if (!unset)
        {
            if (clientId is { } id)
            {
                clientScope.SetClient(id);
            }
            else
            {
                clientScope.SetAllClients();
            }
        }

        return (scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
