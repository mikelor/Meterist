namespace Meterist.Core.Models;

/// <summary>
/// How a per-user row was attributed to its <see cref="UserDailyUsageRecord.UserId"/>.
/// </summary>
public enum UsageAttribution
{
    /// <summary>The vendor reported the usage against a named person.</summary>
    Person,

    /// <summary>
    /// The vendor reported it against an API key or service account, not a person
    /// (Claude API Platform's legacy workspace keys, for example). The identifier is the key's.
    /// </summary>
    ApiKey,

    /// <summary>Meterist divided an aggregate figure across users; the vendor reported no per-user value.</summary>
    Allocated,
}

/// <summary>
/// One user's usage for one day on one vendor, the per-user companion of
/// <see cref="DailySpendRecord"/>. Natural key is (TenantId, VendorId, Date, UserId): storing a
/// day already stored is an upsert against that key, the same overlap handling the daily totals use.
/// <para>
/// A row exists only for a user-day with usage (the vendors return nothing for idle users), so the
/// absence of a row means no usage, and a user count is a count of distinct <see cref="UserId"/>s.
/// </para>
/// <para>
/// Users are named in reports by operator decision, so this holds name and email. That makes it
/// personal data: keep these values out of logs at Information level and above and out of anything
/// committed to the repository. This is deliberately a class with the default <c>ToString</c>, not a
/// record, so logging an instance by accident prints a type name instead of a person.
/// </para>
/// </summary>
public sealed class UserDailyUsageRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>
    /// The vendor's stable identifier for the user (or key). Part of the natural key, so it must
    /// not be an email address that can be renamed; <see cref="UserEmail"/> carries that.
    /// </summary>
    public required string UserId { get; init; }

    public string? UserEmail { get; init; }

    public string? UserName { get; init; }

    /// <summary>
    /// USD cost of the day's usage. Vendor-reported where the vendor gives a dollar figure,
    /// otherwise derived (then <see cref="IsEstimated"/> is true).
    /// </summary>
    public decimal Amount { get; init; }

    /// <summary>Credits consumed, for vendors that bill in credits. Null where the vendor has no such concept.</summary>
    public decimal? Credits { get; init; }

    /// <summary>
    /// Requests or messages, whichever the vendor reports (ChatGPT messages, Claude requests,
    /// Gemini queries). Null where the vendor does not report a count.
    /// </summary>
    public long? Requests { get; init; }

    /// <summary>
    /// True when <see cref="Amount"/> is Meterist's or the vendor's estimate rather than a billed
    /// figure. Reports must label such figures; they are not presented with the confidence of
    /// vendor-reported dollars.
    /// </summary>
    public bool IsEstimated { get; init; }

    public UsageAttribution Attribution { get; init; } = UsageAttribution.Person;
}
