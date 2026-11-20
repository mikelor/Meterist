using Meterist.Core.Auditing;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Auditing;

public class CoverageCheckerTests
{
    private static readonly VendorIdentity SeatVendor = VendorCatalog.ChatGptEnterprise;
    private static readonly VendorIdentity UsageVendor = VendorCatalog.ClaudeApiPlatform;

    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public async Task CheckAsync_FullCoverage_ReportsNoGapsAndNoProblem()
    {
        var repo = new InMemoryRepository().Add("t1", SeatVendor, Days(D(7, 1), D(7, 10)));

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        var seat = Vendor(report, "t1", SeatVendor);
        Assert.Equal(10, seat.DaysStored);
        Assert.Empty(seat.Gaps);
        Assert.Null(seat.NotYetBackfilled);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_MissingRunOfDaysInTheMiddle_IsOneGapRange()
    {
        // The real shape of the incident this exists for: a stretch with no extraction run.
        var stored = Days(D(9, 1), D(9, 24)).Concat(Days(D(10, 3), D(10, 31)));
        var repo = new InMemoryRepository().Add("t1", SeatVendor, stored);

        var report = await Check(repo, "t1", new DateRange(D(9, 1), D(10, 31)));

        var seat = Vendor(report, "t1", SeatVendor);
        var gap = Assert.Single(seat.Gaps);
        Assert.Equal(new DateRange(D(9, 25), D(10, 2)), gap);
        Assert.Equal(8, seat.GapDays);
        Assert.True(seat.HasProblem);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_SeparateMissingStretches_AreSeparateRanges()
    {
        var stored = Days(D(7, 1), D(7, 3)).Concat(Days(D(7, 6), D(7, 8))).Concat(Days(D(7, 10), D(7, 10)));
        var repo = new InMemoryRepository().Add("t1", SeatVendor, stored);

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        Assert.Equal(
            [new DateRange(D(7, 4), D(7, 5)), new DateRange(D(7, 9), D(7, 9))],
            Vendor(report, "t1", SeatVendor).Gaps);
    }

    [Fact]
    public async Task CheckAsync_DaysAfterTheLastRecord_AreAGap()
    {
        // Extraction simply stopped: nothing is stored after Nov 11, and the period runs to Nov 18.
        var repo = new InMemoryRepository().Add("t1", SeatVendor, Days(D(7, 1), D(11, 11)));

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(11, 18)));

        var gap = Assert.Single(Vendor(report, "t1", SeatVendor).Gaps);
        Assert.Equal(new DateRange(D(11, 12), D(11, 18)), gap);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_DaysBeforeTheVendorsFirstRecord_AreNotYetBackfilledNotGaps()
    {
        // A tenant onboarded mid-period: history before its first record was never extracted.
        var repo = new InMemoryRepository().Add("t1", SeatVendor, Days(D(7, 20), D(7, 31)));

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 31)));

        var seat = Vendor(report, "t1", SeatVendor);
        Assert.Empty(seat.Gaps);
        Assert.Equal(new DateRange(D(7, 1), D(7, 19)), seat.NotYetBackfilled);
        Assert.False(seat.HasProblem);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_FirstRecordIsBeforeThePeriod_SoAMissingDayInsideItIsAGap()
    {
        // Data begins in June, so a hole in July is not "unbackfilled history".
        var stored = Days(D(6, 1), D(7, 4)).Concat(Days(D(7, 8), D(7, 10)));
        var repo = new InMemoryRepository().Add("t1", SeatVendor, stored);

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        var seat = Vendor(report, "t1", SeatVendor);
        Assert.Equal([new DateRange(D(7, 5), D(7, 7))], seat.Gaps);
        Assert.Null(seat.NotYetBackfilled);
    }

    [Fact]
    public async Task CheckAsync_PeriodEntirelyBeforeTheFirstRecord_IsAllNotYetBackfilled()
    {
        var repo = new InMemoryRepository().Add("t1", SeatVendor, Days(D(8, 1), D(8, 10)));

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        var seat = Vendor(report, "t1", SeatVendor);
        Assert.Equal(0, seat.DaysStored);
        Assert.Empty(seat.Gaps);
        Assert.Equal(new DateRange(D(7, 1), D(7, 10)), seat.NotYetBackfilled);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_UsageOnlyVendorWithQuietDays_IsNotAProblem()
    {
        // Claude API Platform writes a row only for days with usage, so missing days are normal.
        var repo = new InMemoryRepository().Add("t1", UsageVendor, [D(7, 2), D(7, 7)]);

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        var usage = Vendor(report, "t1", UsageVendor);
        Assert.False(UsageVendor.ExpectsRecordEveryDay);
        Assert.Equal(2, usage.DaysStored);
        Assert.NotEmpty(usage.Gaps); // still reported, so the output can say "N days without usage"
        Assert.False(usage.HasProblem);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_UsageOnlyVendor_DaysBeforeItsFirstUsageAreQuietDaysNotUnbackfilledHistory()
    {
        // Real case: a usage-priced vendor whose first usage was Jul 11. Jul 1-10 were not
        // "missed extractions", there was simply nothing to record, so no backfill note.
        var repo = new InMemoryRepository().Add("t1", UsageVendor, [D(7, 11), D(7, 14)]);

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 20)));

        var usage = Vendor(report, "t1", UsageVendor);
        Assert.Null(usage.NotYetBackfilled);
        Assert.Equal(18, usage.GapDays); // 20 days in the period, 2 stored, all 18 others are quiet days
        Assert.False(usage.HasProblem);
    }

    [Fact]
    public async Task CheckAsync_VendorWithNoRecordsAtAll_IsNotAProblem()
    {
        // Probably unused by this tenant or never extracted: the data can't say which.
        var repo = new InMemoryRepository();

        var report = await Check(repo, "t1", new DateRange(D(7, 1), D(7, 10)));

        var seat = Vendor(report, "t1", SeatVendor);
        Assert.False(seat.HasAnyRecord);
        Assert.Empty(seat.Gaps);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_OneTenantsGapIsNotMaskedByAnothersFullCoverage_AndTenantsAreIsolated()
    {
        var repo = new InMemoryRepository()
            .Add("good", SeatVendor, Days(D(7, 1), D(7, 10)))
            .Add("bad", SeatVendor, Days(D(7, 1), D(7, 4)).Concat(Days(D(7, 8), D(7, 10))));

        var report = await Check(repo, ["good", "bad"], new DateRange(D(7, 1), D(7, 10)));

        Assert.False(report.Tenants.Single(t => t.TenantId == "good").HasProblems);
        Assert.True(report.Tenants.Single(t => t.TenantId == "bad").HasProblems);
        Assert.True(report.HasProblems);
        // The gap belongs to "bad" only: "good" must not inherit or hide it.
        Assert.Empty(Vendor(report, "good", SeatVendor).Gaps);
        Assert.Equal([new DateRange(D(7, 5), D(7, 7))], Vendor(report, "bad", SeatVendor).Gaps);
    }

    [Fact]
    public async Task CheckAsync_VendorFilter_ChecksOnlyThatVendor()
    {
        var repo = new InMemoryRepository()
            .Add("t1", SeatVendor, Days(D(7, 1), D(7, 3)))
            .Add("t1", VendorCatalog.GeminiEnterprise, Days(D(7, 1), D(7, 10)));

        var checker = new CoverageChecker(repo);
        var report = await checker.CheckAsync(
            ["t1"], new DateRange(D(7, 1), D(7, 10)), VendorCatalog.GeminiEnterprise.Id,
            TestContext.Current.CancellationToken);

        var only = Assert.Single(report.Tenants[0].Vendors);
        Assert.Equal(VendorCatalog.GeminiEnterprise.Id, only.Vendor.Id);
        // ChatGPT has a gap but wasn't asked about, so it must not cause a failure.
        Assert.False(report.HasProblems);
    }

    [Fact]
    public async Task CheckAsync_SingleDayPeriod_Works()
    {
        var repo = new InMemoryRepository().Add("t1", SeatVendor, Days(D(7, 1), D(7, 3)));

        var present = await Check(repo, "t1", new DateRange(D(7, 2), D(7, 2)));
        var missing = await Check(repo, "t1", new DateRange(D(7, 5), D(7, 5)));

        Assert.Equal(1, Vendor(present, "t1", SeatVendor).DaysStored);
        Assert.Empty(Vendor(present, "t1", SeatVendor).Gaps);
        Assert.Equal([new DateRange(D(7, 5), D(7, 5))], Vendor(missing, "t1", SeatVendor).Gaps);
    }

    [Fact]
    public void Catalog_MarksTheSeatPricedVendorsAndOnlyThose()
    {
        // Pinned because the exit code depends on it: marking a usage-priced vendor as
        // "expects every day" would fail every run on its quiet days.
        Assert.True(VendorCatalog.ChatGptEnterprise.ExpectsRecordEveryDay);
        Assert.True(VendorCatalog.ClaudeEnterprise.ExpectsRecordEveryDay);
        Assert.True(VendorCatalog.GeminiEnterprise.ExpectsRecordEveryDay);
        Assert.False(VendorCatalog.ClaudeApiPlatform.ExpectsRecordEveryDay);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static Task<CoverageReport> Check(InMemoryRepository repo, string tenant, DateRange period) =>
        Check(repo, [tenant], period);

    private static Task<CoverageReport> Check(InMemoryRepository repo, string[] tenants, DateRange period) =>
        new CoverageChecker(repo).CheckAsync(tenants, period, null, TestContext.Current.CancellationToken);

    private static VendorCoverage Vendor(CoverageReport report, string tenant, VendorIdentity vendor) =>
        report.Tenants.Single(t => t.TenantId == tenant).Vendors.Single(v => v.Vendor.Id == vendor.Id);

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to) =>
        new DateRange(from, to).EnumerateDays();

    private sealed class InMemoryRepository : IDailySpendRepository
    {
        private readonly List<DailySpendRecord> _records = [];

        public InMemoryRepository Add(string tenantId, VendorIdentity vendor, IEnumerable<DateOnly> dates)
        {
            _records.AddRange(dates.Select(d => new DailySpendRecord { TenantId = tenantId, VendorId = vendor.Id, Date = d }));
            return this;
        }

        public Task<IReadOnlyList<DailySpendRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendRecord>>(_records
                .Where(r => r.TenantId == tenantId && r.VendorId == vendorId && r.Date >= range.Start && r.Date <= range.End)
                .OrderBy(r => r.Date)
                .ToList());

        public Task<IReadOnlyList<DailySpendChange>> UpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DailySpendChange>> PlanUpsertAsync(
            IEnumerable<DailySpendRecord> records, bool requiresMonotonicUsage = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
