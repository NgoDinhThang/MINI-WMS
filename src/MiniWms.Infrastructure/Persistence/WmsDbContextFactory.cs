using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MiniWms.Infrastructure.Persistence;

public sealed class WmsDbContextFactory : IDesignTimeDbContextFactory<WmsDbContext>
{
    public WmsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WmsDatabase");

        // Model inspection and migration generation do not open a database connection.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Host=127.0.0.1;Port=5432;Database=mini_wms;Username=postgres";
        }

        var options = new DbContextOptionsBuilder<WmsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WmsDbContext(options);
    }
}
