using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfUserActivityRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 18);
    private static readonly DateOnly D2 = new(2026, 11, 19);
    private static readonly DateOnly D3 = new(2026, 11, 20);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfUserActivityRepository _repository;

    public EfUserActivityRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _context = new MeteristDbContext(new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options);
        _context.Database.Migrate();
        _repository = new EfUserActivityRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Replace_StoresEveryFieldAndTheDetail_AndReadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = new UserActivityRecord
        {
            TenantId = "t1", VendorId = Vendor, Date = D1, UserId = "user_1", UserEmail = "a@example.test",
            ChatMessages = 12, ChatConversations = 3, ClaudeCodeSessions = 2, ClaudeCodeCommits = 4, ClaudeCodePullRequests = 1,
            ClaudeCodeLinesAdded = 300, ClaudeCodeLinesRemoved = 40, CoworkSessions = 5, CoworkActions = 60, CoworkMessages = 9,
            OfficeSessions = 2, OfficeMessages = 7, DesignMessages = 1, ScienceMessages = 0, WebSearches = 6,
            DetailJson = """{"chat_metrics":{"message_count":12}}""",
        };

        var result = await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [record], ct);

        Assert.Equal(new UserActivityReplaceResult(1, 0, 0, 0), result);
        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
        Assert.Equal("user_1", stored.UserId);
        Assert.Equal("a@example.test", stored.UserEmail);
        Assert.Equal((12L, 3L, 2L, 4L, 1L), (stored.ChatMessages, stored.ChatConversations, stored.ClaudeCodeSessions, stored.ClaudeCodeCommits, stored.ClaudeCodePullRequests));
        Assert.Equal((300L, 40L), (stored.ClaudeCodeLinesAdded, stored.ClaudeCodeLinesRemoved));
        Assert.Equal((5L, 60L, 9L), (stored.CoworkSessions, stored.CoworkActions, stored.CoworkMessages));
        Assert.Equal((2L, 7L, 1L, 0L, 6L), (stored.OfficeSessions, stored.OfficeMessages, stored.DesignMessages, stored.ScienceMessages, stored.WebSearches));
        Assert.Equal("""{"chat_metrics":{"message_count":12}}""", stored.DetailJson);
    }

    [Fact]
    public async Task Replace_TheSameDayAgain_IsUnchanged_AndARevisedCountUpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "u1", messages: 5)], ct);

        var same = await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "u1", messages: 5)], ct);
        var revised = await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "u1", messages: 9)], ct);

        Assert.Equal(new UserActivityReplaceResult(0, 0, 1, 0), same);
        Assert.Equal(new UserActivityReplaceResult(0, 1, 0, 0), revised);
        Assert.Equal(9, Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct)).ChatMessages);
    }

    [Fact]
    public async Task Replace_APersonAbsentFromARefetchedDay_IsRemoved_ButOtherDaysAreNeverTouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1, D2], [Row(D1, "a", 1), Row(D1, "b", 1), Row(D2, "a", 1)], ct);

        // Day 1 is fetched again and b is gone; day 2 is not fetched at all.
        var result = await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1)], ct);

        Assert.Equal(new UserActivityReplaceResult(0, 0, 1, 1), result);
        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct);
        Assert.Equal([(D1, "a"), (D2, "a")], stored.Select(r => (r.Date, r.UserId)));
    }

    [Fact]
    public async Task Replace_AFetchedDayWithNobodyActive_ClearsWhatWasStoredForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1)], ct);

        var result = await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [], ct);

        Assert.Equal(new UserActivityReplaceResult(0, 0, 0, 1), result);
        Assert.Empty(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
    }

    [Fact]
    public async Task Replace_NoFetchedDays_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1)], ct);

        var result = await _repository.ReplaceDaysAsync("t1", Vendor, [], [], ct);

        Assert.Equal(new UserActivityReplaceResult(0, 0, 0, 0), result);
        Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));
    }

    [Fact]
    public async Task Plan_ReportsWhatARealRunWouldDo_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1), Row(D1, "b", 1)], ct);

        var plan = await _repository.PlanReplaceDaysAsync("t1", Vendor, [D1, D2], [Row(D1, "a", 5), Row(D2, "c", 1)], ct);

        Assert.Equal(new UserActivityReplaceResult(1, 1, 0, 1), plan);
        var stored = await _repository.GetAsync("t1", Vendor, new DateRange(D1, D2), ct);
        Assert.Equal(["a", "b"], stored.Select(r => r.UserId));
        Assert.Equal(1, stored[0].ChatMessages);   // unchanged
    }

    [Fact]
    public async Task Replace_TenantsAndVendorsAreSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1)], ct);
        await _repository.ReplaceDaysAsync("t2", Vendor, [D1], [Row(D1, "a", 99, tenant: "t2")], ct);

        Assert.Equal(1, Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct)).ChatMessages);
        Assert.Empty(await _repository.GetAsync("t1", VendorCatalog.ChatGptEnterprise.Id, new DateRange(D1, D1), ct));
    }

    [Fact]
    public async Task Replace_AMalformedBatch_IsRejectedBeforeAnythingIsStaged_AndNamesNoUser()
    {
        var ct = TestContext.Current.CancellationToken;

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "secret-user", 1), Row(D1, "secret-user", 2)], ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D2, "a", 1)], ct));   // a day it did not declare
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 1, tenant: "other")], ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, " ", 1)], ct));

        Assert.DoesNotContain("secret-user", duplicate.Message);
        // A later save on the same context must not persist any rejected batch's rows.
        await _context.SaveChangesAsync(ct);
        Assert.Empty(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D3), ct));
    }

    [Fact]
    public async Task HasHeadlineActivity_IsDerivedNotStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceDaysAsync("t1", Vendor, [D1], [Row(D1, "a", 3)], ct);

        var stored = Assert.Single(await _repository.GetAsync("t1", Vendor, new DateRange(D1, D1), ct));

        Assert.True(stored.HasHeadlineActivity);
        Assert.False(new UserActivityRecord { TenantId = "t1", VendorId = Vendor, Date = D1, UserId = "x" }.HasHeadlineActivity);
    }

    private static UserActivityRecord Row(DateOnly date, string userId, long messages, string tenant = "t1") => new()
    {
        TenantId = tenant, VendorId = Vendor, Date = date, UserId = userId, ChatMessages = messages,
    };
}
