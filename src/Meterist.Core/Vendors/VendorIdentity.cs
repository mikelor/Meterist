namespace Meterist.Core.Vendors;

/// <summary>
/// A vendor's stable identity. <see cref="Id"/> is the real key used
/// everywhere (persisted records, credential lookups, rate config) so a
/// rebrand or merger can't orphan historical data. <see cref="DisplayName"/>
/// can change freely since nothing is keyed on it. <see cref="ShortName"/>
/// is the lowercase, hyphenated, CLI-safe token (e.g. "gemini-enterprise")
/// used for command-line arguments.
/// </summary>
public sealed record VendorIdentity(Guid Id, string ShortName, string DisplayName);
