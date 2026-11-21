using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

public class SpendLimitSnapshotServiceTests
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 23, 30, 0, TimeSpan.FromHours(-5));   // already the 21st in UTC

    [Fact]
    public async Task Take_StoresTheLimitsDatedTodayInUtc_AndCountsThoseWithALimit()
    {
        var repository = new FakeRepository();
        var service = Service(
            new FakeSource(Vendor,
            [
                new VendorSpendLimit { MemberId = "m1", LimitAmount = 500m, PeriodToDateSpend = 12m, SourceType = "seat_tier", SourceDetail = "tier" },
                new VendorSpendLimit { MemberId = "m2", LimitAmount = null, PeriodToDateSpend = 0m },
            ]),
            repository);

        var outcome = await service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 21), outcome.SnapshotDate);
        Assert.Equal((2, 1), (outcome.MemberCount, outcome.WithLimitCount));
        Assert.Equal(2, repository.Stored.Count);
        Assert.All(repository.Stored, r =>
        {
            Assert.Equal("t1", r.TenantId);
            Assert.Equal(Vendor, r.VendorId);
            Assert.Equal(new DateOnly(2026, 11, 21), r.SnapshotDate);
        });
        var first = repository.Stored.Single(r => r.MemberId == "m1");
        Assert.Equal((500m, 12m, "seat_tier", "tier"), (first.LimitAmount, first.PeriodToDateSpend, first.SourceType, first.SourceDetail));
        Assert.Null(repository.Stored.Single(r => r.MemberId == "m2").LimitAmount);
    }

    [Fact]
    public async Task Take_DryRun_PlansWithoutStoring()
    {
        var repository = new FakeRepository();
        var service = Service(new FakeSource(Vendor, [new VendorSpendLimit { MemberId = "m1" }]), repository);

        var outcome = await service.TakeAsync("t1", Vendor, dryRun: true, TestContext.Current.CancellationToken);

        Assert.Empty(repository.Stored);
        Assert.Equal(1, repository.Planned);
        Assert.Equal(1, outcome.Stored.Inserted);
    }

    [Fact]
    public async Task Take_NoLimits_IsRefused_SoAGoodSnapshotIsNotReplacedWithNothing()
    {
        var repository = new FakeRepository();
        var service = Service(new FakeSource(Vendor, []), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Contains("usage credits", ex.Message);
        Assert.Empty(repository.Stored);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Take_VendorWithNoSource_SaysSo()
    {
        var service = Service(new FakeSource(Guid.NewGuid(), []), new FakeRepository());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Contains("no spend-limit source", ex.Message);
    }

    [Fact]
    public async Task Take_WhenTheVendorCallFails_ReportsItAsAFailureOfTheCommand_AndStoresNothing()
    {
        var repository = new FakeRepository();
        var service = Service(new FakeSource(Vendor, [], throws: new HttpRequestException("429 Too Many Requests")), repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Contains("Could not read", ex.Message);
        Assert.Contains("HttpRequestException", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Take_AnInvalidOperationFromTheSource_PassesThroughUnwrapped()
    {
        var service = Service(
            new FakeSource(Vendor, [], throws: new InvalidOperationException("No Claude Enterprise credential configured.")),
            new FakeRepository());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Equal("No Claude Enterprise credential configured.", ex.Message);
    }

    [Fact]
    public async Task Take_CallerCancellation_IsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = Service(new FakeSource(Vendor, [], throws: new OperationCanceledException(cts.Token)), new FakeRepository());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TakeAsync("t1", Vendor, dryRun: false, cts.Token));
    }

    private static SpendLimitSnapshotService Service(IVendorSpendLimitSource source, ISpendLimitRepository repository) =>
        new([source], repository, new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class FakeSource(Guid vendorId, IReadOnlyList<VendorSpendLimit> limits, Exception? throws = null) : IVendorSpendLimitSource
    {
        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorSpendLimit>> FetchLimitsAsync(string tenantId, CancellationToken cancellationToken = default) =>
            throws is null ? Task.FromResult(limits) : Task.FromException<IReadOnlyList<VendorSpendLimit>>(throws);
    }

    private sealed class FakeRepository : ISpendLimitRepository
    {
        public List<SpendLimitRecord> Stored { get; } = [];

        public int Planned { get; private set; }

        public int Calls { get; private set; }

        public Task<SpendLimitSnapshotResult> ReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<SpendLimitRecord> limits, CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = limits.ToList();
            Stored.AddRange(list);
            return Task.FromResult(new SpendLimitSnapshotResult(list.Count, 0, 0, 0));
        }

        public Task<SpendLimitSnapshotResult> PlanReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<SpendLimitRecord> limits, CancellationToken cancellationToken = default)
        {
            Calls++;
            var count = limits.Count();
            Planned += count;
            return Task.FromResult(new SpendLimitSnapshotResult(count, 0, 0, 0));
        }

        public Task<SpendLimitSnapshot?> GetLatestSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<SpendLimitSnapshot?>(null);
    }
}
