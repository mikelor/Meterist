using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

public class ConfiguredSpendLimitSnapshotsTests
{
    private static readonly Guid Claude = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly Guid ChatGpt = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly TimeProvider Now = new FixedTime(new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Take_SnapshotsEveryVendorThatHasASourceAndACredential()
    {
        var run = Build([Source(Claude, Limits(3, withLimit: 2)), Source(ChatGpt, Limits(5, withLimit: 5))], [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([SpendLimitRunStatus.Succeeded, SpendLimitRunStatus.Succeeded], results.Select(r => r.Status));
        Assert.Equal(["3 member(s), 2 with a limit", "5 member(s), 5 with a limit"], results.Select(r => r.Detail));
        Assert.Equal(8, run.Repository.Stored.Count);
        Assert.Equal(0, SpendLimitExitCode.For(results));
    }

    [Fact]
    public async Task Take_ATenantWithNoCredentialForAVendor_SkipsIt_WithoutCallingItsSource_AndIsNotAFailure()
    {
        var chatGptSource = Source(ChatGpt, Limits(5, 5));
        var run = Build([Source(Claude, Limits(3, 3)), chatGptSource], [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var skipped = Assert.Single(results, r => r.Status == SpendLimitRunStatus.Skipped);
        Assert.Equal("ChatGPT Enterprise", skipped.DisplayName);
        Assert.Contains("no credential", skipped.Detail);
        Assert.Equal(0, chatGptSource.Calls);
        Assert.Equal(3, run.Repository.Stored.Count);
        Assert.Equal(0, SpendLimitExitCode.For(results));
    }

    [Fact]
    public async Task Take_OneVendorFailing_DoesNotStopTheOthers_AndIsReportedAsFailed()
    {
        var run = Build(
            [FailingSource(Claude, new HttpRequestException("429 Too Many Requests")), Source(ChatGpt, Limits(5, 5))],
            [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == SpendLimitRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.Contains("HttpRequestException", failed.Detail);
        Assert.Contains("Nothing was stored", failed.Detail);
        Assert.Equal(SpendLimitRunStatus.Succeeded, Assert.Single(results, r => r.Status == SpendLimitRunStatus.Succeeded).Status);
        Assert.Equal(5, run.Repository.Stored.Count);   // the other vendor's snapshot still went in
        Assert.Equal(1, SpendLimitExitCode.For(results));
    }

    [Theory]
    [InlineData("The Claude spend limits returned 403. Check that the key has the read:spend_limits scope and that usage credits are turned on for the organisation.")]
    [InlineData("The ChatGPT Enterprise spend limits returned 401. Check that the admin key can read usage limits (scope chatgpt.enterprise.usage_limit.read).")]
    public async Task Take_AKeyTheVendorRejectsForSpendLimits_IsAFailureNamingTheScope_NotASkip(string vendorMessage)
    {
        // Spend limits exist only as a point in time and cannot be backfilled, so a tenant that cannot be read must be noticed.
        var run = Build([FailingSource(Claude, new InvalidOperationException(vendorMessage))], [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results);
        Assert.Equal(SpendLimitRunStatus.Failed, failed.Status);
        Assert.Equal(vendorMessage, failed.Detail);   // the vendor's own message, naming the scope, not wrapped in a type name
        Assert.Contains("scope", failed.Detail);
        Assert.Equal(1, SpendLimitExitCode.For(results));
    }

    [Fact]
    public async Task Take_ASourceThatReturnsNothing_IsAFailure_NotAnEmptySnapshot()
    {
        var run = Build([Source(Claude, [])], [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results);
        Assert.Equal(SpendLimitRunStatus.Failed, failed.Status);
        Assert.Contains("returned no spend limits", failed.Detail);
        Assert.Empty(run.Repository.Stored);
    }

    [Fact]
    public async Task Take_AnUnreadableCredential_IsAFailure_NotASkip_AndTheOtherVendorStillRuns()
    {
        var unreadable = new UnreadableCredentialException("t1", new System.Security.Cryptography.CryptographicException("nope"));
        var run = Build(
            [Source(Claude, Limits(2, 2)), Source(ChatGpt, Limits(4, 4))],
            new ScriptedSecretStore(("t1", Claude, unreadable), ("t1", ChatGpt, "secret")));

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == SpendLimitRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.Equal(unreadable.Message, failed.Detail);
        Assert.DoesNotContain(results, r => r.Status == SpendLimitRunStatus.Skipped);
        Assert.Equal(4, run.Repository.Stored.Count);
        Assert.Equal(1, SpendLimitExitCode.For(results));
    }

    [Fact]
    public async Task Take_APersistenceFailure_IsThatVendorsFailure_AndTheNextVendorStillStores()
    {
        var run = Build(
            [Source(Claude, Limits(2, 2)), Source(ChatGpt, Limits(4, 4))],
            new ScriptedSecretStore(("t1", Claude, "secret"), ("t1", ChatGpt, "secret")),
            failSaveFor: Claude);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == SpendLimitRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.StartsWith("IOException:", failed.Detail);   // a non-vendor error is named by type
        Assert.Equal(4, run.Repository.Stored.Count);
        Assert.Equal(1, SpendLimitExitCode.For(results));
    }

    [Fact]
    public async Task Take_ListsVendorsInCatalogOrder_WhateverOrderTheSourcesWereRegisteredIn()
    {
        var run = Build([Source(ChatGpt, Limits(1, 1)), Source(Claude, Limits(1, 1))], [Claude, ChatGpt]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var catalogOrder = VendorCatalog.All.Where(v => v.Id == Claude || v.Id == ChatGpt).Select(v => v.DisplayName);
        Assert.Equal(catalogOrder, results.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task Take_VendorsWithNoSpendLimitSourceAreNotListedAtAll()
    {
        var run = Build([Source(Claude, Limits(2, 2))], [Claude, ChatGpt, VendorCatalog.GeminiEnterprise.Id]);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Claude Enterprise", Assert.Single(results).DisplayName);
    }

    [Fact]
    public async Task Take_DryRun_PlansEveryVendorAndWritesNothing()
    {
        var run = Build([Source(Claude, Limits(3, 3))], [Claude]);

        var results = await run.Snapshots.TakeAsync("t1", dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal(SpendLimitRunStatus.Succeeded, Assert.Single(results).Status);
        Assert.Empty(run.Repository.Stored);
        Assert.Equal(3, run.Repository.Planned);
    }

    [Fact]
    public async Task Take_TheCallersOwnCancellation_StopsTheRun_InsteadOfBeingReportedAsAVendorFailure()
    {
        using var cts = new CancellationTokenSource();
        var run = Build(
            [FailingSource(Claude, new OperationCanceledException(cts.Token)), Source(ChatGpt, Limits(5, 5))], [Claude, ChatGpt]);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Snapshots.TakeAsync("t1", false, cts.Token));

        Assert.Empty(run.Repository.Stored);   // the second vendor must not have run either
    }

    [Fact]
    public void ExitCode_IsOneOnlyWhenSomethingFailed()
    {
        SpendLimitRunResult R(SpendLimitRunStatus s) => new(Claude, "x", s, "");

        Assert.Equal(0, SpendLimitExitCode.For([]));
        Assert.Equal(0, SpendLimitExitCode.For([R(SpendLimitRunStatus.Succeeded), R(SpendLimitRunStatus.Skipped)]));
        Assert.Equal(1, SpendLimitExitCode.For([R(SpendLimitRunStatus.Succeeded), R(SpendLimitRunStatus.Failed)]));
    }

    // ---- builders and fakes

    private static (ConfiguredSpendLimitSnapshots Snapshots, FakeRepository Repository) Build(
        IReadOnlyList<IVendorSpendLimitSource> sources, IEnumerable<Guid> credentialsFor) =>
        Build(sources, new ScriptedSecretStore(credentialsFor.Select(id => ("t1", id, (object)"secret")).ToArray()));

    private static (ConfiguredSpendLimitSnapshots Snapshots, FakeRepository Repository) Build(
        IReadOnlyList<IVendorSpendLimitSource> sources, ScriptedSecretStore store, Guid? failSaveFor = null)
    {
        var repository = new FakeRepository(failSaveFor);
        var service = new SpendLimitSnapshotService(sources, repository, Now);
        return (new ConfiguredSpendLimitSnapshots(service, sources, store), repository);
    }

    private static List<VendorSpendLimit> Limits(int count, int withLimit) =>
        Enumerable.Range(1, count)
            .Select(i => new VendorSpendLimit { MemberId = $"m{i}", LimitAmount = i <= withLimit ? 100m : null, PeriodToDateSpend = 1m })
            .ToList();

    private static CountingSource Source(Guid vendorId, IReadOnlyList<VendorSpendLimit> limits) => new(vendorId, limits, null);

    private static CountingSource FailingSource(Guid vendorId, Exception failure) => new(vendorId, [], failure);

    private sealed class CountingSource(Guid vendorId, IReadOnlyList<VendorSpendLimit> limits, Exception? failure) : IVendorSpendLimitSource
    {
        public int Calls { get; private set; }

        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorSpendLimit>> FetchLimitsAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return failure is null ? Task.FromResult(limits) : throw failure;
        }
    }

    // Each entry is a credential string, or an exception to throw when it is read; a vendor with no entry has no credential.
    private sealed class ScriptedSecretStore(params (string Tenant, Guid Vendor, object Outcome)[] script) : ISecretStore
    {
        public Task<string?> GetCredentialAsync(string tenantId, Guid vendorId, CancellationToken cancellationToken = default)
        {
            var entry = script.FirstOrDefault(s => s.Tenant == tenantId && s.Vendor == vendorId);
            return entry.Outcome switch
            {
                null => Task.FromResult<string?>(null),
                Exception ex => throw ex,
                var value => Task.FromResult<string?>((string)value),
            };
        }

        public Task SetCredentialAsync(string tenantId, Guid vendorId, string credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeRepository(Guid? failSaveFor = null) : ISpendLimitRepository
    {
        public List<SpendLimitRecord> Stored { get; } = [];

        public int Planned { get; private set; }

        public Task<SpendLimitSnapshotResult> ReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<SpendLimitRecord> limits,
            CancellationToken cancellationToken = default)
        {
            if (vendorId == failSaveFor)
            {
                // Stands in for an EF save failure (Core cannot name the type): a database error after a successful fetch.
                throw new IOException("The database is locked.");
            }

            var list = limits.ToList();
            Stored.AddRange(list);
            return Task.FromResult(new SpendLimitSnapshotResult(list.Count, 0, 0, 0));
        }

        public Task<SpendLimitSnapshotResult> PlanReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<SpendLimitRecord> limits,
            CancellationToken cancellationToken = default)
        {
            var count = limits.Count();
            Planned += count;
            return Task.FromResult(new SpendLimitSnapshotResult(count, 0, 0, 0));
        }

        public Task<SpendLimitSnapshot?> GetLatestSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly? onOrBefore = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<SpendLimitSnapshot?>(null);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
