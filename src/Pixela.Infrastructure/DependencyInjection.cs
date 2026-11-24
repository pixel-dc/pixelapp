using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pixela.Application.Abstractions;
using Pixela.Infrastructure.Persistence;
using Pixela.Infrastructure.Security;

namespace Pixela.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPixelaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Default.");

        services.AddSingleton(TimeProvider.System);

        // ClientScope es scoped: una instancia por petición, compartida por la autenticación y el DbContext.
        services.AddScoped<ClientScope>();
        services.AddScoped<IClientScope>(sp => sp.GetRequiredService<ClientScope>());

        // jsonb con Dictionary<string,string> (textos de la marca) exige habilitar JSON dinámico.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build());
        services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.Section));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        return services;
    }
}
