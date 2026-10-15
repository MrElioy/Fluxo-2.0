using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fluxo.Persistence;

// Solo para dotnet ef: generar migraciones no depende del arranque ni de la configuración de la API.
public sealed class FluxoDbContextFactory : IDesignTimeDbContextFactory<FluxoDbContext>
{
    // dotnet ef ejecuta la factoría desde el directorio del proyecto de persistencia.
    public FluxoDbContext CreateDbContext(string[] args)
    {
        var path = Path.GetFullPath("Data/fluxo.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FluxoDbContext(new DbContextOptionsBuilder<FluxoDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True").Options);
    }
}
