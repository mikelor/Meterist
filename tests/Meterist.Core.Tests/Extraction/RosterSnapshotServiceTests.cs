using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

public class RosterSnapshotServiceTests
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;

    // 2026-11-20 23:30 UTC: late enough that a local-time bug would put the snapshot on the 21st.
    private static readonly TimeProvider Now = new FixedTime(new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero));

    [Fact]
    public async Task Take_StoresTheMembersUnderTodaysUtcDate_WithRoleCounts()
    {
        var repo = new FakeRepository();
        var service = Service(repo, [Member("m1", "owner"), Member("m2", "user"), Member("m3", "user"), Member("m4", null)]);

        var outcome = await service.TakeAsync("t1", Vendor, dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 20), outcome.SnapshotDate);
        Assert.Equal(4, outcome.MemberCount);
        Assert.Equal(1, outcome.RoleCounts["owner"]);
        Assert.Equal(2, outcome.RoleCounts["user"]);
        Assert.Equal(1, outcome.RoleCounts["(none)"]);

        Assert.Equal(["m1", "m2", "m3", "m4"], repo.Stored.Select(m => m.MemberId));
        Assert.All(repo.Stored, m =>
        {
            Assert.Equal("t1", m.TenantId);
            Assert.Equal(Vendor, m.VendorId);
            Assert.Equal(new DateOnly(2026, 11, 20), m.SnapshotDate);
        });
        Assert.Equal(0, repo.Planned);
    }

    [Fact]
    public async Task Take_CarriesEveryFieldFromTheVendorMemberToTheStoredRecord()
    {
        var repo = new FakeRepository();
        var added = new DateTimeOffset(2026, 3, 30, 0, 4, 14, TimeSpan.Zero);
        var service = Service(repo, [new VendorMember
        {
            Id = "m1", Email = "a@example.test", Name = "Alex Example", Role = "owner", SeatType = "Premium", IsScimManaged = true, AddedAt = added,
        }]);

        await service.TakeAsync("t1", Vendor, false, TestContext.Current.CancellationToken);

        var stored = Assert.Single(repo.Stored);
        Assert.Equal("a@example.test", stored.Email);
        Assert.Equal("Alex Example", stored.Name);
        Assert.Equal("owner", stored.Role);
        Assert.Equal("Premium", stored.SeatType);
        Assert.True(stored.IsScimManaged);
        Assert.Equal(added, stored.AddedAt);
    }

    [Fact]
    public async Task Take_DryRun_PlansAndWritesNothing()
    {
        var repo = new FakeRepository();
        var service = Service(repo, [Member("m1", "user")]);

        var outcome = await service.TakeAsync("t1", Vendor, dryRun: true, TestContext.Current.CancellationToken);

        Assert.Empty(repo.Stored);
        Assert.Equal(1, repo.Planned);
        Assert.Equal(1, outcome.Stored.Inserted);
    }

    [Fact]
    public async Task Take_AnEmptyList_IsRefused_SoAGoodSnapshotIsNeverReplacedByNothing()
    {
        var repo = new FakeRepository();
        var service = Service(repo, []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, false, TestContext.Current.CancellationToken));

        Assert.Contains("returned no members", ex.Message);
        Assert.Contains("scopes", ex.Message);
        Assert.Empty(repo.Stored);
        Assert.Equal(0, repo.Planned);
    }

    [Fact]
    public async Task Take_AVendorWithNoRosterSource_SaysSo()
    {
        var service = Service(new FakeRepository(), [Member("m1", "user")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TakeAsync(
            "t1", VendorCatalog.GeminiEnterprise.Id, false, TestContext.Current.CancellationToken));

        Assert.Contains("Gemini Enterprise", ex.Message);
        Assert.Contains("nothing to snapshot", ex.Message);
    }

    public static TheoryData<Exception> ExpectedVendorFailures() => new()
    {
        new HttpRequestException("Response status code does not indicate success: 429 (Too Many Requests).", null, System.Net.HttpStatusCode.TooManyRequests),
        new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable)."),
        new System.Text.Json.JsonException("The JSON value could not be converted. Path: $.data | LineNumber: 0."),
        new TimeoutException("The operation timed out."),
        new TaskCanceledException("A task was canceled (an HTTP timeout, not the caller)."),
        new CustomResilienceException("The circuit is open."),
    };

    [Theory]
    [MemberData(nameof(ExpectedVendorFailures))]
    public async Task Take_AFailureTalkingToTheVendor_IsAControlledFailure_NotACrash_AndStoresNothing(Exception failure)
    {
        var repo = new FakeRepository();
        var service = new RosterSnapshotService([new ThrowingSource(Vendor, failure)], repo, Now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, false, TestContext.Current.CancellationToken));

        Assert.Same(failure, ex.InnerException);
        Assert.Contains("Could not read the Claude Enterprise member list", ex.Message);
        Assert.Contains("'t1'", ex.Message);
        Assert.Contains(failure.GetType().Name, ex.Message);
        Assert.Contains("Nothing was stored", ex.Message);
        Assert.DoesNotContain("..", ex.Message);
        Assert.Empty(repo.Stored);
        Assert.Equal(0, repo.Planned);
    }

    [Fact]
    public async Task Take_ACallersOwnCancellation_IsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        var repo = new FakeRepository();
        var service = new RosterSnapshotService([new ThrowingSource(Vendor, new OperationCanceledException(cts.Token))], repo, Now);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TakeAsync("t1", Vendor, false, cts.Token));

        Assert.Empty(repo.Stored);
    }

    [Fact]
    public async Task Take_AMessageTheSourceAlreadyWroteForAnOperator_PassesThroughUnchanged()
    {
        // "No credential configured", "key lacks the scope" and similar are already clear; wrapping them would bury them.
        var service = new RosterSnapshotService(
            [new ThrowingSource(Vendor, new InvalidOperationException("No Claude Enterprise credential configured for tenant 't1'."))],
            new FakeRepository(), Now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeAsync("t1", Vendor, false, TestContext.Current.CancellationToken));

        Assert.Equal("No Claude Enterprise credential configured for tenant 't1'.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    // Stands in for a resilience-library exception (a rejected execution, an open circuit): not an HTTP or timeout type.
    private sealed class CustomResilienceException(string message) : Exception(message);

    private static RosterSnapshotService Service(FakeRepository repo, IReadOnlyList<VendorMember> members) =>
        new([new FakeSource(Vendor, members)], repo, Now);

    private static VendorMember Member(string id, string? role) => new() { Id = id, Role = role };

    private sealed class FakeSource(Guid vendorId, IReadOnlyList<VendorMember> members) : IVendorRosterSource
    {
        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(members);
    }

    private sealed class ThrowingSource(Guid vendorId, Exception failure) : IVendorRosterSource
    {
        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default) =>
            throw failure;
    }

    private sealed class FakeRepository : IMemberRosterRepository
    {
        public List<MemberRosterRecord> Stored { get; } = [];

        public int Planned { get; private set; }

        public Task<RosterSnapshotResult> ReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members,
            CancellationToken cancellationToken = default)
        {
            var list = members.ToList();
            Stored.AddRange(list);
            return Task.FromResult(new RosterSnapshotResult(list.Count, 0, 0, 0));
        }

        public Task<RosterSnapshotResult> PlanReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members,
            CancellationToken cancellationToken = default)
        {
            var count = members.Count();
            Planned += count;
            return Task.FromResult(new RosterSnapshotResult(count, 0, 0, 0));
        }

        public Task<RosterSnapshot?> GetLatestSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<RosterSnapshot?>(null);

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(
            string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>([]);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
