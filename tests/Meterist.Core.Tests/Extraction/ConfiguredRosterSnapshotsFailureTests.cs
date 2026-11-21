using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Extraction;

/// <summary>
/// Everything one vendor does inside <see cref="ConfiguredRosterSnapshots"/> is that vendor's failure, never the
/// run's: reading its credential and writing its snapshot included. And an unreadable credential is a failure, never
/// a skip, because a skip exits 0 having collected nothing.
/// </summary>
public class ConfiguredRosterSnapshotsFailureTests
{
    private static readonly Guid Claude = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly Guid ChatGpt = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly TimeProvider Now = new FixedTime(new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AnUnreadableCredential_IsAFailure_NotASkip_AndTheOtherVendorStillRuns()
    {
        var unreadable = new UnreadableCredentialException("t1", new System.Security.Cryptography.CryptographicException("nope"));
        var run = Build(
            new ScriptedSecretStore(("t1", Claude, unreadable), ("t1", ChatGpt, "secret")),
            new FakeRepository());

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == RosterRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.Equal(unreadable.Message, failed.Detail);      // the operator-facing message, not wrapped in a type name
        Assert.Contains("Windows account", failed.Detail);
        Assert.DoesNotContain(results, r => r.Status == RosterRunStatus.Skipped);

        Assert.Equal(RosterRunStatus.Succeeded, Assert.Single(results, r => r.DisplayName == "ChatGPT Enterprise").Status);
        Assert.Equal(1, RosterExitCode.For(results));         // the run must not exit 0
    }

    [Fact]
    public async Task AnyOtherCredentialReadFailure_IsAlsoThatVendorsFailure_NotARunAbort()
    {
        var run = Build(
            new ScriptedSecretStore(("t1", Claude, new IOException("The file is locked.")), ("t1", ChatGpt, "secret")),
            new FakeRepository());

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == RosterRunStatus.Failed);
        Assert.Equal("IOException: The file is locked.", failed.Detail);
        Assert.Equal(RosterRunStatus.Succeeded, Assert.Single(results, r => r.DisplayName == "ChatGPT Enterprise").Status);
        Assert.Equal(1, RosterExitCode.For(results));
    }

    [Fact]
    public async Task APersistenceFailure_IsThatVendorsFailure_AndTheNextVendorStillStores()
    {
        // Stands in for an EF save failure (Core cannot name the type): a database error after a successful fetch.
        var repo = new FakeRepository(failFor: Claude);
        var run = Build(new ScriptedSecretStore(("t1", Claude, "secret"), ("t1", ChatGpt, "secret")), repo);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(results, r => r.Status == RosterRunStatus.Failed);
        Assert.Equal("Claude Enterprise", failed.DisplayName);
        Assert.Contains("SimulatedDatabaseException", failed.Detail);
        Assert.Contains("could not save", failed.Detail);

        Assert.Equal(RosterRunStatus.Succeeded, Assert.Single(results, r => r.DisplayName == "ChatGPT Enterprise").Status);
        Assert.All(repo.Stored, m => Assert.Equal(ChatGpt, m.VendorId));    // only the vendor that succeeded was stored
        Assert.Equal(1, RosterExitCode.For(results));
    }

    [Fact]
    public async Task APersistenceFailureMessage_NamesItsTypeAndMessageOnly_NotItsInnerException()
    {
        var repo = new FakeRepository(failFor: Claude, innerDetail: "UNIQUE constraint failed: value alice@example.test");
        var run = Build(new ScriptedSecretStore(("t1", Claude, "secret")), repo);

        var results = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);
        var failed = Assert.Single(results, r => r.DisplayName == "Claude Enterprise");

        Assert.Equal(RosterRunStatus.Failed, failed.Status);
        Assert.DoesNotContain("alice@example.test", failed.Detail);
        Assert.DoesNotContain("UNIQUE", failed.Detail);
    }

    [Fact]
    public async Task ACallersOwnCancellation_StillStopsTheRun()
    {
        using var cts = new CancellationTokenSource();
        var run = Build(
            new ScriptedSecretStore(("t1", Claude, new OperationCanceledException(cts.Token)), ("t1", ChatGpt, "secret")),
            new FakeRepository());
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Snapshots.TakeAsync("t1", false, cts.Token));
    }

    [Fact]
    public async Task AFailureForOneTenant_DoesNotAffectTheNextTenant()
    {
        var run = Build(
            new ScriptedSecretStore(("t1", Claude, new UnreadableCredentialException("t1", new Exception("x"))), ("t2", Claude, "secret")),
            new FakeRepository());

        var first = await run.Snapshots.TakeAsync("t1", cancellationToken: TestContext.Current.CancellationToken);
        var second = await run.Snapshots.TakeAsync("t2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RosterRunStatus.Failed, Assert.Single(first, r => r.DisplayName == "Claude Enterprise").Status);
        Assert.Equal(RosterRunStatus.Succeeded, Assert.Single(second, r => r.DisplayName == "Claude Enterprise").Status);
    }

    private static (ConfiguredRosterSnapshots Snapshots, FakeRepository Repository) Build(
        ScriptedSecretStore secrets, FakeRepository repository)
    {
        IReadOnlyList<IVendorRosterSource> sources = [new Source(Claude), new Source(ChatGpt)];
        var service = new RosterSnapshotService(sources, repository, Now);
        return (new ConfiguredRosterSnapshots(service, sources, secrets), repository);
    }

    private sealed class Source(Guid vendorId) : IVendorRosterSource
    {
        public Guid VendorId => vendorId;

        public Task<IReadOnlyList<VendorMember>> FetchMembersAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorMember>>([new VendorMember { Id = "m1", Role = "user" }, new VendorMember { Id = "m2" }]);
    }

    /// <summary>For each (tenant, vendor): a credential string, or an exception to throw when it is read. Anything unlisted has none.</summary>
    private sealed class ScriptedSecretStore(params (string Tenant, Guid Vendor, object Outcome)[] script) : ISecretStore
    {
        public Task<string?> GetCredentialAsync(string tenantId, Guid vendorId, CancellationToken cancellationToken = default)
        {
            var entry = script.FirstOrDefault(s => s.Tenant == tenantId && s.Vendor == vendorId);
            return entry.Outcome switch
            {
                null => Task.FromResult<string?>(null),
                Exception ex => throw ex,
                string value => Task.FromResult<string?>(value),
                _ => throw new InvalidOperationException("bad script"),
            };
        }

        public Task SetCredentialAsync(string tenantId, Guid vendorId, string credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SimulatedDatabaseException(string message, Exception? inner) : Exception(message, inner);

    private sealed class FakeRepository(Guid? failFor = null, string? innerDetail = null) : IMemberRosterRepository
    {
        public List<MemberRosterRecord> Stored { get; } = [];

        public Task<RosterSnapshotResult> ReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members,
            CancellationToken cancellationToken = default)
        {
            if (vendorId == failFor)
            {
                throw new SimulatedDatabaseException(
                    "An error occurred while saving the entity changes. See the inner exception for details. (could not save)",
                    innerDetail is null ? null : new Exception(innerDetail));
            }

            var list = members.ToList();
            Stored.AddRange(list);
            return Task.FromResult(new RosterSnapshotResult(list.Count, 0, 0, 0));
        }

        public Task<RosterSnapshotResult> PlanReplaceSnapshotAsync(
            string tenantId, Guid vendorId, DateOnly snapshotDate, IEnumerable<MemberRosterRecord> members,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RosterSnapshotResult(members.Count(), 0, 0, 0));

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
