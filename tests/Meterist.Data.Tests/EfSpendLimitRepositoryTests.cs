using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfSpendLimitRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 18);
    private static readonly DateOnly D2 = new(2026, 11, 20);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfSpendLimitRepository _repository;

    public EfSpendLimitRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.Migrate();
        _repository = new EfSpendLimitRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Replace_StoresEveryFieldAndReadsItBack_NullLimitStaysNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
        [
            new SpendLimitRecord
            {
                TenantId = "t1", VendorId = Vendor, SnapshotDate = D1, MemberId = "m1",
                Email = "a@example.test", Name = "Alex Example",
                LimitAmount = 500.25m, PeriodToDateSpend = 123.4567m, PriorPeriodSpend = 77.125m, Currency = "USD", Period = "monthly",
                SourceType = "rbac_group", SourceDetail = "grp_1", SpendLimitId = "spl_1",
            },
            Row("m2", D1, limit: null, spent: 0m),
        ], ct);

        Assert.Equal(new SpendLimitSnapshotResult(2, 0, 0, 0), result);
        var snapshot = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        Assert.NotNull(snapshot);
        Assert.Equal(D1, snapshot.Date);

        var first = snapshot.Limits.Single(l => l.MemberId == "m1");
        Assert.Equal((500.25m, 123.4567m, 77.125m), (first.LimitAmount, first.PeriodToDateSpend, first.PriorPeriodSpend));
        Assert.Equal(("USD", "monthly", "rbac_group", "grp_1", "spl_1"), (first.Currency, first.Period, first.SourceType, first.SourceDetail, first.SpendLimitId));
        Assert.Equal(("a@example.test", "Alex Example"), (first.Email, first.Name));

        var none = snapshot.Limits.Single(l => l.MemberId == "m2");
        Assert.Null(none.LimitAmount);   // no limit is not zero
        Assert.Null(none.PriorPeriodSpend);   // unavailable is not zero
        Assert.Equal(0m, none.PeriodToDateSpend);
    }

    [Fact]
    public async Task Replace_RetakingTheDay_IsTheWholeSnapshot_ChangedUpdated_AbsentRemoved_SameUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [Row("m1", D1, 100m, 10m), Row("m2", D1, 100m, 20m), Row("m3", D1, 100m, 30m)], ct);

        var result = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [Row("m1", D1, 100m, 10m), Row("m2", D1, 100m, 25m), Row("m4", D1, 100m, 1m)], ct);

        Assert.Equal(new SpendLimitSnapshotResult(1, 1, 1, 1), result);
        var snapshot = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        Assert.Equal(["m1", "m2", "m4"], snapshot!.Limits.Select(l => l.MemberId));
        Assert.Equal(25m, snapshot.Limits.Single(l => l.MemberId == "m2").PeriodToDateSpend);
    }

    [Fact]
    public async Task Replace_AnotherDay_IsKeptAlongside_AndLatestOnOrBeforeFindsTheRightOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 100m, 10m)], ct);
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D2, [Row("m1", D2, 100m, 40m)], ct);

        var latest = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        var earlier = await _repository.GetLatestSnapshotAsync("t1", Vendor, D1.AddDays(1), ct);
        var before = await _repository.GetLatestSnapshotAsync("t1", Vendor, D1.AddDays(-1), ct);

        Assert.Equal(D2, latest!.Date);
        Assert.Equal(D1, earlier!.Date);
        Assert.Equal(10m, Assert.Single(earlier.Limits).PeriodToDateSpend);   // the earlier day was not touched
        Assert.Null(before);
    }

    [Fact]
    public async Task Replace_AChangedPriorPeriodSpend_CountsAsAnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 100m, 10m)], ct);

        var result = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [new SpendLimitRecord { TenantId = "t1", VendorId = Vendor, SnapshotDate = D1, MemberId = "m1", LimitAmount = 100m, PeriodToDateSpend = 10m, PriorPeriodSpend = 5m }], ct);

        Assert.Equal(new SpendLimitSnapshotResult(0, 1, 0, 0), result);
    }

    [Fact]
    public async Task Plan_ReportsWhatARealRunWouldDo_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 100m, 10m), Row("m2", D1, 100m, 20m)], ct);

        var plan = await _repository.PlanReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 100m, 11m), Row("m3", D1, 100m, 1m)], ct);

        Assert.Equal(new SpendLimitSnapshotResult(1, 1, 0, 1), plan);
        var snapshot = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        Assert.Equal(["m1", "m2"], snapshot!.Limits.Select(l => l.MemberId));
        Assert.Equal(10m, snapshot.Limits.Single(l => l.MemberId == "m1").PeriodToDateSpend);
    }

    [Fact]
    public async Task Replace_TenantsAndVendorsAreSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 100m, 10m)], ct);
        await _repository.ReplaceSnapshotAsync("t2", Vendor, D1,
            [new SpendLimitRecord { TenantId = "t2", VendorId = Vendor, SnapshotDate = D1, MemberId = "m1", PeriodToDateSpend = 99m }], ct);

        var t1 = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        var other = await _repository.GetLatestSnapshotAsync("t1", VendorCatalog.ChatGptEnterprise.Id, null, ct);

        Assert.Equal(10m, Assert.Single(t1!.Limits).PeriodToDateSpend);
        Assert.Null(other);
    }

    [Fact]
    public async Task Replace_AMalformedBatch_IsRejectedBeforeAnythingIsStaged()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m1", D1, 1m, 1m), Row("m1", D1, 2m, 2m)], ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("m2", D2, 1m, 1m)], ct));   // wrong date
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row(" ", D1, 1m, 1m)], ct));

        // A later save on the same context must not persist any rejected batch's rows.
        await _context.SaveChangesAsync(ct);
        Assert.Null(await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct));
    }

    [Fact]
    public async Task Replace_RejectionMessages_NameNoMember()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Row("secret-member-id", D1, 1m, 1m), Row("secret-member-id", D1, 2m, 2m)], ct));

        Assert.DoesNotContain("secret-member-id", ex.Message);
    }

    private static SpendLimitRecord Row(string memberId, DateOnly date, decimal? limit, decimal? spent) => new()
    {
        TenantId = "t1",
        VendorId = Vendor,
        SnapshotDate = date,
        MemberId = memberId,
        LimitAmount = limit,
        PeriodToDateSpend = spent,
    };
}
