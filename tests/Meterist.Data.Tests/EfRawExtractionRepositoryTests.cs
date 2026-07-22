using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfRawExtractionRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfRawExtractionRepository _repository;

    public EfRawExtractionRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.EnsureCreated();
        _repository = new EfRawExtractionRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_WritingTheSameDayTwice_ReplacesRatherThanDuplicating()
    {
        var vendorId = VendorCatalog.GeminiEnterprise.Id;
        var date = new DateOnly(2026, 7, 20);

        await _repository.UpsertAsync("ecosync", vendorId, RowsFor(date, cost: 10m), DateTime.UtcNow,
            TestContext.Current.CancellationToken);

        // Same natural key (tenant, vendor, date) — this is "latest pull wins," the
        // deliberate scope cut vs. keeping a full audit history of every pull.
        await _repository.UpsertAsync("ecosync", vendorId, RowsFor(date, cost: 20m), DateTime.UtcNow,
            TestContext.Current.CancellationToken);

        var stored = await _context.RawDailyExtractionRecords
            .Where(r => r.TenantId == "ecosync" && r.VendorId == vendorId && r.Date == date)
            .ToListAsync(TestContext.Current.CancellationToken);

        var record = Assert.Single(stored);
        Assert.Contains("20", record.RecordsJson);
    }

    private static IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsFor(
        DateOnly date, decimal cost) => new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
    {
        [date] = [new Dictionary<string, object?> { ["Cost"] = cost }],
    };
}
