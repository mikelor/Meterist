using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfDailySpendRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfDailySpendRepository _repository;

    public EfDailySpendRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.EnsureCreated();
        _repository = new EfDailySpendRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_WritingTheSameDayTwice_UpdatesInPlaceRatherThanDuplicating()
    {
        var vendorId = VendorCatalog.GeminiEnterprise.Id;
        var date = new DateOnly(2026, 7, 20);

        await _repository.UpsertAsync([Record(vendorId, date, 100m)], TestContext.Current.CancellationToken);

        // Simulates an overlapping-timespan re-extraction of the same day with revised numbers —
        // this is the concrete mechanism behind "overlapping timespans just update what's there."
        await _repository.UpsertAsync([Record(vendorId, date, 150m)], TestContext.Current.CancellationToken);

        var stored = await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), TestContext.Current.CancellationToken);

        var record = Assert.Single(stored);
        Assert.Equal(150m, record.GrossSpend);
    }

    [Fact]
    public async Task GetAsync_ReturnsOnlyRecordsWithinRange()
    {
        var vendorId = VendorCatalog.GeminiEnterprise.Id;

        await _repository.UpsertAsync(
            [
                Record(vendorId, new DateOnly(2026, 7, 19), 10m),
                Record(vendorId, new DateOnly(2026, 7, 20), 20m),
                Record(vendorId, new DateOnly(2026, 7, 25), 30m),
            ],
            TestContext.Current.CancellationToken);

        var stored = await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stored.Count);
    }

    private static DailySpendRecord Record(Guid vendorId, DateOnly date, decimal grossSpend) => new()
    {
        TenantId = "ecosync",
        VendorId = vendorId,
        Date = date,
        GrossSpend = grossSpend,
        NetSpend = grossSpend,
    };
}
