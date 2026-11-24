using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pixela.Application.Abstractions;
using Pixela.Application.Chat;
using Pixela.Infrastructure;
using Pixela.Infrastructure.Persistence;
using Pixela.Migrator;

// Migración de una sola vez de FacilCorreo a Pixela. Ver tools/Pixela.Migrator/README.md.
//
//   dotnet run --project tools/Pixela.Migrator -- --tenant "Don Porko" --agency "Pixel DC" [--dry-run]
//
// Pixela:      ConnectionStrings__Default, Secrets__MasterKey, Meta__AttachmentsPath (las mismas del servidor de Pixela)
// FacilCorreo: MIGRATE_SOURCE_DB, MIGRATE_SOURCE_MASTER_KEY, MIGRATE_SOURCE_ATTACHMENTS (opcional), MIGRATE_SOURCE_META_APP_SECRET (opcional)

var tenants = new List<string>();
string? agency = null;
var dryRun = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tenant" when i + 1 < args.Length: tenants.Add(args[++i]); break;
        case "--agency" when i + 1 < args.Length: agency = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        default:
            Console.Error.WriteLine($"Argumento desconocido: {args[i]}");
            return 2;
    }
}

if (tenants.Count == 0 && agency is null)
{
    Console.Error.WriteLine("Indica al menos una cuenta: --tenant \"Don Porko\" (repetible) y, si aplica, --agency \"Pixel DC\".");
    return 2;
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
string Required(string name) => configuration[name] is { Length: > 0 } v ? v : throw new InvalidOperationException($"Falta la variable {name}.");

try
{
    StartupChecks.Validate(configuration);
    var services = new ServiceCollection().AddLogging().AddPixelaInfrastructure(configuration).BuildServiceProvider();
    await DatabaseInitializer.InitializeAsync(services);

    await using var scope = services.CreateAsyncScope();
    scope.ServiceProvider.GetRequiredService<ClientScope>().SetAllClients();
    var importer = new FacilCorreoImporter(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<ISecretProtector>(), TimeProvider.System);

    var report = await importer.ImportAsync(
        new ImportOptions(
            Required("MIGRATE_SOURCE_DB"),
            Required("MIGRATE_SOURCE_MASTER_KEY"),
            tenants,
            agency,
            configuration["MIGRATE_SOURCE_META_APP_SECRET"],
            configuration["MIGRATE_SOURCE_ATTACHMENTS"],
            configuration[$"{MetaOptions.Section}:AttachmentsPath"] ?? new MetaOptions().AttachmentsPath,
            dryRun),
        CancellationToken.None);

    Console.WriteLine(dryRun ? "Simulación terminada (no se guardó nada)." : "Migración terminada.");
    foreach (var (what, n) in report.Counts.OrderBy(c => c.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {what}: {n}");
    }

    if (report.Warnings.Count > 0)
    {
        Console.WriteLine("\nAvisos:");
        report.Warnings.ForEach(w => Console.WriteLine($"  - {w}"));
    }

    if (report.ToDo.Count > 0)
    {
        Console.WriteLine("\nPendiente a mano:");
        report.ToDo.ForEach(t => Console.WriteLine($"  - {t}"));
    }

    return 0;
}
catch (Exception ex) when (ex is ImportException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
