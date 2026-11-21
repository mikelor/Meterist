using Meterist.Core.Auditing;
using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

/// <summary>
/// The report's people sections, built from rows stored through the real repositories in a throwaway database, through the
/// real aggregator and renderer. Proves the pieces fit together (the aggregator's optional services, the per-user
/// reconciliation against stored daily records, the roster and activity reads) and not just each part on its own.
/// </summary>
public sealed class ReportPeopleSectionsIntegrationTests : IDisposable
{
    private static readonly Guid Claude = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly Start = new(2026, 11, 4);
    private static readonly DateRange Period = new(Start, new DateOnly(2026, 11, 17));

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;

    public ReportPeopleSectionsIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _context = new MeteristDbContext(new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options);
        _context.Database.Migrate();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task StoredRows_FlowThroughTheAggregatorIntoEveryPeopleSection_AndReconcile()
    {
        var ct = TestContext.Current.CancellationToken;
        var daily = new EfDailySpendRepository(_context);
        var usage = new EfUserDailyUsageRepository(_context);
        var roster = new EfMemberRosterRepository(_context);
        var activity = new EfActivitySummaryRepository(_context);
        var rates = new EfVendorRateConfigRepository(_context);

        await rates.AddAsync(new VendorRateConfig
        {
            TenantId = "t1", VendorId = Claude, RateType = "per-seat", ModelOrSku = "seat", Rate = 200m, SeatCount = 2,
            BillingCadence = BillingCadence.Monthly, EffectiveFrom = new DateOnly(2026, 1, 1),
        }, ct);

        // Fourteen days: Alex spends 10 a day and Blake nothing but has requests (a seat plan); the aggregate usage is 140.
        var days = Enumerable.Range(0, 14).Select(i => Start.AddDays(i)).ToList();
        await daily.UpsertAsync(days.Select(d => new DailySpendRecord
        {
            TenantId = "t1", VendorId = Claude, Date = d, SeatFee = 13m, UsageOrOverage = 10m, GrossSpend = 23m, NetSpend = 23m,
        }), cancellationToken: ct);
        await usage.UpsertAsync(days.SelectMany(d => new[]
        {
            Row(d, "alex", "Alex Example", "alex@example.test", 10m, requests: 40),
            Row(d, "blake", "Blake Sample", "blake@example.test", 0m, requests: 7),
        }), ct);

        // A roster of three: Casey has no activity at all, so is idle.
        await roster.ReplaceSnapshotAsync("t1", Claude, Period.End,
        [
            Member("alex", "Alex Example", "alex@example.test"),
            Member("blake", "Blake Sample", "blake@example.test"),
            Member("casey", "Casey Quiet", "casey@example.test"),
        ], ct);

        await activity.UpsertAsync(
        [
            new ActivitySummaryRecord
            {
                TenantId = "t1", VendorId = Claude, Date = Period.End.AddDays(-1), DailyActiveUsers = 2, WeeklyActiveUsers = 2,
                MonthlyActiveUsers = 2, AssignedSeatCount = 3,
            },
        ], ct);

        var aggregator = new ReportDataAggregator(
            daily, rates, null,
            new UserUsageReporter(usage, daily),
            new SeatAuditor([new Source(Claude)], rates, roster, activity, usage),
            activity);

        var data = await aggregator.AggregateAsync(["t1"], Period, cancellationToken: ct);
        var vendor = data.Tenants[0].Vendors.Single(v => v.Vendor.Id == Claude);

        Assert.NotNull(vendor.UserUsage);
        Assert.True(vendor.UserUsage.Reconciliation.Reconciles);
        Assert.Equal(140m, vendor.UserUsage.TotalAmount);
        Assert.Equal(140m, vendor.UserUsage.AggregateUsage);
        Assert.Equal(2, vendor.Seats!.BilledSeats);
        Assert.Equal(3, vendor.Seats.AssignedSeats);
        Assert.True(vendor.Seats.HasDrift);   // billed 2 against 3 assigned
        Assert.Equal(["casey"], vendor.Seats.Idle.IdleSeats.Select(s => s.MemberId));
        Assert.Equal(2, vendor.Activity!.DailyActive);

        var html = ClientBenchmarkReportRenderer.Render(data);
        Assert.Contains("Usage by person", html);
        Assert.Contains("Alex Example", html);
        Assert.Contains("Reconciles", html);
        Assert.Contains("Casey Quiet", html);                 // the idle list
        Assert.Contains("Seat drift", html);
        Assert.Contains("2 daily · 2 weekly · 2 monthly", html);
        Assert.Contains("Top consumers by spend", html);

        // Blake has requests but no spend: in the per-user table, not among the top consumers.
        var top = html[html.IndexOf("<h2>Top consumers by spend</h2>", StringComparison.Ordinal)..];
        top = top[..top.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.DoesNotContain("Blake Sample", top);
        Assert.Contains("Alex Example", top);
    }

    [Fact]
    public async Task WithNoPeopleServices_TheAggregatorStillWorks_AndTheReportHasNoPeopleSections()
    {
        var ct = TestContext.Current.CancellationToken;
        var daily = new EfDailySpendRepository(_context);
        var aggregator = new ReportDataAggregator(daily, new EfVendorRateConfigRepository(_context));

        var data = await aggregator.AggregateAsync(["t1"], Period, cancellationToken: ct);
        var html = ClientBenchmarkReportRenderer.Render(data);

        Assert.All(data.Tenants[0].Vendors, v =>
        {
            Assert.Null(v.UserUsage);
            Assert.Null(v.Seats);
            Assert.Null(v.Activity);
        });
        Assert.DoesNotContain("Usage by person", html);
    }

    private static UserDailyUsageRecord Row(DateOnly date, string id, string name, string email, decimal amount, long requests) => new()
    {
        TenantId = "t1", VendorId = Claude, Date = date, UserId = id, UserName = name, UserEmail = email, Amount = amount,
        Requests = requests, Attribution = UsageAttribution.Person,
    };

    private static MemberRosterRecord Member(string id, string name, string email) => new()
    {
        TenantId = "t1", VendorId = Claude, SnapshotDate = Period.End, MemberId = id, Name = name, Email = email, Role = "user",
    };

    private sealed class Source(Guid id) : IVendorRosterSource
    {
        public Guid VendorId => id;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorMember>>([]);
    }
}
