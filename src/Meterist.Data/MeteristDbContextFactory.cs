using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Meterist.Data;

/// <summary>
/// Lets `dotnet ef migrations` create a MeteristDbContext at design time without
/// a runnable host project. The connection string here is design-time only —
/// Meterist.Cli configures the real one at runtime.
/// </summary>
public sealed class MeteristDbContextFactory : IDesignTimeDbContextFactory<MeteristDbContext>
{
    public MeteristDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MeteristDbContext>();
        optionsBuilder.UseSqlite("Data Source=meterist.design.db");
        return new MeteristDbContext(optionsBuilder.Options);
    }
}
