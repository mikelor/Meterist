using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class UserUsageReporterTests
{
    private static readonly Guid Vendor = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 2);
    private static readonly DateOnly D2 = new(2026, 11, 3);
    private static readonly DateRange Period = new(D1, new DateOnly(2026, 11, 30));

    [Fact]
    public async Task Build_SumsEachUsersDays_AndOrdersBySpendThenRequests()
    {
        var reporter = Reporter(
            users:
            [
                User("small", D1, 1m, requests: 5),
                User("big", D1, 10m, credits: 250m, requests: 40),
                User("big", D2, 5m, credits: 125m, requests: 20),
                User("tie-fewer", D1, 3m, requests: 1),
                User("tie-more", D1, 3m, requests: 9),
            ],
            daily: [Day(D1, usage: 17m), Day(D2, usage: 5m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.Equal(["big", "tie-more", "tie-fewer", "small"], vendor.Users.Select(u => u.UserId));

        var big = vendor.Users[0];
        Assert.Equal(15m, big.Amount);
        Assert.Equal(375m, big.Credits);
        Assert.Equal(60L, big.Requests);
        Assert.Equal(2, big.DaysActive);
    }

    [Fact]
    public async Task Build_CreditsAndRequestsStayNullWhenTheVendorReportsNone()
    {
        var reporter = Reporter([User("u1", D1, 2m)], [Day(D1, usage: 2m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.Null(vendor.Users[0].Credits);
        Assert.Null(vendor.Users[0].Requests);
        Assert.Null(vendor.TotalCredits);
        Assert.Null(vendor.TotalRequests);
    }

    [Fact]
    public async Task Build_ShowsTheEstimatedFlagAndAttribution_AndNamesWhoeverIsMostRecent()
    {
        var reporter = Reporter(
            [
                User("u1", D1, 1m, name: null, email: "old@example.test", estimated: false),
                User("u1", D2, 1m, name: "New Name", email: "new@example.test", estimated: true),
                User("k1", D1, 1m, attribution: UsageAttribution.ApiKey),
            ],
            [Day(D1, usage: 2m), Day(D2, usage: 1m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        var u1 = vendor.Users.Single(u => u.UserId == "u1");
        Assert.True(u1.IsEstimated);   // any estimated day makes the total an estimate
        Assert.Equal("New Name", u1.DisplayName);
        Assert.Equal(UsageAttribution.Person, u1.Attribution);
        Assert.Equal(UsageAttribution.ApiKey, vendor.Users.Single(u => u.UserId == "k1").Attribution);
        Assert.False(vendor.Users.Single(u => u.UserId == "k1").IsEstimated);
    }

    [Theory]
    [InlineData("Alex Example", "a@example.test", "Alex Example")]
    [InlineData(null, "a@example.test", "a@example.test")]
    [InlineData("  ", "a@example.test", "a@example.test")]
    [InlineData(null, null, "user-id")]
    public void DisplayName_PrefersNameThenEmailThenId(string? name, string? email, string expected)
    {
        var summary = new UserUsageSummary("user-id", email, name, UsageAttribution.Person, 0m, null, null, 1, false);

        Assert.Equal(expected, summary.DisplayName);
    }

    [Fact]
    public async Task Build_WhenPerUserDollarsMatchTheAggregate_Reconciles()
    {
        var reporter = Reporter(
            [User("a", D1, 6m), User("b", D1, 4m)],
            [Day(D1, usage: 10m, seat: 50m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.True(vendor.Reconciliation.Reconciles);
        Assert.Equal(0m, vendor.Reconciliation.Difference);
        Assert.Equal(10m, vendor.AggregateUsage);
        Assert.Equal(50m, vendor.AggregateSeatFee);   // shown beside, never mixed into per-user usage
        Assert.Equal(0, vendor.Reconciliation.UsageDaysWithoutUserRows);
    }

    [Fact]
    public async Task Build_AllowsRoundingAndASmallShortfall_ButFlagsARealOne()
    {
        var close = Reporter([User("a", D1, 98.5m)], [Day(D1, usage: 100m)]);        // 1.5%
        var far = Reporter([User("a", D1, 80m)], [Day(D1, usage: 100m)]);            // 20%
        var rounding = Reporter([User("a", D1, 0.01m)], [Day(D1, usage: 0.03m)]);    // cents

        Assert.True(Assert.Single((await close.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors).Reconciliation.Reconciles);
        Assert.True(Assert.Single((await rounding.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors).Reconciliation.Reconciles);

        var flagged = Assert.Single((await far.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors).Reconciliation;
        Assert.False(flagged.Reconciles);
        Assert.Equal(-20m, flagged.Difference);
    }

    [Fact]
    public async Task Build_ComparesOnlyDaysThatHavePerUserRows_AndCountsTheOnesThatDoNot()
    {
        // Per-user history starts on D2; D1 has aggregate usage but no per-user rows.
        var reporter = Reporter(
            [User("a", D2, 5m)],
            [Day(D1, usage: 100m), Day(D2, usage: 5m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.True(vendor.Reconciliation.Reconciles);   // like for like on D2
        Assert.Equal(5m, vendor.Reconciliation.AggregateUsage);
        Assert.Equal(105m, vendor.AggregateUsage);       // whole period, for context
        Assert.Equal(1, vendor.Reconciliation.UsageDaysWithoutUserRows);
        Assert.Equal(1, vendor.Reconciliation.DaysCompared);
    }

    [Fact]
    public async Task Build_ADayWithNoUsageAndNoUserRows_IsNotCountedAsMissing()
    {
        var reporter = Reporter([User("a", D2, 5m)], [Day(D1, usage: 0m, seat: 40m), Day(D2, usage: 5m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.Equal(0, vendor.Reconciliation.UsageDaysWithoutUserRows);
    }

    [Fact]
    public async Task Build_LeavesOutVendorsWithNoPerUserRows_AndHonoursTheVendorFilter()
    {
        var other = VendorCatalog.ClaudeEnterprise.Id;
        var reporter = new UserUsageReporter(
            new FakeUsers([User("a", D1, 1m), User("c", D1, 2m, vendor: other)]),
            new FakeDaily([Day(D1, usage: 1m), Day(D1, usage: 2m, vendor: other)]));

        var all = await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken);
        var onlyClaude = await reporter.BuildAsync("t1", Period, other, TestContext.Current.CancellationToken);
        var gemini = await reporter.BuildAsync("t1", Period, VendorCatalog.GeminiEnterprise.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, all.Vendors.Count);
        Assert.Equal(VendorCatalog.ClaudeEnterprise.Id, Assert.Single(onlyClaude.Vendors).Vendor.Id);
        Assert.Empty(gemini.Vendors);
    }

    [Fact]
    public async Task Build_UsesOnlyTheRequestedTenantAndPeriod()
    {
        var reporter = Reporter(
            [User("in", D1, 1m), User("other-tenant", D1, 9m, tenant: "t2"), User("out", new DateOnly(2026, 12, 15), 9m)],
            [Day(D1, usage: 1m)]);

        var vendor = Assert.Single((await reporter.BuildAsync("t1", Period, null, TestContext.Current.CancellationToken)).Vendors);

        Assert.Equal(["in"], vendor.Users.Select(u => u.UserId));
    }

    private static UserUsageReporter Reporter(
        IReadOnlyList<UserDailyUsageRecord> users, IReadOnlyList<DailySpendRecord> daily) =>
        new(new FakeUsers(users), new FakeDaily(daily));

    private static UserDailyUsageRecord User(
        string id, DateOnly date, decimal amount, decimal? credits = null, long? requests = null,
        string? name = null, string? email = null, bool estimated = false,
        UsageAttribution attribution = UsageAttribution.Person, string tenant = "t1", Guid? vendor = null) => new()
    {
        TenantId = tenant,
        VendorId = vendor ?? Vendor,
        Date = date,
        UserId = id,
        UserName = name,
        UserEmail = email,
        Amount = amount,
        Credits = credits,
        Requests = requests,
        IsEstimated = estimated,
        Attribution = attribution,
    };

    private static DailySpendRecord Day(DateOnly date, decimal usage, decimal seat = 0m, Guid? vendor = null) => new()
    {
        TenantId = "t1",
        VendorId = vendor ?? Vendor,
        Date = date,
        SeatFee = seat,
        UsageOrOverage = usage,
        GrossSpend = seat + usage,
        NetSpend = seat + usage,
    };

    private sealed class FakeUsers(IReadOnlyList<UserDailyUsageRecord> rows) : IUserDailyUsageRepository
    {
        public Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserDailyUsageRecord>>(rows
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
                .ToList());

        public Task<UserUsageUpsertResult> UpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UserUsageUpsertResult> PlanUpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDaily(IReadOnlyList<DailySpendRecord> rows) : IDailySpendRepository
    {
        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendRecord>>(rows
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
                .ToList());

        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
