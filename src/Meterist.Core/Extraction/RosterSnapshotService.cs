using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>What taking a roster snapshot found, and what it stored (or, for a dry run, would store).</summary>
/// <param name="EmptyNotStored">
/// The vendor's list was legitimately empty (a licence list nobody is on yet), so nothing was stored: an empty snapshot would
/// read as everyone having left, and the latest earlier snapshot, if any, stays the latest.
/// </param>
public sealed record RosterSnapshotOutcome(
    DateOnly SnapshotDate, int MemberCount, IReadOnlyDictionary<string, int> RoleCounts, RosterSnapshotResult Stored,
    bool EmptyNotStored = false);

/// <summary>
/// Takes a dated snapshot of a vendor's member list and stores it. The roster is current state, not a date range,
/// so this is separate from extraction: it is run on its own (and can be scheduled), and each run records who was on
/// the list that day.
/// </summary>
public sealed class RosterSnapshotService
{
    private readonly IEnumerable<IVendorRosterSource> _sources;
    private readonly IMemberRosterRepository _repository;
    private readonly TimeProvider _timeProvider;

    public RosterSnapshotService(
        IEnumerable<IVendorRosterSource> sources, IMemberRosterRepository repository, TimeProvider? timeProvider = null)
    {
        _sources = sources;
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <exception cref="InvalidOperationException">
    /// The vendor has no roster source, the source could not read the member list (a rejected key, a rate limit or
    /// server error, a timeout or open circuit, a response that is not valid JSON), or it returned no members. The
    /// original exception is kept as the inner exception. Cancellation requested by the caller is not wrapped.
    /// </exception>
    public async Task<RosterSnapshotOutcome> TakeAsync(
        string tenantId, Guid vendorId, bool dryRun, CancellationToken cancellationToken = default)
    {
        var source = _sources.FirstOrDefault(s => s.VendorId == vendorId)
            ?? throw new InvalidOperationException(
                $"{VendorCatalog.FindById(vendorId)?.DisplayName ?? vendorId.ToString()} has no member roster source, "
                + "so there is nothing to snapshot.");

        IReadOnlyList<VendorMember> members;
        try
        {
            members = await source.FetchMembersAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Everything that can go wrong talking to the vendor (HTTP 429 or 5xx, a timeout, an open circuit
            // breaker, a response that does not parse) is an expected failure of this command, not a crash: it is
            // reported as one, with a non-zero exit, and nothing is stored. The type name and message say what
            // happened; neither carries a member (the sources never put one in an exception).
            throw new InvalidOperationException(
                $"Could not read the {VendorCatalog.FindById(vendorId)?.DisplayName ?? "vendor"} member list for tenant "
                + $"'{tenantId}': {ex.GetType().Name}: {ex.Message.TrimEnd('.')}. Nothing was stored.",
                ex);
        }

        // An organisation always has at least an owner, so an empty list is a vendor or scope problem, not a
        // roster. Storing it would replace a good snapshot taken earlier the same day with nothing, and read
        // as every member having left.
        if (members.Count == 0 && source.CanBeEmpty)
        {
            // A real answer for this vendor, not a failure and not a key problem. Reported, never stored.
            return new RosterSnapshotOutcome(
                DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime), 0, new Dictionary<string, int>(),
                new RosterSnapshotResult(0, 0, 0, 0), EmptyNotStored: true);
        }

        if (members.Count == 0)
        {
            throw new InvalidOperationException(
                $"{VendorCatalog.FindById(vendorId)?.DisplayName ?? "The vendor"} returned no members for tenant "
                + $"'{tenantId}'. An organisation has at least one, so this is not stored: check the key's scopes.");
        }

        var date = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var records = members.Select(m => new MemberRosterRecord
        {
            TenantId = tenantId,
            VendorId = vendorId,
            SnapshotDate = date,
            MemberId = m.Id,
            Email = m.Email,
            Name = m.Name,
            Role = m.Role,
            SeatType = m.SeatType,
            IsScimManaged = m.IsScimManaged,
            AddedAt = m.AddedAt,
        }).ToList();

        var stored = dryRun
            ? await _repository.PlanReplaceSnapshotAsync(tenantId, vendorId, date, records, cancellationToken).ConfigureAwait(false)
            : await _repository.ReplaceSnapshotAsync(tenantId, vendorId, date, records, cancellationToken).ConfigureAwait(false);

        var roles = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Role) ? "(none)" : r.Role!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        return new RosterSnapshotOutcome(date, records.Count, roles, stored);
    }
}
