using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MonitorVendas.Api.Data;

// Usada apenas pelo dotnet-ef para gerar migrações, sem subir o Program.cs.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=monitor_vendas;Username=postgres;Password=postgres")
            .Options;

        return new AppDbContext(options);
    }
}
