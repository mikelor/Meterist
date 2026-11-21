using Meterist.Core.Auditing;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Auditing;

public class SeatAuditorTests
{
    private static readonly Guid Claude = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly Guid ChatGpt = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly DateOnly AsOf = new(2026, 11, 21);

    private sealed class World
    {
        public List<VendorRateConfig> Rates { get; } = [];

        public Dictionary<Guid, RosterSnapshot> Rosters { get; } = [];

        public List<ActivitySummaryRecord> Activity { get; } = [];

        public List<UserDailyUsageRecord> Usage { get; } = [];

        public SeatAuditor Auditor(params Guid[] withRosterSource) => new(
            withRosterSource.Select(id => (IVendorRosterSource)new Source(id)).ToList(),
            new Rates(Rates), new Rosters_(Rosters), new Activity_(Activity), new Usage_(Usage));
    }

    private static MemberRosterRecord Member(
        Guid vendor, string id, string? email = null, string? name = null, string? seat = null, DateTimeOffset? added = null,
        string? role = "standard-user") => new()
    {
        TenantId = "t1", VendorId = vendor, SnapshotDate = AsOf, MemberId = id, Email = email, Name = name, SeatType = seat,
        AddedAt = added, Role = role,
    };

    private static RosterSnapshot Roster(params MemberRosterRecord[] members) => new(AsOf, members);

    private static UserDailyUsageRecord Use(
        Guid vendor, string userId, int daysAgo, decimal amount = 0m, long? requests = null, decimal? credits = null,
        string? email = null, UsageAttribution attribution = UsageAttribution.Person) => new()
    {
        TenantId = "t1", VendorId = vendor, Date = AsOf.AddDays(-daysAgo), UserId = userId, UserEmail = email,
        Amount = amount, Requests = requests, Credits = credits, Attribution = attribution,
    };

    private static VendorRateConfig SeatRate(Guid vendor, int? seats) => new()
    {
        TenantId = "t1", VendorId = vendor, RateType = "per-seat", ModelOrSku = "seat", Rate = 50m, SeatCount = seats,
        BillingCadence = BillingCadence.Monthly, EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    [Fact]
    public async Task BilledSeatsDifferingFromTheVendorsAssignedSeats_IsDrift()
    {
        var w = new World();
        w.Rates.Add(SeatRate(Claude, 29));
        w.Rosters[Claude] = Roster(Member(Claude, "m1"));
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-1), AssignedSeatCount = 42 });

        var audit = await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken);

        var v = Assert.Single(audit.Vendors);
        Assert.Equal((29, 42), (v.BilledSeats, v.AssignedSeats));
        Assert.True(v.HasDrift);
        Assert.True(audit.HasDrift);
        Assert.Equal("assigned seats", v.VendorReportedSource);   // assigned is preferred over the roster size
    }

    [Fact]
    public async Task BilledSeatsEqualToTheVendorsFigure_IsNotDrift()
    {
        var w = new World();
        w.Rates.Add(SeatRate(Claude, 42));
        w.Rosters[Claude] = Roster(Member(Claude, "m1"));
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-1), AssignedSeatCount = 42 });

        var audit = await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(audit.HasDrift);
    }

    [Fact]
    public async Task WithNoAssignedFigure_TheBilledCountIsComparedWithTheRosterSize()
    {
        var w = new World();
        w.Rates.Add(SeatRate(ChatGpt, 3));
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a", seat: "default"), Member(ChatGpt, "b", seat: "default"), Member(ChatGpt, "c", seat: "automation"), Member(ChatGpt, "d", seat: "default"));

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.Equal((3, 4, "roster"), (v.BilledSeats, v.VendorReportedSeats, v.VendorReportedSource));
        Assert.True(v.HasDrift);
        Assert.Equal(new Dictionary<string, int> { ["automation"] = 1, ["default"] = 3 }, v.RosterBySeatType);
    }

    [Fact]
    public async Task ABilledCountAboveTheRoster_IsUnusedCommittedSeats_NotDrift()
    {
        // The real zelleri case: 50 seats billed, 29 members. A commitment larger than the roster is not a stale rate card.
        var w = new World();
        w.Rates.Add(SeatRate(ChatGpt, 50));
        w.Rosters[ChatGpt] = Roster(Enumerable.Range(0, 29).Select(i => Member(ChatGpt, $"m{i}")).ToArray());

        var audit = await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken);

        var v = Assert.Single(audit.Vendors);
        Assert.False(v.HasDrift);
        Assert.False(audit.HasDrift);   // so the exit code stays clean
        Assert.Equal(21, v.UnusedBilledSeats);
    }

    [Fact]
    public async Task MoreRosterMembersThanBilledSeats_IsDrift_AndHasNoUnusedSeats()
    {
        var w = new World();
        w.Rates.Add(SeatRate(ChatGpt, 2));
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"), Member(ChatGpt, "b"), Member(ChatGpt, "c"));

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.True(v.HasDrift);
        Assert.Null(v.UnusedBilledSeats);
    }

    [Fact]
    public async Task AssignedSeatsBelowTheBilledCount_IsStillDrift_BecauseBillingFollowsAssignedSeats()
    {
        var w = new World();
        w.Rates.Add(SeatRate(Claude, 12));
        w.Rosters[Claude] = Roster(Member(Claude, "m1"));
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-1), AssignedSeatCount = 10 });

        var v = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.True(v.HasDrift);
        Assert.Null(v.UnusedBilledSeats);   // the "unused" reading applies only to a roster comparison
    }

    [Fact]
    public async Task NoBilledCount_IsAnExplainedNote_NotDrift()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.Null(v.BilledSeats);
        Assert.False(v.HasDrift);
        Assert.Contains(v.Notes, n => n.Contains("No seat rate"));
    }

    [Fact]
    public async Task ASeatRateWithNoSeatCount_IsAnExplainedNote()
    {
        var w = new World();
        w.Rates.Add(SeatRate(ChatGpt, null));
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.False(v.HasDrift);
        Assert.Contains(v.Notes, n => n.Contains("no seat count"));
    }

    [Fact]
    public async Task NoRosterSnapshot_SaysSo_AndDoesNotAssessIdleSeats()
    {
        var w = new World();
        w.Rates.Add(SeatRate(ChatGpt, 3));

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.Null(v.RosterCount);
        Assert.False(v.HasDrift);   // nothing to compare with
        Assert.False(v.Idle.Assessable);
        Assert.Contains(v.Notes, n => n.Contains("No roster snapshot"));
    }

    [Fact]
    public async Task AStaleRosterSnapshot_IsNoted()
    {
        var w = new World();
        w.Rosters[ChatGpt] = new RosterSnapshot(AsOf.AddDays(-20), [Member(ChatGpt, "a")]);

        var v = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.Contains(v.Notes, n => n.Contains("20 days old"));
    }

    [Fact]
    public async Task RosterAndAssignedSeatsThatDisagree_AreNotedAsDifferentMeasures_NotCalledDrift()
    {
        var w = new World();
        w.Rates.Add(SeatRate(Claude, 10));
        w.Rosters[Claude] = Roster(Member(Claude, "m1"), Member(Claude, "m2"));
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-1), AssignedSeatCount = 10 });

        var v = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.False(v.HasDrift);
        Assert.Contains(v.Notes, n => n.Contains("not the same measure"));
    }

    [Fact]
    public async Task AProductRowOrAnOldAssignedFigure_IsNotUsedAsTheAssignedSeatCount()
    {
        var w = new World();
        w.Rosters[Claude] = Roster(Member(Claude, "m1"));
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-1), Product = "chat", AssignedSeatCount = 99 });
        w.Activity.Add(new ActivitySummaryRecord { TenantId = "t1", VendorId = Claude, Date = AsOf.AddDays(-40), AssignedSeatCount = 77 });

        var v = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors);

        Assert.Null(v.AssignedSeats);
    }

    // ---- idle seats

    [Fact]
    public async Task AMemberWithNoActivityInTheWindow_IsIdle_AndAnActiveOneIsNot()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "busy", name: "Busy"), Member(ChatGpt, "quiet", name: "Quiet"));
        w.Usage.Add(Use(ChatGpt, "busy", daysAgo: 3, credits: 5m));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.True(idle.Assessable);
        Assert.Equal("quiet", Assert.Single(idle.IdleSeats).MemberId);
        Assert.Equal(1, idle.ActiveCount);
        Assert.Equal(new DateRange(AsOf.AddDays(-29), AsOf), idle.Window);
    }

    [Theory]
    [InlineData(0, 0, 3, true)]    // requests only: a seat plan's cost is zero by design
    [InlineData(2.5, 0, 0, true)]  // spend
    [InlineData(0, 4, 0, true)]    // credits
    [InlineData(0, 0, 0, false)]   // a row with nothing in it is not activity
    public async Task ActivityIsSpendCreditsOrRequestsAboveZero(double amount, double credits, long requests, bool active)
    {
        var w = new World();
        w.Rosters[Claude] = Roster(Member(Claude, "m1"), Member(Claude, "other"));
        w.Usage.Add(Use(Claude, "other", 1, amount: 1m));   // someone else is active, so the window is assessable
        w.Usage.Add(Use(Claude, "m1", 2, amount: (decimal)amount, credits: (decimal)credits, requests: requests));

        var idle = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal(active, !idle.IdleSeats.Any(s => s.MemberId == "m1"));
    }

    [Fact]
    public async Task OnlyPersonAttributedUsageCountsAsAMemberBeingActive()
    {
        var w = new World();
        w.Rosters[Claude] = Roster(Member(Claude, "m1"), Member(Claude, "m2"));
        w.Usage.Add(Use(Claude, "m2", 1, amount: 1m));
        w.Usage.Add(Use(Claude, "m1", 1, amount: 9m, attribution: UsageAttribution.ApiKey));

        var idle = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal("m1", Assert.Single(idle.IdleSeats).MemberId);
    }

    [Fact]
    public async Task UsageIsMatchedToTheRosterByEmailWhenTheIdsDiffer_CaseInsensitively()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "roster-id", email: "Pat@Example.test"), Member(ChatGpt, "other"));
        w.Usage.Add(Use(ChatGpt, "different-id", 2, credits: 3m, email: "pat@example.TEST"));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.DoesNotContain(idle.IdleSeats, s => s.MemberId == "roster-id");
        Assert.Equal(0, idle.ActiveNotOnRoster);
    }

    [Fact]
    public async Task ActiveUsersWhoAreNotOnTheRoster_AreCounted_NotCalledIdle()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));
        w.Usage.Add(Use(ChatGpt, "a", 1, credits: 1m));
        w.Usage.Add(Use(ChatGpt, "gone", 1, credits: 1m));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal(1, idle.ActiveNotOnRoster);
        Assert.Empty(idle.IdleSeats);
    }

    [Fact]
    public async Task AMemberAddedInsideTheWindow_IsNewNotIdle()
    {
        var w = new World();
        var added = new DateTimeOffset(AsOf.AddDays(-5).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        w.Rosters[Claude] = Roster(Member(Claude, "old"), Member(Claude, "fresh", added: added), Member(Claude, "active"));
        w.Usage.Add(Use(Claude, "active", 1, amount: 1m));

        var idle = Assert.Single((await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal("old", Assert.Single(idle.IdleSeats).MemberId);
        Assert.Equal(1, idle.NewMemberCount);
    }

    [Fact]
    public async Task NoPerUserUsageAtAll_IsNotAssessable_RatherThanEverySeatIdle()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"), Member(ChatGpt, "b"));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.False(idle.Assessable);
        Assert.Contains("No per-user usage is stored", idle.Reason);
        Assert.Empty(idle.IdleSeats);
    }

    [Fact]
    public async Task UsageThatStopsBeforeTheWindow_IsNotAssessable_AndSaysWhereItEnds()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));
        w.Usage.Add(Use(ChatGpt, "a", 50, credits: 1m));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.False(idle.Assessable);
        Assert.Contains(AsOf.AddDays(-50).ToString("yyyy-MM-dd"), idle.Reason);
    }

    [Fact]
    public async Task StoredUsageThatStartsPartwayThroughTheWindow_IsAssessableWithACaveat()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"), Member(ChatGpt, "b"));
        w.Usage.Add(Use(ChatGpt, "a", 5, credits: 1m));   // the earliest stored row is 5 days ago, in a 30-day window

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.True(idle.Assessable);
        Assert.Contains("starts on", idle.Reason);
        Assert.Equal("b", Assert.Single(idle.IdleSeats).MemberId);
    }

    [Fact]
    public async Task AnIdleMemberWhoWasActiveEarlier_ShowsTheirLastActiveDay_AndNeverSeenSortsFirst()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(
            Member(ChatGpt, "was", name: "Was Active"), Member(ChatGpt, "never", name: "Never Active"), Member(ChatGpt, "now", name: "Now"));
        w.Usage.Add(Use(ChatGpt, "now", 1, credits: 1m));
        w.Usage.Add(Use(ChatGpt, "was", 45, credits: 1m));
        w.Usage.Add(Use(ChatGpt, "was", 60, credits: 1m));

        var idle = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal(["never", "was"], idle.IdleSeats.Select(s => s.MemberId));
        Assert.Null(idle.IdleSeats[0].LastActiveBeforeWindow);
        Assert.Equal(AsOf.AddDays(-45), idle.IdleSeats[1].LastActiveBeforeWindow);   // the most recent, not the oldest
    }

    [Fact]
    public async Task ADifferentIdleWindow_ChangesWhoIsIdle()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"), Member(ChatGpt, "b"));
        w.Usage.Add(Use(ChatGpt, "a", 1, credits: 1m));
        w.Usage.Add(Use(ChatGpt, "b", 10, credits: 1m));

        var narrow = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 7, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;
        var wide = Assert.Single((await w.Auditor(ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken)).Vendors).Idle;

        Assert.Equal("b", Assert.Single(narrow.IdleSeats).MemberId);
        Assert.Empty(wide.IdleSeats);
    }

    [Fact]
    public async Task OnlyVendorsWithARosterSourceAreAudited_AndTheFilterNarrowsThem()
    {
        var w = new World();
        w.Rosters[Claude] = Roster(Member(Claude, "m"));
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));

        var both = await w.Auditor(Claude, ChatGpt).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken);
        var one = await w.Auditor(Claude, ChatGpt).AuditAsync("t1", AsOf, 30, ChatGpt, TestContext.Current.CancellationToken);
        var onlyClaude = await w.Auditor(Claude).AuditAsync("t1", AsOf, 30, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, both.Vendors.Count);
        Assert.Equal(ChatGpt, Assert.Single(one.Vendors).Vendor.Id);
        Assert.Equal(Claude, Assert.Single(onlyClaude.Vendors).Vendor.Id);
    }

    [Fact]
    public async Task AnIdleWindowBelowOne_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new World().Auditor(Claude).AuditAsync("t1", AsOf, 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheRosterUsedIsTheLatestOnOrBeforeTheAuditDay()
    {
        var w = new World();
        w.Rosters[ChatGpt] = Roster(Member(ChatGpt, "a"));
        var future = await w.Auditor(ChatGpt).AuditAsync("t1", AsOf.AddDays(-30), 30, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(future.Vendors).RosterCount);   // the only snapshot is after the audit day
    }

    // ---- fakes

    private sealed class Source(Guid id) : IVendorRosterSource
    {
        public Guid VendorId => id;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorMember>>([]);
    }

    private sealed class Rates(List<VendorRateConfig> rates) : IVendorRateConfigRepository
    {
        public Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> CloseOpenEndedRateAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>(rates.Where(r => r.VendorId == vendorId).ToList());

        public Task<DateOnly?> FindNextEffectiveFromAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly afterEffectiveFrom, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly effectiveFrom, DateOnly? effectiveTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);
    }

    private sealed class Rosters_(Dictionary<Guid, RosterSnapshot> rosters) : IMemberRosterRepository
    {
        public Task<RosterSnapshotResult> ReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RosterSnapshotResult> PlanReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RosterSnapshot?> GetLatestSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(rosters.TryGetValue(vendorId, out var s) && (onOrBefore is null || s.Date <= onOrBefore) ? s : null);

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Activity_(List<ActivitySummaryRecord> rows) : IActivitySummaryRepository
    {
        public Task<ActivitySummaryUpsertResult> UpsertAsync(IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ActivitySummaryUpsertResult> PlanUpsertAsync(IEnumerable<ActivitySummaryRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ActivitySummaryRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivitySummaryRecord>>(
                rows.Where(r => r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End).ToList());
    }

    private sealed class Usage_(List<UserDailyUsageRecord> rows) : IUserDailyUsageRepository
    {
        public Task<UserUsageUpsertResult> UpsertAsync(IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UserUsageUpsertResult> PlanUpsertAsync(IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserDailyUsageRecord>>(
                rows.Where(r => r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End).ToList());
    }
}
