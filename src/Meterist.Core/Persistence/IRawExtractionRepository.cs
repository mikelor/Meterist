namespace Meterist.Core.Persistence;

/// <summary>
/// Raw vendor data survives independently of normalization, so a
/// normalization bug can be fixed and replayed against the original pull
/// rather than needing to re-hit a vendor API that may no longer have the
/// historical window (e.g. Claude Enterprise Analytics API only goes back to
/// Jan 1, 2026; ChatGPT Enterprise ~120 days).
///
/// Scope note: this keeps only the *latest* raw pull per (tenant, vendor,
/// date) — an upsert, same as the canonical layer — not a full audit log of
/// every historical extraction attempt. A true append-only history is a
/// larger, separate feature (unbounded growth, different retention story)
/// and is deliberately not built here.
/// </summary>
public interface IRawExtractionRepository
{
    Task UpsertAsync(
        string tenantId,
        Guid vendorId,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
        DateTime extractedAtUtc,
        CancellationToken cancellationToken = default);
}
