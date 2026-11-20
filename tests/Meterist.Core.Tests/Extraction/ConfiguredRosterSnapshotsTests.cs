using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

public class ConfiguredRosterSnapshotsTests
{
    private static readonly Guid Claude = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly Guid ChatGpt = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly TimeProvider Now = new FixedTime(new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Take_SnapshotsEveryVendorThatHasASourceAndACredential()
    {
        var run = Build(
            sources: [Source(Claude, Members(3)), Source(ChatGpt, Members(5))],
            credentialsFor: [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([RosterRunStatus.Succeeded, RosterRunStatus.Succeeded], results.Select(r => r.Status));
        Assert.Equal(["3 member(s)", "5 member(s)"], results.Select(r => r.Detail));
        Assert.Equal(8, run.Repository.Stored.Count);
        Assert.Equal(0, RosterExitCode.For(results));
    }

    [Fact]
    public async Task Take_ATenantWithNoCredentialForAVendor_SkipsIt_WithoutCallingItsSource_AndIsNotAFailure()
    {
        var chatGptSource = Source(ChatGpt, Members(5));
        var run = Build(sources: [Source(Claude, Members(3)), chatGptSource], credentialsFor: [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var skipped = Assert.Single(results, r => r.Status == RosterRunStatus.Skipped);
        Assert.Equal("ChatGPT Enterprise", skipped.DisplayName);
        Assert.Contains("no credential", skipped.Detail);
        Assert.Equal(0, chatGptSource.Calls);
        Assert.Equal(3, run.Repository.Stored.Count);
        Assert.Equal(0, RosterExitCode.For(results));   // a tenant that uses only some vendors is not "broken"
    }

    [Fact]
    public async Task Take_OneVendorFailing_DoesNotStopTheOthers_AndIsReportedAsFailed()
    {
        var run = Build(
            sources: [FailingSource(Claude, new HttpRequestException("429 Too Many Requests")), Source(ChatGpt, Members(5))],
            credentialsFor: [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == RosterRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.Contains("HttpRequestException", failed.Detail);
        Assert.Contains("Nothing was stored", failed.Detail);

        var ok = Assert.Single(results, r => r.Status == RosterRunStatus.Succeeded);
        Assert.Equal("ChatGPT Enterprise", ok.DisplayName);
        Assert.Equal(5, run.Repository.Stored.Count);   // the other vendor's snapshot still went in
        Assert.Equal(1, RosterExitCode.For(results));
    }

    [Fact]
    public async Task Take_ASourceThatReturnsNothing_IsAFailure_NotASkippedOrEmptySnapshot()
    {
        var run = Build(sources: [Source(Claude, [])], credentialsFor: [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results);
        Assert.Equal(RosterRunStatus.Failed, failed.Status);
        Assert.Contains("returned no members", failed.Detail);
        Assert.Empty(run.Repository.Stored);
        Assert.Equal(1, RosterExitCode.For(results));
    }

    [Fact]
    public async Task Take_ListsVendorsInCatalogOrder_WhateverOrderTheSourcesWereRegisteredIn()
    {
        var run = Build(
            sources: [Source(ChatGpt, Members(1)), Source(Claude, Members(1))],
            credentialsFor: [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var catalogOrder = VendorCatalog.All.Where(v => v.Id == Claude || v.Id == ChatGpt).Select(v => v.DisplayName);
        Assert.Equal(catalogOrder, results.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task Take_VendorsWithNoRosterSourceAreNotListedAtAll()
    {
        var run = Build(sources: [Source(Claude, Members(2))], credentialsFor: [Claude, ChatGpt, VendorCatalog.GeminiEnterprise.Id]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Claude Enterprise", Assert.Single(results).DisplayName);
    }

    [Fact]
    public async Task Take_DryRun_PlansEveryVendorAndWritesNothing()
    {
        var run = Build(sources: [Source(Claude, Members(3))], credentialsFor: [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal(RosterRunStatus.Succeeded, Assert.Single(results).Status);
        Assert.Empty(run.Repository.Stored);
        Assert.Equal(3, run.Repository.Planned);
    }

    [Fact]
    public async Task Take_TheCallersOwnCancellation_StopsTheRun_InsteadOfBeingReportedAsAVendorFailure()
    {
        using var cts = new CancellationTokenSource();
        var run = Build(
            sources: [FailingSource(Claude, new OperationCanceledException(cts.Token)), Source(ChatGpt, Members(5))],
            credentialsFor: [Claude, ChatGpt]);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Snapshots.TakeAsync("t1", false, cts.Token));

        Assert.Empty(run.Repository.Stored);   // the second vendor must not have run either
    }

    [Fact]
    public void ExitCode_IsOneOnlyWhenSomethingFailed()
    {
        RosterRunResult R(RosterRunStatus s) => new(Claude, "x", s, "");

        Assert.Equal(0, RosterExitCode.For([]));
        Assert.Equal(0, RosterExitCode.For([R(RosterRunStatus.Succeeded), R(RosterRunStatus.Skipped)]));
        Assert.Equal(1, RosterExitCode.For([R(RosterRunStatus.Succeeded), R(RosterRunStatus.Failed)]));
    }

    private static (ConfiguredRosterSnapshots Snapshots, FakeRepository Repository) Build(
        IReadOnlyList<IVendorRosterSource> sources, IEnumerable<Guid> credentialsFor)
    {
        var repository = new FakeRepository();
        var service = new RosterSnapshotService(sources, repository, Now);
        return (new ConfiguredRosterSnapshots(service, sources, new FakeSecretStore(credentialsFor)), repository);
    }

    private static List<VendorMember> Members(int count) =>
        Enumerable.Range(1, count).Select(i => new VendorMember { Id = $"m{i}", Role = "user" }).ToList();

    private static CountingSource Source(Guid vendorId, IReadOnlyList<VendorMember> members) => new(vendorId, members, null);

    private static CountingSource FailingSource(Guid vendorId, Exception failure) => new(vendorId, [], failure);

    private sealed class CountingSource(Guid vendorId, IReadOnlyList<VendorMember> members, Exception? failure) : IVendorRosterSource
    {
        public int Calls { get; private set; }

        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return failure is null ? Task.FromResult(members) : throw failure;
        }
    }

    private sealed class FakeSecretStore(IEnumerable<Guid> withCredential) : ISecretStore
    {
        private readonly HashSet<Guid> _with = [.. withCredential];

        public Task<string?> GetCredentialAsync(string tenantId, Guid vendorId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_with.Contains(vendorId) ? "secret" : null);

        public Task SetCredentialAsync(string tenantId, Guid vendorId, string credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
