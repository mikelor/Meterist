namespace Meterist.Data;

/// <summary>
/// Persisted raw vendor data, one row per (TenantId, VendorId, Date). See
/// Meterist.Core.Persistence.IRawExtractionRepository for the scope note on
/// why this is latest-pull-per-day, not a full audit history.
/// </summary>
public sealed class RawDailyExtractionRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    public required DateTime ExtractedAtUtc { get; init; }

    public required string RecordsJson { get; init; }
}
