namespace Meterist.Core.Vendors;

/// <summary>
/// Vendor-native records prior to normalization, structurally grouped by
/// calendar day — daily is the raw-storage grain by construction here, not a
/// convention each vendor extractor has to remember to follow. Deliberately
/// shaped as loose key/value records rather than a vendor-specific type,
/// since the four v1 vendors return wildly different native shapes
/// (Analytics API JSON, Admin API JSON, unified Cost API JSON, BigQuery
/// rows) — <see cref="IVendorSpendNormalizer"/> implementations interpret
/// the keys they know about.
/// </summary>
public sealed class RawVendorSpendData
{
    public required Guid VendorId { get; init; }

    public required string TenantId { get; init; }

    public required DateRange Period { get; init; }

    public required IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> RecordsByDate { get; init; }
}
