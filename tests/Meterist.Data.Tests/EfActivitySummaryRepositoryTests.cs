using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfActivitySummaryRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 18);
    private static readonly DateOnly D2 = new(2026, 11, 19);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfActivitySummaryRepository _repository;

    public EfActivitySummaryRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.Migrate();
        _repository = new EfActivitySummaryRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Upsert_StoresEveryFieldAndReadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _repository.UpsertAsync(
        [
            new ActivitySummaryRecord
            {
                TenantId = "t1", VendorId = Vendor, Date = D1,
                DailyActiveUsers = 3, WeeklyActiveUsers = 6, MonthlyActiveUsers = 9,
                AssignedSeatCount = 10, PendingInviteCount = 1,
                DailyAdoptionRate = 30m, WeeklyAdoptionRate = 60.5m, MonthlyAdoptionRate = 90m,
            },
            new ActivitySummaryRecord { TenantId = "t1", VendorId = Vendor, Date = D1, Product = "chat", DailyActiveUsers = 2 },
        ], ct);

        Assert.Equal(new ActivitySummaryUpsertResult(2, 0, 0), result);

        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct);
        Assert.Equal(["all", "chat"], stored.Select(r => r.Product));

        var all = stored[0];
        Assert.Equal((3, 6, 9), (all.DailyActiveUsers, all.WeeklyActiveUsers, all.MonthlyActiveUsers));
        Assert.Equal((10, 1), (all.AssignedSeatCount, all.PendingInviteCount));
        Assert.Equal((30m, 60.5m, 90m), (all.DailyAdoptionRate, all.WeeklyAdoptionRate, all.MonthlyAdoptionRate));
        Assert.Null(stored[1].AssignedSeatCount);   // seats are organisation-wide, so a product row does not carry them
    }

    [Fact]
    public async Task Upsert_TheSameDayAgain_IsUnchanged_AndAChangedFigureUpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Row(D1, daily: 3, seats: 10)], ct);

        var same = await _repository.UpsertAsync([Row(D1, daily: 3, seats: 10)], ct);
        var changed = await _repository.UpsertAsync([Row(D1, daily: 4, seats: 11)], ct);

        Assert.Equal(new ActivitySummaryUpsertResult(0, 0, 1), same);
        Assert.Equal(new ActivitySummaryUpsertResult(0, 1, 0), changed);
        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
        Assert.Equal((4, 11), (stored.DailyActiveUsers, stored.AssignedSeatCount));
    }

    [Fact]
    public async Task Upsert_ADayAbsentFromALaterPull_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Row(D1, daily: 3, seats: 10), Row(D2, daily: 5, seats: 10)], ct);

        await _repository.UpsertAsync([Row(D2, daily: 5, seats: 10)], ct);

        Assert.Equal(2, (await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct)).Count);
    }

    [Fact]
    public async Task PlanUpsert_ReportsWhatARealRunWouldDo_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Row(D1, daily: 3, seats: 10)], ct);

        var plan = await _repository.PlanUpsertAsync([Row(D1, daily: 4, seats: 10), Row(D2, daily: 1, seats: 10)], ct);

        Assert.Equal(new ActivitySummaryUpsertResult(1, 1, 0), plan);
        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct);
        var only = Assert.Single(stored);
        Assert.Equal(3, only.DailyActiveUsers);
    }

    [Fact]
    public async Task Upsert_TenantsAndVendorsAreSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Row(D1, daily: 3, seats: 10)], ct);
        await _repository.UpsertAsync(
            [new ActivitySummaryRecord { TenantId = "t2", VendorId = Vendor, Date = D1, DailyActiveUsers = 99 }], ct);

        var t1 = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
        var other = await _repository.GetAsync("t1", VendorCatalog.ChatGptEnterprise.Id, new DateRange(D1, D1), ct);

        Assert.Equal(3, t1.DailyActiveUsers);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Upsert_ADuplicateKeyInOneBatch_IsRejected_AndStagesNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.UpsertAsync([Row(D1, daily: 1, seats: 1), Row(D2, daily: 2, seats: 2), Row(D1, daily: 3, seats: 3)], ct));

        // A later save on the same context must not persist the rejected batch's rows.
        await _context.SaveChangesAsync(ct);
        Assert.Empty(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct));
    }

    [Fact]
    public async Task Upsert_ABlankProduct_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.UpsertAsync(
            [new ActivitySummaryRecord { TenantId = "t1", VendorId = Vendor, Date = D1, Product = " " }], ct));

        Assert.Contains("no product", ex.Message);
    }

    private static ActivitySummaryRecord Row(DateOnly date, int daily, int seats) => new()
    {
        TenantId = "t1",
        VendorId = Vendor,
        Date = date,
        DailyActiveUsers = daily,
        AssignedSeatCount = seats,
    };
}
