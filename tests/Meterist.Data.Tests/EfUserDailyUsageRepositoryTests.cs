using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfUserDailyUsageRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly DateOnly Day = new(2026, 11, 3);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfUserDailyUsageRepository _repository;

    public EfUserDailyUsageRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.Migrate();
        _repository = new EfUserDailyUsageRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_StoresEveryFieldAndReadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = Record("u1", amount: 12.3456m, credits: 308.64m, requests: 42, isEstimated: true,
            attribution: UsageAttribution.Allocated, email: "a@example.test", name: "Alex Example");

        var result = await _repository.UpsertAsync([record], ct);

        Assert.Equal(new UserUsageUpsertResult(1, 0, 0), result);

        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct));
        Assert.Equal("u1", stored.UserId);
        Assert.Equal("a@example.test", stored.UserEmail);
        Assert.Equal("Alex Example", stored.UserName);
        Assert.Equal(12.3456m, stored.Amount);
        Assert.Equal(308.64m, stored.Credits);
        Assert.Equal(42, stored.Requests);
        Assert.True(stored.IsEstimated);
        Assert.Equal(UsageAttribution.Allocated, stored.Attribution);
    }

    [Fact]
    public async Task UpsertAsync_NullableFieldsStayNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Record("u1")], ct);

        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct));
        Assert.Null(stored.Credits);
        Assert.Null(stored.Requests);
        Assert.Null(stored.UserEmail);
        Assert.Null(stored.UserName);
    }

    [Fact]
    public async Task UpsertAsync_SameKeyTwice_ReplacesRatherThanDuplicating_AndCountsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Record("u1", amount: 1m, name: "Old Name")], ct);

        var result = await _repository.UpsertAsync([Record("u1", amount: 2m, name: "New Name")], ct);

        Assert.Equal(new UserUsageUpsertResult(0, 1, 0), result);
        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct));
        Assert.Equal(2m, stored.Amount);
        Assert.Equal("New Name", stored.UserName);
    }

    [Fact]
    public async Task UpsertAsync_IdenticalRow_IsReportedUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Record("u1", amount: 5m, credits: 125m, requests: 3)], ct);

        var result = await _repository.UpsertAsync([Record("u1", amount: 5m, credits: 125m, requests: 3)], ct);

        Assert.Equal(new UserUsageUpsertResult(0, 0, 1), result);
    }

    [Fact]
    public async Task UpsertAsync_LeavesRowsAbsentFromTheInputAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Record("u1"), Record("u2")], ct);

        await _repository.UpsertAsync([Record("u1", amount: 9m)], ct);

        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct);
        Assert.Equal(["u1", "u2"], stored.Select(r => r.UserId));
    }

    [Fact]
    public async Task PlanUpsertAsync_PredictsTheSameCounts_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync([Record("u1", amount: 1m), Record("u2", amount: 2m)], ct);
        var batch = new[] { Record("u1", amount: 1m), Record("u2", amount: 3m), Record("u3") };

        var plan = await _repository.PlanUpsertAsync(batch, ct);

        Assert.Equal(new UserUsageUpsertResult(1, 1, 1), plan);
        Assert.Equal(2, (await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct)).Count);
        Assert.Equal(2m, (await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct))[1].Amount);

        var real = await _repository.UpsertAsync(batch, ct);
        Assert.Equal(plan, real);
    }

    [Fact]
    public async Task UpsertAsync_DuplicateKeyInOneBatch_IsRefused_WithoutNamingTheUser()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpsertAsync([Record("secret-user"), Record("secret-user")], ct));

        Assert.DoesNotContain("secret-user", ex.Message);
        Assert.Contains("2026-11-03", ex.Message);
    }

    [Fact]
    public async Task SameUserOnDifferentTenantsVendorsAndDays_AreSeparateRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync(
        [
            Record("u1"),
            Record("u1", tenant: "t2"),
            Record("u1", vendor: VendorCatalog.ClaudeEnterprise.Id),
            Record("u1", date: Day.AddDays(1)),
        ], ct);

        Assert.Equal(4, await _context.UserDailyUsageRecords.CountAsync(ct));
        Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day), ct));
        Assert.Equal(2, (await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day.AddDays(1)), ct)).Count);
    }

    [Fact]
    public async Task GetAsync_FiltersByPeriod_AndOrdersByDateThenUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.UpsertAsync(
        [
            Record("b", date: Day.AddDays(1)),
            Record("a", date: Day.AddDays(1)),
            Record("z", date: Day),
            Record("m", date: Day.AddDays(10)),
        ], ct);

        var result = await _repository.GetAsync("t1", Vendor, new DateRange(Day, Day.AddDays(1)), ct);

        Assert.Equal(["z", "a", "b"], result.Select(r => r.UserId));
    }

    [Fact]
    public void ToString_DoesNotRevealThePerson()
    {
        var text = Record("u1", email: "a@example.test", name: "Alex Example").ToString();

        Assert.DoesNotContain("a@example.test", text);
        Assert.DoesNotContain("Alex", text);
    }

    private static UserDailyUsageRecord Record(
        string userId,
        string tenant = "t1",
        Guid? vendor = null,
        DateOnly? date = null,
        decimal amount = 0m,
        decimal? credits = null,
        long? requests = null,
        bool isEstimated = false,
        UsageAttribution attribution = UsageAttribution.Person,
        string? email = null,
        string? name = null) => new()
    {
        TenantId = tenant,
        VendorId = vendor ?? Vendor,
        Date = date ?? Day,
        UserId = userId,
        UserEmail = email,
        UserName = name,
        Amount = amount,
        Credits = credits,
        Requests = requests,
        IsEstimated = isEstimated,
        Attribution = attribution,
    };
}
