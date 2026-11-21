using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

public enum SpendLimitRunStatus
{
    Succeeded,

    /// <summary>Not attempted: the tenant has no credential stored for this vendor.</summary>
    Skipped,

    Failed,
}

public sealed record SpendLimitRunResult(
    Guid VendorId, string DisplayName, SpendLimitRunStatus Status, string Detail, SpendLimitSnapshotOutcome? Outcome = null);

/// <summary>
/// Takes the spend-limit snapshot for every vendor that has a spend-limit source and that one tenant has a credential for,
/// skipping the rest, so a scheduled run can do it for a tenant without knowing which vendors it uses. The same shape and
/// the same rules as <see cref="ConfiguredRosterSnapshots"/>: one vendor failing does not stop the others, an unreadable
/// credential or a failed save is that vendor's failure and not an abort, and the caller gives each tenant its own scope.
/// <para>
/// <b>A rejected key is a failure, not a skip.</b> The Claude Spend Limits API answers 401 or 403 when the key lacks
/// <c>read:spend_limits</c> or the organisation has not turned usage credits on, and ChatGPT answers the same when the key
/// lacks the usage-limit scope. These figures exist only as a point in time and cannot be backfilled, so quietly skipping a
/// tenant that cannot be read would lose days nobody can recover and never be noticed. The failure names the scope to
/// check. A tenant that genuinely has no spend limits to read should have no credential stored for that vendor (a skip) or be
/// left out of the run's tenant list.
/// </para>
/// </summary>
public sealed class ConfiguredSpendLimitSnapshots
{
    private readonly SpendLimitSnapshotService _service;
    private readonly IEnumerable<IVendorSpendLimitSource> _sources;
    private readonly ISecretStore _secretStore;

    public ConfiguredSpendLimitSnapshots(
        SpendLimitSnapshotService service, IEnumerable<IVendorSpendLimitSource> sources, ISecretStore secretStore)
    {
        _service = service;
        _sources = sources;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<SpendLimitRunResult>> TakeAsync(
        string tenantId, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var results = new List<SpendLimitRunResult>();

        // Catalog order, so the output and the log read the same every day.
        foreach (var vendor in VendorCatalog.All.Where(v => _sources.Any(s => s.VendorId == v.Id)))
        {
            // Everything for one vendor, including reading its credential and writing the snapshot, is inside the try:
            // whatever goes wrong is that vendor's failure, and the next vendor and tenant still run.
            try
            {
                var credential = await _secretStore
                    .GetCredentialAsync(tenantId, vendor.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(credential))
                {
                    results.Add(new SpendLimitRunResult(
                        vendor.Id, vendor.DisplayName, SpendLimitRunStatus.Skipped, "no credential stored for this tenant"));
                    continue;
                }

                var outcome = await _service.TakeAsync(tenantId, vendor.Id, dryRun, cancellationToken).ConfigureAwait(false);
                results.Add(new SpendLimitRunResult(
                    vendor.Id, vendor.DisplayName, SpendLimitRunStatus.Succeeded,
                    $"{outcome.MemberCount} member(s), {outcome.WithLimitCount} with a limit", outcome));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The service turns the expected vendor failures into an InvalidOperationException with a clear message,
                // and an unreadable credential is one too. Anything else (a database write that failed, a credential file
                // that could not be opened) is named by its type, with its own message and not its inner exceptions, which
                // can echo values. A caller's own cancellation is not caught and stops the run.
                results.Add(new SpendLimitRunResult(
                    vendor.Id, vendor.DisplayName, SpendLimitRunStatus.Failed,
                    ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return results;
    }
}

public static class SpendLimitExitCode
{
    /// <summary>1 if any vendor failed, otherwise 0. A skipped vendor (no credential) is not a failure.</summary>
    public static int For(IEnumerable<SpendLimitRunResult> results) =>
        results.Any(r => r.Status == SpendLimitRunStatus.Failed) ? 1 : 0;
}
