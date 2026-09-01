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

        await _repository.UpsertAsync(
            [Record(vendorId, date, 100m)], cancellationToken: TestContext.Current.CancellationToken);

        // Simulates an overlapping-timespan re-extraction of the same day with revised numbers —
        // this is the concrete mechanism behind "overlapping timespans just update what's there."
        await _repository.UpsertAsync(
            [Record(vendorId, date, 150m)], cancellationToken: TestContext.Current.CancellationToken);

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
            cancellationToken: TestContext.Current.CancellationToken);

        var stored = await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 20)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task UpsertAsync_RequiresMonotonicUsage_NeverLowersAnExistingDaysUsage()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);

        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, seatFee: 460.27m, usage: 337.43m)],
            requiresMonotonicUsage: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // Simulates a later pull where the vendor's retention window has trimmed
        // some of this day's rows even though the day is still nominally in-window.
        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, seatFee: 460.27m, usage: 319.97m)],
            requiresMonotonicUsage: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var stored = await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), TestContext.Current.CancellationToken);

        var record = Assert.Single(stored);
        Assert.Equal(337.43m, record.UsageOrOverage);
        Assert.Equal(797.70m, record.GrossSpend);
    }

    [Fact]
    public async Task UpsertAsync_RequiresMonotonicUsageFalse_StillOverwritesDownward()
    {
        var vendorId = VendorCatalog.ClaudeEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);

        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, seatFee: 460.27m, usage: 100m)],
            requiresMonotonicUsage: false,
            cancellationToken: TestContext.Current.CancellationToken);

        // A legitimate downward revision (Claude Enterprise's revision window) must
        // still win when the vendor doesn't require monotonic usage.
        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, seatFee: 460.27m, usage: 50m)],
            requiresMonotonicUsage: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var stored = await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), TestContext.Current.CancellationToken);

        var record = Assert.Single(stored);
        Assert.Equal(50m, record.UsageOrOverage);
    }

    private static DailySpendRecord Record(Guid vendorId, DateOnly date, decimal grossSpend) => new()
    {
        TenantId = "ecosync",
        VendorId = vendorId,
        Date = date,
        GrossSpend = grossSpend,
        NetSpend = grossSpend,
    };

    private static DailySpendRecord UsageRecord(Guid vendorId, DateOnly date, decimal seatFee, decimal usage) => new()
    {
        TenantId = "ecosync",
        VendorId = vendorId,
        Date = date,
        SeatFee = seatFee,
        UsageOrOverage = usage,
        GrossSpend = seatFee + usage,
        NetSpend = seatFee + usage,
    };
}
