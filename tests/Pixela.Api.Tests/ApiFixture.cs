using System.Net.Http.Json;
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
        Environment.SetEnvironmentVariable("Panel__SecureCookies", "false"); // el servidor de pruebas habla http
        Environment.SetEnvironmentVariable("Panel__LoginRateLimitPerMinute", "10000");
        Environment.SetEnvironmentVariable("ClientApps__Domain", ApiFixture.AppsDomain); // el acceso de cada cliente: {slug}.apps.pixela.test
    }

    public const string Password = "una frase larga de prueba 2026";

    public const string AppsDomain = "apps.pixela.test";

    /// <summary>Web Push simulado: registra lo que se enviaría a cada dispositivo (nunca sale a la red).</summary>
    public FakeWebPush Push { get; } = new();

    /// <summary>Cliente HTTP que entra por el acceso propio de un cliente (<c>{slug}.apps.pixela.test</c>), sin sesión.</summary>
    public HttpClient CreateClientFor(string slug)
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{slug}.{AppsDomain}") });
        http.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        return http;
    }

    /// <summary>Inicia sesión por el acceso de un cliente; falla la prueba si no entra.</summary>
    public async Task<HttpClient> LoginAtAsync(string slug, string email)
    {
        var http = CreateClientFor(slug);
        var response = await http.PostAsJsonAsync("/panel/auth/login", new { email, password = Password });
        Assert.True(response.IsSuccessStatusCode, $"login {email} en {slug}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return http;
    }

    /// <summary>Crea un usuario con <see cref="Password"/> directamente en la base (sin invitación).</summary>
    public async Task<Guid> CreateUserAsync(string email, Pixela.Domain.Users.UserRole role, Guid? clientId)
    {
        var hash = Services.GetRequiredService<Pixela.Application.Users.IPasswordHasher>().Hash(Password);
        var id = Guid.CreateVersion7();
        await WithDbAsync(async db =>
        {
            db.Users.Add(new Pixela.Domain.Users.User { Id = id, Email = email, PasswordHash = hash, Role = role, ClientId = clientId, CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Cliente HTTP con la sesión iniciada (cookie) y la cabecera anti-CSRF; <paramref name="clientId"/> = el selector de cliente.</summary>
    public async Task<HttpClient> LoginAsync(string email, Guid? clientId = null)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Add("X-Requested-With", "Pixela");
        var response = await http.PostAsJsonAsync("/panel/auth/login", new { email, password = Password });
        Assert.True(response.IsSuccessStatusCode, $"login {email}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        if (clientId is { } c)
        {
            http.DefaultRequestHeaders.Add("X-Client-Id", c.ToString());
        }

        return http;
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
            services.RemoveAll<Pixela.Application.Users.IWebPush>();
            services.AddSingleton<Pixela.Application.Users.IWebPush>(Push);
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

public sealed class FakeWebPush : Pixela.Application.Users.IWebPush
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Endpoint, string Payload)> Sent { get; } = new();

    /// <summary>Direcciones que el servicio da por vencidas (responde 410).</summary>
    public HashSet<string> Gone { get; } = [];

    public string PublicKey => "BFakeServerKey";

    public Task<Pixela.Application.Users.PushOutcome> SendAsync(Pixela.Application.Users.PushTarget target, string payload, string? topic, CancellationToken ct)
    {
        lock (Gone)
        {
            if (Gone.Contains(target.Endpoint))
            {
                return Task.FromResult(Pixela.Application.Users.PushOutcome.Gone);
            }
        }

        Sent.Enqueue((target.Endpoint, payload));
        return Task.FromResult(Pixela.Application.Users.PushOutcome.Sent);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
