namespace Meterist.Core.Vendors;

/// <summary>
/// A vendor's stable identity. <see cref="Id"/> is the real key used
/// everywhere (persisted records, credential lookups, rate config) so a
/// rebrand or merger can't orphan historical data. <see cref="DisplayName"/>
/// can change freely since nothing is keyed on it. <see cref="ShortName"/>
/// is the lowercase, hyphenated, CLI-safe token (e.g. "gemini-enterprise")
/// used for command-line arguments.
/// <para>
/// <see cref="ExpectsRecordEveryDay"/> is true for seat-priced vendors, where a
/// day accrues a seat or subscription fee whether or not anyone used the
/// product, so every real day has a stored record and a missing day is a gap in
/// our data. It is false for usage-priced vendors, which have no record for a
/// day with no usage, so a missing day there is normal.
/// </para>
/// </summary>
public sealed record VendorIdentity(
    Guid Id, string ShortName, string DisplayName, bool ExpectsRecordEveryDay = false);
