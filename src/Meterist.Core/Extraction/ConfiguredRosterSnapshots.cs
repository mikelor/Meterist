using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

public enum RosterRunStatus
{
    Succeeded,

    /// <summary>Not attempted: the tenant has no credential stored for this vendor.</summary>
    Skipped,

    Failed,
}

/// <summary>
/// What one vendor's roster snapshot did for one tenant. <see cref="Outcome"/> is set only when it succeeded.
/// <see cref="Detail"/> names no member: it is a count line, a reason for skipping, or an error message.
/// </summary>
public sealed record RosterRunResult(
    Guid VendorId, string DisplayName, RosterRunStatus Status, string Detail, RosterSnapshotOutcome? Outcome = null);

/// <summary>
/// Takes the roster snapshot for every vendor that has a roster source and that the tenant has a credential for,
/// so an unattended run needs no list of who has what. A vendor the tenant does not use is
/// <see cref="RosterRunStatus.Skipped"/>, not a failure, and one vendor failing never stops the others: each
/// vendor's snapshot is independent, and a snapshot that fails stores nothing (see <see cref="RosterSnapshotService"/>).
/// </summary>
public sealed class ConfiguredRosterSnapshots
{
    private readonly RosterSnapshotService _service;
    private readonly IEnumerable<IVendorRosterSource> _sources;
    private readonly ISecretStore _secretStore;

    public ConfiguredRosterSnapshots(
        RosterSnapshotService service, IEnumerable<IVendorRosterSource> sources, ISecretStore secretStore)
    {
        _service = service;
        _sources = sources;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<RosterRunResult>> TakeAsync(
        string tenantId, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var results = new List<RosterRunResult>();

        // Catalog order, so the output and the log read the same every day.
        foreach (var vendor in VendorCatalog.All.Where(v => _sources.Any(s => s.VendorId == v.Id)))
        {
            // Everything for one vendor, including reading its credential and writing the snapshot, is inside the
            // try: whatever goes wrong is that vendor's failure, and the next vendor and tenant still run.
            try
            {
                var credential = await _secretStore
                    .GetCredentialAsync(tenantId, vendor.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(credential))
                {
                    results.Add(new RosterRunResult(
                        vendor.Id, vendor.DisplayName, RosterRunStatus.Skipped, "no credential stored for this tenant"));
                    continue;
                }

                var skipReason = await _sources.First(s => s.VendorId == vendor.Id)
                    .GetSkipReasonAsync(tenantId, cancellationToken).ConfigureAwait(false);
                if (skipReason is not null)
                {
                    results.Add(new RosterRunResult(vendor.Id, vendor.DisplayName, RosterRunStatus.Skipped, skipReason));
                    continue;
                }

                var outcome = await _service.TakeAsync(tenantId, vendor.Id, dryRun, cancellationToken).ConfigureAwait(false);
                results.Add(new RosterRunResult(
                    vendor.Id, vendor.DisplayName, RosterRunStatus.Succeeded,
                    $"{outcome.MemberCount} member(s)", outcome));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The service already turns the expected vendor failures into an InvalidOperationException with a
                // clear message; an unreadable credential is one too. Anything else (a database write that failed,
                // a credential file that could not be opened) is named by its type, with its own message and not its
                // inner exceptions, which can echo values. A caller's own cancellation is not caught and stops the run.
                results.Add(new RosterRunResult(
                    vendor.Id, vendor.DisplayName, RosterRunStatus.Failed,
                    ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return results;
    }
}

public static class RosterExitCode
{
    /// <summary>
    /// 1 if any roster snapshot failed, otherwise 0. Skipped vendors are not failures, so a tenant that uses only some
    /// vendors does not make a scheduled run look broken.
    /// </summary>
    public static int For(IEnumerable<RosterRunResult> results) =>
        results.Any(r => r.Status == RosterRunStatus.Failed) ? 1 : 0;
}
