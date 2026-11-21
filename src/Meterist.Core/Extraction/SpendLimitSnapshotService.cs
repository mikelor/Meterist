using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>What taking a spend-limit snapshot found, and what it stored (or, for a dry run, would store).</summary>
public sealed record SpendLimitSnapshotOutcome(
    DateOnly SnapshotDate, int MemberCount, int WithLimitCount, SpendLimitSnapshotResult Stored);

/// <summary>
/// Takes a dated snapshot of a vendor's per-member spend limits and stores it. Like the roster this is current state,
/// not a date range, so it is separate from extraction and is run on its own.
/// </summary>
public sealed class SpendLimitSnapshotService
{
    private readonly IEnumerable<IVendorSpendLimitSource> _sources;
    private readonly ISpendLimitRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SpendLimitSnapshotService(
        IEnumerable<IVendorSpendLimitSource> sources, ISpendLimitRepository repository, TimeProvider? timeProvider = null)
    {
        _sources = sources;
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <exception cref="InvalidOperationException">
    /// The vendor has no spend-limit source, the source could not read the limits (a rejected key, a rate limit or
    /// server error, a timeout or open circuit, a response that is not valid JSON), or it returned none. The original
    /// exception is kept as the inner exception. Cancellation requested by the caller is not wrapped.
    /// </exception>
    public async Task<SpendLimitSnapshotOutcome> TakeAsync(
        string tenantId, Guid vendorId, bool dryRun, CancellationToken cancellationToken = default)
    {
        var vendorName = VendorCatalog.FindById(vendorId)?.DisplayName ?? vendorId.ToString();

        var source = _sources.FirstOrDefault(s => s.VendorId == vendorId)
            ?? throw new InvalidOperationException(
                $"{vendorName} has no spend-limit source, so there is nothing to snapshot.");

        IReadOnlyList<VendorSpendLimit> limits;
        try
        {
            limits = await source.FetchLimitsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Every way talking to the vendor can fail is an expected failure of this command, reported with a
            // non-zero exit, with nothing stored. Neither the type name nor the message carries a member.
            throw new InvalidOperationException(
                $"Could not read the {vendorName} spend limits for tenant '{tenantId}': "
                + $"{ex.GetType().Name}: {ex.Message.TrimEnd('.')}. Nothing was stored.",
                ex);
        }

        // The vendor returns one row per current member, so none at all is a scope or plan problem. Storing it would
        // replace a good snapshot taken earlier the same day with nothing.
        if (limits.Count == 0)
        {
            throw new InvalidOperationException(
                $"{vendorName} returned no spend limits for tenant '{tenantId}'. An organisation has at least one member, "
                + "so this is not stored: check that usage credits are enabled and the key has the spend-limits scope.");
        }

        var date = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var records = limits.Select(l => new SpendLimitRecord
        {
            TenantId = tenantId,
            VendorId = vendorId,
            SnapshotDate = date,
            MemberId = l.MemberId,
            Email = l.Email,
            Name = l.Name,
            LimitAmount = l.LimitAmount,
            PeriodToDateSpend = l.PeriodToDateSpend,
            PriorPeriodSpend = l.PriorPeriodSpend,
            Currency = l.Currency,
            Period = l.Period,
            SourceType = l.SourceType,
            SourceDetail = l.SourceDetail,
            SpendLimitId = l.SpendLimitId,
        }).ToList();

        var stored = dryRun
            ? await _repository.PlanReplaceSnapshotAsync(tenantId, vendorId, date, records, cancellationToken).ConfigureAwait(false)
            : await _repository.ReplaceSnapshotAsync(tenantId, vendorId, date, records, cancellationToken).ConfigureAwait(false);

        return new SpendLimitSnapshotOutcome(date, records.Count, records.Count(r => r.LimitAmount is not null), stored);
    }
}
