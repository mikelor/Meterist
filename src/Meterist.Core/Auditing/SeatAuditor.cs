using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Auditing;

/// <summary>One member who holds a seat and has shown no activity in the window. Personal data: never logged.</summary>
public sealed class IdleSeat
{
    public required string MemberId { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? Role { get; init; }

    public string? SeatType { get; init; }

    /// <summary>The most recent day of activity before the window, if the stored per-user data has one. Null if none is stored.</summary>
    public DateOnly? LastActiveBeforeWindow { get; init; }
}

/// <summary>
/// The idle-seat half of the audit. When <see cref="Assessable"/> is false there is no honest answer, and
/// <see cref="Reason"/> says why (a roster with no usage stored would otherwise read as every seat being idle). When it is
/// true, a <see cref="Reason"/> is a caveat about the data, for example that stored usage starts partway through the window.
/// </summary>
public sealed record IdleSeatAssessment(
    DateRange Window,
    bool Assessable,
    string? Reason,
    int ActiveCount,
    int NewMemberCount,
    int ActiveNotOnRoster,
    IReadOnlyList<IdleSeat> IdleSeats);

/// <summary>
/// What one vendor reports and what Meterist is configured with, for the same day. <see cref="HasDrift"/> is the headline:
/// the seat count being billed (from the versioned rate configuration) disagrees with the vendor's own figure, so either
/// the rate card is stale or the vendor changed something. Drift is flagged, never corrected.
/// <para>
/// The rule depends on what the vendor's figure is. An <b>assigned-seat</b> count is what billing follows, so any
/// difference from the billed count is drift. A <b>roster</b> is only who has been provisioned, and the billed count may
/// be a commitment larger than that (billed 50, 29 members), so fewer members than billed seats is
/// <see cref="UnusedBilledSeats"/>, reported but not drift. More members than billed seats is drift either way: people
/// are using seats that are not being paid for.
/// </para>
/// </summary>
public sealed record VendorSeatAudit(
    VendorIdentity Vendor,
    DateOnly AsOf,
    int? BilledSeats,
    DateOnly? RosterDate,
    int? RosterCount,
    IReadOnlyDictionary<string, int> RosterBySeatType,
    int? AssignedSeats,
    DateOnly? AssignedDate,
    IdleSeatAssessment Idle,
    IReadOnlyList<string> Notes)
{
    /// <summary>The vendor's own count of seats: the assigned-seat figure where it reports one, otherwise the roster size.</summary>
    public int? VendorReportedSeats => AssignedSeats ?? RosterCount;

    public string? VendorReportedSource => AssignedSeats is not null ? "assigned seats" : RosterCount is not null ? "roster" : null;

    /// <summary>True when a billed seat count is configured, the vendor reports one, and they disagree by the rule above.</summary>
    public bool HasDrift =>
        BilledSeats is not null && VendorReportedSeats is not null
        && (AssignedSeats is not null ? BilledSeats != VendorReportedSeats : VendorReportedSeats > BilledSeats);

    /// <summary>
    /// Billed seats beyond the roster, when the comparison is against a roster: committed or not yet provisioned, so
    /// information and not drift. Null otherwise.
    /// </summary>
    public int? UnusedBilledSeats =>
        AssignedSeats is null && BilledSeats is not null && RosterCount is not null && BilledSeats > RosterCount
            ? BilledSeats - RosterCount
            : null;
}

public sealed record TenantSeatAudit(string TenantId, IReadOnlyList<VendorSeatAudit> Vendors)
{
    public bool HasDrift => Vendors.Any(v => v.HasDrift);
}

/// <summary>
/// Compares the billed seat count in the rate configuration with what each vendor reports (the assigned-seat figure and
/// the size of the latest roster snapshot), and lists rostered members who have shown no activity lately. Only vendors
/// that have a roster source are audited. A library service so a dashboard or scheduled run can use it too.
/// <para>
/// Activity is any stored per-user row for a person with spend, credits or requests above zero; requests count because on
/// a seat-based plan cost is zero by design and must not read as inactive. Members are matched to usage by the vendor's
/// member id (live-tested: the ChatGPT usage user id equals the roster id; Claude's actor id equals the member id) and then
/// by email. Members added inside the window are not called idle, since they have had no time to be active.
/// </para>
/// </summary>
public sealed class SeatAuditor
{
    // The seat rate every seat-priced adapter reads (see the per-vendor RateKeys classes).
    private const string SeatModelOrSku = "seat";

    // How far back an assigned-seat figure may be and still describe "now".
    private const int AssignedSeatLookbackDays = 14;

    // The roster is a snapshot: older than this and a comparison against it says little about today.
    private const int StaleRosterDays = 7;

    // How far back before the window to look, for the first stored per-user row (to say when stored usage starts) and for
    // a member's last activity before the window. The lookback is this much before the window starts, so a long window
    // is never cut short: it is always read in full.
    private const int HistoryBeforeWindowDays = 365;

    // The longest idle window accepted. Far beyond any useful audit, and keeps the date arithmetic well inside range.
    public const int MaxIdleDays = 3650;

    // Vendors publish the newest day late, so stored usage ending a day or two before the audit date is normal. Past this,
    // activity since the last stored day is unknown and an idle list would include people who simply have not been pulled yet.
    private const int AllowedUsageLagDays = 3;

    private readonly IEnumerable<IVendorRosterSource> _rosterSources;
    private readonly IVendorRateConfigRepository _rates;
    private readonly IMemberRosterRepository _rosters;
    private readonly IActivitySummaryRepository _activity;
    private readonly IUserDailyUsageRepository _usage;
    private readonly IUserActivityRepository? _userActivity;

    public SeatAuditor(
        IEnumerable<IVendorRosterSource> rosterSources,
        IVendorRateConfigRepository rates,
        IMemberRosterRepository rosters,
        IActivitySummaryRepository activity,
        IUserDailyUsageRepository usage,
        IUserActivityRepository? userActivity = null)
    {
        _userActivity = userActivity;
        _rosterSources = rosterSources;
        _rates = rates;
        _rosters = rosters;
        _activity = activity;
        _usage = usage;
    }

    /// <summary>Audits every vendor with a roster source, or only <paramref name="vendorFilter"/> if given.</summary>
    public async Task<TenantSeatAudit> AuditAsync(
        string tenantId,
        DateOnly asOf,
        int idleDays,
        Guid? vendorFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (idleDays < 1 || idleDays > MaxIdleDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleDays), $"The idle window must be between 1 and {MaxIdleDays} days.");
        }

        var vendors = new List<VendorSeatAudit>();
        var ids = _rosterSources.Select(s => s.VendorId).Distinct()
            .Where(id => vendorFilter is null || id == vendorFilter);

        foreach (var vendorId in ids)
        {
            var vendor = VendorCatalog.FindById(vendorId);
            if (vendor is null)
            {
                continue;
            }

            vendors.Add(await AuditVendorAsync(tenantId, vendor, asOf, idleDays, cancellationToken).ConfigureAwait(false));
        }

        return new TenantSeatAudit(tenantId, vendors);
    }

    private async Task<VendorSeatAudit> AuditVendorAsync(
        string tenantId, VendorIdentity vendor, DateOnly asOf, int idleDays, CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        // Billed seats: the seat rate in force on the day.
        var rates = await _rates.GetApplicableRatesAsync(tenantId, vendor.Id, new DateRange(asOf, asOf), cancellationToken)
            .ConfigureAwait(false);
        var seatRate = rates.FindRateForDay(SeatModelOrSku, asOf);
        int? billed = seatRate?.SeatCount;
        if (seatRate is null)
        {
            notes.Add("No seat rate is configured for this day, so there is no billed seat count to compare (see 'rates set').");
        }
        else if (billed is null)
        {
            notes.Add("The seat rate has no seat count, so there is no billed seat count to compare.");
        }

        // Roster: the latest snapshot on or before the day.
        var snapshot = await _rosters.GetLatestSnapshotAsync(tenantId, vendor.Id, asOf, cancellationToken).ConfigureAwait(false);
        var bySeatType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (snapshot is null)
        {
            notes.Add("No roster snapshot is stored, so the roster size and the idle seats are unknown (run 'roster snapshot').");
        }
        else
        {
            foreach (var group in snapshot.Members.GroupBy(m => string.IsNullOrWhiteSpace(m.SeatType) ? "(none)" : m.SeatType!, StringComparer.Ordinal))
            {
                bySeatType[group.Key] = group.Count();
            }

            var age = asOf.DayNumber - snapshot.Date.DayNumber;
            if (age > StaleRosterDays)
            {
                notes.Add($"The latest roster snapshot is {age} days old ({snapshot.Date:yyyy-MM-dd}); take a fresh one before trusting a comparison.");
            }
        }

        // The vendor's own assigned-seat figure, where it reports one (Claude Enterprise's activity summary).
        var summaries = await _activity.GetAsync(
            tenantId, vendor.Id, new DateRange(asOf.AddDays(-AssignedSeatLookbackDays), asOf), cancellationToken).ConfigureAwait(false);
        var assigned = summaries
            .Where(s => s.Product == ActivitySummaryRecord.AllProducts && s.AssignedSeatCount is not null)
            .OrderByDescending(s => s.Date)
            .FirstOrDefault();

        var idle = snapshot is null
            ? new IdleSeatAssessment(
                new DateRange(asOf.AddDays(1 - idleDays), asOf), false, "No roster snapshot is stored.", 0, 0, 0, [])
            : await AssessIdleAsync(tenantId, vendor, snapshot, asOf, idleDays, cancellationToken).ConfigureAwait(false);

        // Only people who hold a seat count toward the roster size: a licence list that also names people with no licence
        // (Gemini) would otherwise read as seats in use.
        var seatHolders = snapshot?.Members.Count(m => RosterSeatTypes.HoldsSeat(m.SeatType));

        var refused = snapshot?.Members.Count(m => RosterSeatTypes.TriedToSignInWithoutLicense(m.SeatType)) ?? 0;
        if (refused > 0)
        {
            notes.Add($"{refused} user(s) tried to sign in without a licence and were refused (not counted as seats): demand the licences do not cover.");
        }

        if (snapshot is not null && assigned is not null && seatHolders != assigned.AssignedSeatCount)
        {
            notes.Add(
                $"The roster has {seatHolders} member(s) but the vendor reports {assigned.AssignedSeatCount} assigned seat(s); "
                + "they are not the same measure (a member may hold no seat, or a seat may be unassigned), so only the billed count is compared with the vendor's own figure.");
        }

        return new VendorSeatAudit(
            vendor, asOf, billed, snapshot?.Date, seatHolders, bySeatType,
            assigned?.AssignedSeatCount, assigned?.Date, idle, notes);
    }

    private async Task<IdleSeatAssessment> AssessIdleAsync(
        string tenantId, VendorIdentity vendor, RosterSnapshot snapshot, DateOnly asOf, int idleDays, CancellationToken cancellationToken)
    {
        var window = new DateRange(asOf.AddDays(1 - idleDays), asOf);

        var history = await _usage.GetAsync(
            tenantId, vendor.Id, new DateRange(window.Start.AddDays(-HistoryBeforeWindowDays), asOf), cancellationToken).ConfigureAwait(false);
        var people = history
            .Where(r => r.Attribution == UsageAttribution.Person && IsActive(r))
            .Select(r => new Evidence(r.Date, r.UserId, r.UserEmail))
            .ToList();

        // Vendor-reported activity (Claude Enterprise): a row exists only for a person who did something that day, so any row
        // counts, including a person who was busy and cost nothing on a seat plan.
        IReadOnlyList<UserActivityRecord> activityRows = _userActivity is null
            ? []
            : await _userActivity.GetAsync(
                tenantId, vendor.Id, new DateRange(window.Start.AddDays(-HistoryBeforeWindowDays), asOf), cancellationToken).ConfigureAwait(false);
        people.AddRange(activityRows.Select(r => new Evidence(r.Date, r.UserId, r.UserEmail)));

        var inWindow = people.Where(r => r.Date >= window.Start && r.Date <= window.End).ToList();

        // With no activity at all stored for the window, "everyone is idle" is more likely missing data than a finding.
        if (inWindow.Count == 0)
        {
            return new IdleSeatAssessment(
                window, false,
                people.Count == 0
                    ? "No per-user usage is stored for this vendor, so idle seats cannot be told (run 'extract', or 'backfill' for older raw data)."
                    : $"No per-user activity is stored in the last {idleDays} day(s), so idle seats cannot be told (the stored usage ends {people.Max(r => r.Date):yyyy-MM-dd}; run 'extract').",
                0, 0, 0, []);
        }

        var firstStored = people.Min(r => r.Date);

        var byId = snapshot.Members.ToDictionary(m => m.MemberId, StringComparer.Ordinal);
        var byEmail = snapshot.Members
            .Where(m => !string.IsNullOrWhiteSpace(m.Email))
            .GroupBy(m => m.Email!.Trim().ToUpperInvariant())
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        MemberRosterRecord? Resolve(Evidence row)
        {
            if (byId.TryGetValue(row.UserId, out var member))
            {
                return member;
            }

            return !string.IsNullOrWhiteSpace(row.UserEmail) && byEmail.TryGetValue(row.UserEmail.Trim().ToUpperInvariant(), out member)
                ? member
                : null;
        }

        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var lastBefore = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var notOnRoster = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in people)
        {
            var member = Resolve(row);
            if (member is null)
            {
                if (row.Date >= window.Start)
                {
                    notOnRoster.Add(row.UserId);
                }

                continue;
            }

            // On the roster but holding no seat (a Gemini user with no licence): their activity is neither a seat in use nor an
            // active person to count against the seats, or one licensed user plus one former unlicensed one reads as 200%.
            if (!RosterSeatTypes.HoldsSeat(member.SeatType))
            {
                continue;
            }

            if (row.Date >= window.Start)
            {
                activeIds.Add(member.MemberId);
            }
            else if (!lastBefore.TryGetValue(member.MemberId, out var seen) || row.Date > seen)
            {
                lastBefore[member.MemberId] = row.Date;
            }
        }

        var idle = new List<IdleSeat>();
        var newMembers = 0;
        foreach (var member in snapshot.Members.Where(m => RosterSeatTypes.HoldsSeat(m.SeatType)))
        {
            if (activeIds.Contains(member.MemberId))
            {
                continue;
            }

            // Joined inside the window: no time to have been active yet.
            if (member.AddedAt is { } added && DateOnly.FromDateTime(added.UtcDateTime) >= window.Start)
            {
                newMembers++;
                continue;
            }

            idle.Add(new IdleSeat
            {
                MemberId = member.MemberId,
                Name = member.Name,
                Email = member.Email,
                Role = member.Role,
                SeatType = member.SeatType,
                LastActiveBeforeWindow = lastBefore.TryGetValue(member.MemberId, out var last) ? last : null,
            });
        }

        var ordered = idle
            .OrderBy(i => i.LastActiveBeforeWindow is null ? 0 : 1)   // never seen active first
            .ThenBy(i => i.LastActiveBeforeWindow)
            .ThenBy(i => i.Name ?? i.Email ?? i.MemberId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var caveats = new List<string>();
        if (firstStored > window.Start.AddDays(1))
        {
            // Stored usage begins after the window does, so a member could have been active in the part that is missing.
            caveats.Add($"Stored per-user activity starts on {firstStored:yyyy-MM-dd}, after the window began ({window.Start:yyyy-MM-dd}); a seat may have been active earlier in the window.");
        }

        // And the other end: usage that stops well before the audit date says nothing about the days since, so anyone active
        // only in that stretch looks idle. Any stored row counts (a day with rows was pulled), not only active ones.
        var lastStored = history.Select(r => r.Date).Concat(activityRows.Select(r => r.Date)).Max();
        if (asOf.DayNumber - lastStored.DayNumber > AllowedUsageLagDays)
        {
            caveats.Add(
                $"Stored per-user activity ends on {lastStored:yyyy-MM-dd}, {asOf.DayNumber - lastStored.DayNumber} days before the audit date; "
                + "a seat that was active only since then is listed as idle (run 'extract' to bring the data up to date).");
        }

        return new IdleSeatAssessment(
            window, true, caveats.Count == 0 ? null : string.Join(" ", caveats),
            activeIds.Count, newMembers, notOnRoster.Count, ordered);
    }

    // One dated sign of life for one person, from either spend rows or vendor-reported activity rows.
    private sealed record Evidence(DateOnly Date, string UserId, string? UserEmail);

    // Spend, credits or requests above zero. Requests count because cost is zero by design on a seat-based plan.
    private static bool IsActive(UserDailyUsageRecord r) =>
        r.Amount > 0m || r.Credits is > 0m || r.Requests is > 0;
}
