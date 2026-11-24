using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pixela.Application.Abstractions;

namespace Pixela.Infrastructure.Persistence;

/// <summary>Solo para <c>dotnet ef</c>. La cadena real viene de la configuración en tiempo de ejecución.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("PIXELA_DB")
            ?? "Host=localhost;Port=5432;Database=pixela;Username=pixela;Password=dev_only_change_me";

        var scope = new ClientScope();
        scope.SetAllClients();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        return new AppDbContext(options, scope);
    }
}
