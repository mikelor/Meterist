using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Turns the raw (actor, product, day) cost rows into one <see cref="UserDailyUsageRecord"/> per
/// actor per day: dollars and requests are summed across products. Dollars are vendor-reported
/// (the API's own per-actor cost after discounts and before credits), so rows are not estimated.
/// <para>
/// On a seat-based plan the cost is usage credits only and is near zero by design, while
/// <c>Requests</c> counts seat-covered activity too (live test, docs/vendor-integration-reference.md).
/// A row with a zero amount and a request count is a real, active user, so zero-cost rows are kept.
/// </para>
/// <para>
/// Attribution comes from the actor type. <c>user_actor</c> is a person; <c>service_account_actor</c>
/// and <c>api_actor</c> are <see cref="UsageAttribution.ApiKey"/>; anything else, including a
/// <c>federated_actor</c> (a cloud identity, not known to be a person), is
/// <see cref="UsageAttribution.Unrecognized"/> and named in a warning by type. A row with no actor
/// is usage the vendor could not attribute; it is kept in a single synthetic bucket with user id
/// <see cref="UnattributedUserId"/> so the per-user figures still add up to the daily total, and is
/// never a person.
/// </para>
/// <para>
/// Rows stored before the actor type was kept (the legacy <c>user_cost_report</c>, a per-user report)
/// have no <c>ActorType</c> key at all; those are treated as people when they carry a user id. A row
/// that has the key but no value (from the newer endpoint) is unrecognized, not assumed to be a person.
/// </para>
/// Warnings name actor types and counts only, never a person.
/// </summary>
public sealed class ClaudeEnterpriseUserUsageNormalizer : IVendorUserUsageNormalizer
{
    /// <summary>The stand-in user id for cost the API could not attribute to any actor.</summary>
    public const string UnattributedUserId = "unattributed";

    private readonly ILogger<ClaudeEnterpriseUserUsageNormalizer> _logger;

    public ClaudeEnterpriseUserUsageNormalizer(ILogger<ClaudeEnterpriseUserUsageNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public IReadOnlyList<UserDailyUsageRecord> Normalize(
        RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates)
    {
        // Not used: the API reports dollars directly, so no rate is needed for per-user cost.
        _ = applicableRates;

        var records = new List<UserDailyUsageRecord>();
        var unrecognizedTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            var actorRows = rows
                .Where(r => r.ContainsKey(ClaudeAnalyticsCostRowFields.AmountUsd))
                .GroupBy(r => UserIdOf(r), StringComparer.Ordinal);

            foreach (var group in actorRows)
            {
                var first = group.First();
                var attribution = AttributionOf(first, group.Key);

                if (attribution == UsageAttribution.Unrecognized)
                {
                    // The type name is a vendor label, not personal data, so it is safe to log.
                    var label = group.Key == UnattributedUserId
                        ? "(no actor)"
                        : TypeLabel(first);
                    unrecognizedTypes[label] = unrecognizedTypes.GetValueOrDefault(label) + 1;
                }

                var requests = group
                    .Select(r => r.TryGetValue(ClaudeAnalyticsCostRowFields.Requests, out var v) ? ToNullableLong(v) : null)
                    .Where(v => v is not null)
                    .ToList();

                records.Add(new UserDailyUsageRecord
                {
                    TenantId = rawData.TenantId,
                    VendorId = rawData.VendorId,
                    Date = date,
                    UserId = group.Key,
                    UserEmail = FirstText(group, ClaudeAnalyticsCostRowFields.UserEmail),
                    UserName = FirstText(group, ClaudeAnalyticsCostRowFields.UserName),
                    Amount = group.Sum(r => ToDecimal(r[ClaudeAnalyticsCostRowFields.AmountUsd])),
                    Credits = null,
                    Requests = requests.Count == 0 ? null : requests.Sum(v => v!.Value),
                    IsEstimated = false,
                    Attribution = attribution,
                });
            }
        }

        if (unrecognizedTypes.Count > 0)
        {
            _logger.LogWarning(
                "Claude Enterprise tenant '{TenantId}': {Count} per-actor day(s) have an actor type other than "
                + "user_actor, service_account_actor or api_actor and were stored as unrecognized, not as people: "
                + "{Types}. They are left out of person-level reporting until the type is classified.",
                rawData.TenantId, unrecognizedTypes.Values.Sum(),
                string.Join(", ", unrecognizedTypes.Select(kv => $"{kv.Key} x{kv.Value}")));
        }

        _logger.LogDebug(
            "Normalized {RowCount} per-user Claude Enterprise row(s) for tenant '{TenantId}'.",
            records.Count, rawData.TenantId);

        return records;
    }

    private static string? FirstText(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string key) =>
        rows.Select(r => r.TryGetValue(key, out var v) ? v as string : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string UserIdOf(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ClaudeAnalyticsCostRowFields.UserId, out var id) && id is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : UnattributedUserId;

    private static UsageAttribution AttributionOf(IReadOnlyDictionary<string, object?> row, string userId)
    {
        if (userId == UnattributedUserId)
        {
            return UsageAttribution.Unrecognized;
        }

        // No ActorType key at all: stored before the type was kept, i.e. from the legacy per-user report.
        if (!row.TryGetValue(ClaudeAnalyticsCostRowFields.ActorType, out var type))
        {
            return UsageAttribution.Person;
        }

        return (type as string)?.ToLowerInvariant() switch
        {
            "user_actor" => UsageAttribution.Person,
            "service_account_actor" or "api_actor" => UsageAttribution.ApiKey,
            _ => UsageAttribution.Unrecognized,
        };
    }

    private static string TypeLabel(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ClaudeAnalyticsCostRowFields.ActorType, out var type) && type is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : "(none)";

    private static long? ToNullableLong(object? value) => value switch
    {
        null => null,
        long l => l,
        decimal d => (long)d,
        _ => Convert.ToInt64(value),
    };

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };
}
