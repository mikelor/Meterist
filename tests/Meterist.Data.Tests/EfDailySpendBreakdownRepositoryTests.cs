using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfDailySpendBreakdownRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.GeminiEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 2);
    private static readonly DateOnly D2 = new(2026, 11, 3);
    private static readonly DateOnly D3 = new(2026, 11, 4);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfDailySpendBreakdownRepository _repository;

    public EfDailySpendBreakdownRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.Migrate();
        _repository = new EfDailySpendBreakdownRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Replace_StoresEveryFieldAndReadsItBack_OrderedByDateCategoryDetail()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _repository.ReplaceAsync("t1", Vendor, [D1, D2],
        [
            Row(D2, "Seat", 11.5m, "Plus: Subscription"),
            Row(D1, "Tax", 38m, "Tax", isUsage: false),
            Row(D1, "AgentPlatform", 0.02m, "Compute"),
            Row(D1, "AgentPlatform", 0.01m, "Memory"),
        ], ct);

        Assert.Equal(new BreakdownReplaceResult(4, 0, 0, 0), result);

        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct);
        Assert.Equal(
            [(D1, "AgentPlatform", "Compute"), (D1, "AgentPlatform", "Memory"), (D1, "Tax", "Tax"), (D2, "Seat", "Plus: Subscription")],
            stored.Select(r => (r.Date, r.Category, r.Detail)));
        Assert.Equal(38m, stored.Single(r => r.Category == "Tax").Amount);
        Assert.False(stored.Single(r => r.Category == "Tax").IsUsage);
        Assert.True(stored.Single(r => r.Category == "Seat").IsUsage);
    }

    [Fact]
    public async Task Replace_ADetaillessRow_StoresAnEmptyDetail_AndIsStillUniqueByCategory()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 1m, detail: "")], ct);

        var again = await _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 2m, detail: "")], ct);

        Assert.Equal(new BreakdownReplaceResult(0, 1, 0, 0), again);
        Assert.Equal(1, await _context.DailySpendBreakdownRecords.CountAsync(ct));
    }

    [Fact]
    public async Task Replace_ReportsUnchangedUpdatedInsertedAndRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1],
            [Row(D1, "Seat", 10m, "s"), Row(D1, "ModelUsage", 1m, "m"), Row(D1, "Tax", 2m, "t", isUsage: false)], ct);

        var result = await _repository.ReplaceAsync("t1", Vendor, [D1],
            [Row(D1, "Seat", 10m, "s"), Row(D1, "ModelUsage", 3m, "m"), Row(D1, "Support", 4m, "x", isUsage: false)], ct);

        Assert.Equal(new BreakdownReplaceResult(Inserted: 1, Updated: 1, Unchanged: 1, Removed: 1), result);
        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct);
        Assert.Equal(["ModelUsage", "Seat", "Support"], stored.Select(r => r.Category));
        Assert.Equal(3m, stored.Single(r => r.Category == "ModelUsage").Amount);
    }

    [Fact]
    public async Task Replace_ACoveredDayWithNoRows_ClearsItsStoredRows()
    {
        // D2 is covered but the vendor now reports nothing for it: the old rows must go.
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1, D2], [Row(D1, "Seat", 1m), Row(D2, "Seat", 1m)], ct);

        var result = await _repository.ReplaceAsync("t1", Vendor, [D1, D2], [Row(D1, "Seat", 1m)], ct);

        Assert.Equal(new BreakdownReplaceResult(0, 0, 1, 1), result);
        Assert.Equal([D1], (await _repository.GetAsync("t1", Vendor, new DateRange(D1, D3), ct)).Select(r => r.Date));
    }

    [Fact]
    public async Task Replace_NeverTouchesDaysItDoesNotCover_OrOtherTenantsAndVendors()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1, D2, D3], [Row(D1, "Seat", 1m), Row(D2, "Seat", 2m), Row(D3, "Seat", 3m)], ct);
        await _repository.ReplaceAsync("t2", Vendor, [D2], [Row(D2, "Seat", 20m, tenant: "t2")], ct);
        await _repository.ReplaceAsync("t1", VendorCatalog.ChatGptEnterprise.Id, [D2],
            [Row(D2, "Seat", 30m, vendor: VendorCatalog.ChatGptEnterprise.Id)], ct);

        // Replace only D2 for t1/Gemini, with nothing: only that one row may disappear.
        var result = await _repository.ReplaceAsync("t1", Vendor, [D2], [], ct);

        Assert.Equal(new BreakdownReplaceResult(0, 0, 0, 1), result);
        Assert.Equal([D1, D3], (await _repository.GetAsync("t1", Vendor, new DateRange(D1, D3), ct)).Select(r => r.Date));
        Assert.Single(await _repository.GetAsync("t2", Vendor, new DateRange(D1, D3), ct));
        Assert.Single(await _repository.GetAsync("t1", VendorCatalog.ChatGptEnterprise.Id, new DateRange(D1, D3), ct));
    }

    [Fact]
    public async Task Plan_PredictsTheSameResult_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 10m), Row(D1, "Tax", 2m, isUsage: false)], ct);
        var batch = new[] { Row(D1, "Seat", 11m), Row(D1, "Support", 4m, isUsage: false) };

        var plan = await _repository.PlanReplaceAsync("t1", Vendor, [D1], batch, ct);

        Assert.Equal(new BreakdownReplaceResult(1, 1, 0, 1), plan);
        var untouched = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct);
        Assert.Equal(["Seat", "Tax"], untouched.Select(r => r.Category));
        Assert.Equal(10m, untouched.Single(r => r.Category == "Seat").Amount);

        Assert.Equal(plan, await _repository.ReplaceAsync("t1", Vendor, [D1], batch, ct));
    }

    [Fact]
    public async Task Replace_DuplicateKeyInOneBatch_IsRefused_AndStagesNothingForALaterSave()
    {
        var ct = TestContext.Current.CancellationToken;

        // The duplicate comes after a valid new row, so a repository that stages as it goes would already
        // hold "early" in the shared context when it throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.ReplaceAsync("t1", Vendor, [D1],
            [Row(D1, "Early", 1m), Row(D1, "Dup", 1m, "x"), Row(D1, "Dup", 2m, "x")], ct));

        await _repository.ReplaceAsync("t1", Vendor, [D2], [Row(D2, "Later", 1m)], ct);

        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D3), ct);
        Assert.Equal(["Later"], stored.Select(r => r.Category));
    }

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("outside-days")]
    [InlineData("blank-category")]
    public async Task Replace_RejectsAMalformedBatch_BeforeChangingAnything(string problem)
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 1m)], ct);

        var bad = problem switch
        {
            "other-tenant" => Row(D1, "X", 1m, tenant: "t2"),
            "outside-days" => Row(D3, "X", 1m),
            _ => Row(D1, " ", 1m),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 99m), bad], ct));

        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D3), ct));
        Assert.Equal(1m, stored.Amount);
    }

    [Fact]
    public async Task Replace_WithNoCoveredDays_IsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceAsync("t1", Vendor, [D1], [Row(D1, "Seat", 1m)], ct);

        var result = await _repository.ReplaceAsync("t1", Vendor, [], [], ct);

        Assert.Equal(new BreakdownReplaceResult(0, 0, 0, 0), result);
        Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
    }

    private static DailySpendBreakdownRecord Row(
        DateOnly date, string category, decimal amount, string detail = "", bool isUsage = true,
        string tenant = "t1", Guid? vendor = null) => new()
    {
        TenantId = tenant,
        VendorId = vendor ?? Vendor,
        Date = date,
        Category = category,
        Detail = detail,
        Amount = amount,
        IsUsage = isUsage,
    };
}
