using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
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

    /// <summary>Claude simulado: registra lo que recibe y responde con el primer fragmento del contexto (nunca sale a la red).</summary>
    public FakeAiGateway Ai { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiGateway>();
            services.AddSingleton<IAiGateway>(Ai);
        });
    }

    /// <summary>Base en vista de agencia (sin filtro), de esta API o de una variante creada con <c>WithWebHostBuilder</c>.</summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action, WebApplicationFactory<Program>? factory = null)
    {
        await using var scope = (factory ?? this).Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Un servicio con el alcance indicado (sin cliente = vista de agencia).</summary>
    public (AsyncServiceScope Scope, T Service) Service<T>(Guid? clientId = null)
        where T : notnull
    {
        var (scope, _) = Database(clientId);
        return (scope, scope.ServiceProvider.GetRequiredService<T>());
    }

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

public sealed class FakeAiGateway : IAiGateway
{
    public List<AiRequest> Requests { get; } = [];

    public bool RequiresKey => true;

    public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var text = request.Context.Count > 0 ? "Según el negocio: " + request.Context[0].Text : "Te ayudo.";
        return Task.FromResult(new AiReply(text, 1000, 100, request.Model ?? "?"));
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
