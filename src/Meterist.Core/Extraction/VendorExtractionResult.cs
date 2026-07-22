namespace Meterist.Core.Extraction;

public enum VendorExtractionStatus
{
    Succeeded,
    NotImplemented,
    Failed,
}

/// <summary>
/// Per-vendor outcome of one SpendExtractionService run — distinguishes
/// "not built yet" from "actually broke" from "worked," per
/// docs/architecture.md §10's requirement that extraction status be visible
/// to the operator, not swallowed.
/// </summary>
public sealed record VendorExtractionResult(
    Guid VendorId,
    string DisplayName,
    VendorExtractionStatus Status,
    int RecordCount,
    string? Detail);
