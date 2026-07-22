using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meterist.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MeteristDbContext against a single SQLite file with
    /// TenantId-scoped tables — one database for all tenants, not one file
    /// per tenant, so cross-tenant queries (the consulting benchmarking use
    /// case) stay possible. See docs/architecture.md §7.
    /// </summary>
    public static IServiceCollection AddMeteristData(
        this IServiceCollection services,
        string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meterist",
            "meterist.db");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        services.AddDbContext<MeteristDbContext>(options =>
            options.UseSqlite($"Data Source={path}"));

        return services;
    }
}
