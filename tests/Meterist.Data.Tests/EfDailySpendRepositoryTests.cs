using Meterist.Core.Models;
using Meterist.Core.Persistence;
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

    [Fact]
    public async Task UpsertAsync_ReportsInsertedThenUnchangedThenUpdated()
    {
        var vendorId = VendorCatalog.ClaudeEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);
        var ct = TestContext.Current.CancellationToken;

        var first = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 10m, 100m)], cancellationToken: ct));
        Assert.Equal(DailySpendChangeKind.Inserted, first.Kind);
        Assert.Null(first.Previous);

        var same = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 10m, 100m)], cancellationToken: ct));
        Assert.Equal(DailySpendChangeKind.Unchanged, same.Kind);

        var revised = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 10m, 120m)], cancellationToken: ct));
        Assert.Equal(DailySpendChangeKind.Updated, revised.Kind);
        Assert.Equal(100m, revised.Previous!.UsageOrOverage);
        Assert.Equal(120m, revised.Stored.UsageOrOverage);
    }

    [Fact]
    public async Task UpsertAsync_FiguresDifferingOnlyBeyondTheColumnScale_AreUnchangedNotUpdated()
    {
        var vendorId = VendorCatalog.ClaudeEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);
        var ct = TestContext.Current.CancellationToken;

        // A credits-times-rate figure carries more places than the column keeps;
        // it must not read as a change on every run.
        await _repository.UpsertAsync([UsageRecord(vendorId, date, 0m, 167.7234966m)], cancellationToken: ct);
        var again = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 0m, 167.7234966m)], cancellationToken: ct));

        Assert.Equal(DailySpendChangeKind.Unchanged, again.Kind);
    }

    [Fact]
    public async Task UpsertAsync_MonotonicKeepsTheHigherFigure_ReportsItAndKeepsTheEarlierSource()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 337.43m, DailySpendSources.CostsExport)],
            requiresMonotonicUsage: true, cancellationToken: ct);

        var change = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 319.97m, DailySpendSources.DailyUsageApi)],
            requiresMonotonicUsage: true, cancellationToken: ct));

        Assert.Equal(DailySpendChangeKind.KeptHigherUsage, change.Kind);
        Assert.Equal(337.43m, change.Stored.UsageOrOverage);
        Assert.Equal(319.97m, change.Incoming.UsageOrOverage);

        // The figure that was kept came from the earlier source, so the label must too.
        var stored = Assert.Single(await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), ct));
        Assert.Equal(DailySpendSources.CostsExport, stored.Source);
    }

    [Fact]
    public async Task UpsertAsync_MonotonicAcceptsAHigherFigure_AndTakesItsSource()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 100m, DailySpendSources.CostsExport)],
            requiresMonotonicUsage: true, cancellationToken: ct);

        var change = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 150m, DailySpendSources.DailyUsageApi)],
            requiresMonotonicUsage: true, cancellationToken: ct));

        Assert.Equal(DailySpendChangeKind.Updated, change.Kind);
        var stored = Assert.Single(await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), ct));
        Assert.Equal(150m, stored.UsageOrOverage);
        Assert.Equal(DailySpendSources.DailyUsageApi, stored.Source);
    }

    [Fact]
    public async Task UpsertAsync_SameFiguresButNewSource_IsAnUpdateSoTheLabelIsRecorded()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var date = new DateOnly(2026, 7, 22);
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 100m, DailySpendSources.CostsExport)],
            requiresMonotonicUsage: true, cancellationToken: ct);

        var change = Assert.Single(await _repository.UpsertAsync(
            [UsageRecord(vendorId, date, 460.27m, 100m, DailySpendSources.DailyUsageApi)],
            requiresMonotonicUsage: true, cancellationToken: ct));

        Assert.Equal(DailySpendChangeKind.Updated, change.Kind);
        var stored = Assert.Single(await _repository.GetAsync(
            "ecosync", vendorId, new DateRange(date, date), ct));
        Assert.Equal(DailySpendSources.DailyUsageApi, stored.Source);
    }

    [Fact]
    public async Task PlanUpsertAsync_WritesNothing_AndPredictsExactlyWhatTheRealUpsertDoes()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var ct = TestContext.Current.CancellationToken;
        var d1 = new DateOnly(2026, 7, 20);
        var d2 = new DateOnly(2026, 7, 21);
        var d3 = new DateOnly(2026, 7, 22);
        var d4 = new DateOnly(2026, 7, 23);

        // Existing: d1 will be unchanged, d2 raised, d3 pinned higher. d4 is new.
        await _repository.UpsertAsync(
            [
                UsageRecord(vendorId, d1, 10m, 100m, DailySpendSources.CostsExport),
                UsageRecord(vendorId, d2, 10m, 100m, DailySpendSources.CostsExport),
                UsageRecord(vendorId, d3, 10m, 500m, DailySpendSources.CostsExport),
            ],
            requiresMonotonicUsage: true, cancellationToken: ct);

        DailySpendRecord[] incoming =
        [
            UsageRecord(vendorId, d1, 10m, 100m, DailySpendSources.CostsExport),
            UsageRecord(vendorId, d2, 10m, 150m, DailySpendSources.DailyUsageApi),
            UsageRecord(vendorId, d3, 10m, 400m, DailySpendSources.DailyUsageApi),
            UsageRecord(vendorId, d4, 10m, 70m, DailySpendSources.DailyUsageApi),
        ];

        var before = await _repository.GetAsync("ecosync", vendorId, new DateRange(d1, d4), ct);
        var plan = await _repository.PlanUpsertAsync(incoming, requiresMonotonicUsage: true, cancellationToken: ct);
        var after = await _repository.GetAsync("ecosync", vendorId, new DateRange(d1, d4), ct);

        // Nothing was written: same rows, same figures, and no row for the new day.
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(3, after.Count);
        Assert.Equal(
            before.Select(r => (r.Date, r.UsageOrOverage, r.Source)),
            after.Select(r => (r.Date, r.UsageOrOverage, r.Source)));

        var real = await _repository.UpsertAsync(incoming, requiresMonotonicUsage: true, cancellationToken: ct);

        Assert.Equal(
            plan.Select(c => (c.Date, c.Kind, c.Stored.UsageOrOverage, c.Stored.Source)),
            real.Select(c => (c.Date, c.Kind, c.Stored.UsageOrOverage, c.Stored.Source)));
        Assert.Equal(
            [DailySpendChangeKind.Unchanged, DailySpendChangeKind.Updated, DailySpendChangeKind.KeptHigherUsage, DailySpendChangeKind.Inserted],
            plan.Select(c => c.Kind));
    }

    private static DailySpendRecord Record(Guid vendorId, DateOnly date, decimal grossSpend) => new()
    {
        TenantId = "ecosync",
        VendorId = vendorId,
        Date = date,
        GrossSpend = grossSpend,
        NetSpend = grossSpend,
    };

    private static DailySpendRecord UsageRecord(
        Guid vendorId, DateOnly date, decimal seatFee, decimal usage, string? source = null) => new()
    {
        TenantId = "ecosync",
        VendorId = vendorId,
        Date = date,
        SeatFee = seatFee,
        UsageOrOverage = usage,
        GrossSpend = seatFee + usage,
        NetSpend = seatFee + usage,
        Source = source,
    };
}
