using System.Text.Json;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>What a backfill read and what it stored (or, for a dry run, would store).</summary>
public sealed record UserUsageBackfillResult(int DaysRead, int RowsNormalized, UserUsageUpsertResult Upsert);

/// <summary>
/// Rebuilds per-user daily usage from raw extraction records already in the database, so history is
/// populated without calling the vendor's API again (which may no longer hold those days). It runs the
/// vendor's <see cref="IVendorUserUsageNormalizer"/> over the stored raw rows, exactly as an extraction
/// would, and upserts the result. Safe to repeat: the upsert is keyed on tenant, vendor, date and user.
/// <para>
/// Only as complete as the raw rows are. Raw storage keeps the latest pull per day, and a day stored
/// before a vendor's raw rows carried per-user detail simply has none to normalize.
/// </para>
/// </summary>
public sealed class UserUsageBackfill
{
    private readonly IRawExtractionRepository _rawExtractionRepository;
    private readonly IEnumerable<IVendorUserUsageNormalizer> _normalizers;
    private readonly IVendorRateConfigRepository _vendorRateConfigRepository;
    private readonly IUserDailyUsageRepository _userDailyUsageRepository;

    public UserUsageBackfill(
        IRawExtractionRepository rawExtractionRepository,
        IEnumerable<IVendorUserUsageNormalizer> normalizers,
        IVendorRateConfigRepository vendorRateConfigRepository,
        IUserDailyUsageRepository userDailyUsageRepository)
    {
        _rawExtractionRepository = rawExtractionRepository;
        _normalizers = normalizers;
        _vendorRateConfigRepository = vendorRateConfigRepository;
        _userDailyUsageRepository = userDailyUsageRepository;
    }

    /// <exception cref="InvalidOperationException">The vendor has no per-user normalizer.</exception>
    public async Task<UserUsageBackfillResult> RunAsync(
        string tenantId, Guid vendorId, DateRange period, bool dryRun, CancellationToken cancellationToken = default)
    {
        var normalizer = _normalizers.FirstOrDefault(n => n.VendorId == vendorId)
            ?? throw new InvalidOperationException(
                $"{VendorCatalog.FindById(vendorId)?.DisplayName ?? vendorId.ToString()} has no per-user usage normalizer, "
                + "so there is nothing to backfill.");

        var stored = await _rawExtractionRepository
            .GetRecordsJsonAsync(tenantId, vendorId, period, cancellationToken)
            .ConfigureAwait(false);

        var recordsByDate = stored.ToDictionary(
            kvp => kvp.Key,
            kvp => ParseRows(kvp.Value));

        var rawData = new RawVendorSpendData
        {
            VendorId = vendorId,
            TenantId = tenantId,
            Period = period,
            RecordsByDate = recordsByDate,
        };

        var rates = await _vendorRateConfigRepository
            .GetApplicableRatesAsync(tenantId, vendorId, period, cancellationToken)
            .ConfigureAwait(false);

        var rows = normalizer.Normalize(rawData, rates);

        var upsert = dryRun
            ? await _userDailyUsageRepository.PlanUpsertAsync(rows, cancellationToken).ConfigureAwait(false)
            : await _userDailyUsageRepository.UpsertAsync(rows, cancellationToken).ConfigureAwait(false);

        return new UserUsageBackfillResult(stored.Count, rows.Count, upsert);
    }

    /// <summary>
    /// Reads a day's stored raw rows (a JSON array of objects) back into the loose key/value shape the
    /// normalizers read. Numbers become <see cref="decimal"/> (what the extractors produced for amounts
    /// and credits; a count converts from it), strings, booleans and nulls map directly, and a nested
    /// array or object stays a <see cref="JsonElement"/>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ParseRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                row[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => property.Value.Clone(),
                };
            }

            rows.Add(row);
        }

        return rows;
    }
}
