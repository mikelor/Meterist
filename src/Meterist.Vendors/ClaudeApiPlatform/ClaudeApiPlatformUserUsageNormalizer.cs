using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Turns the raw (actor, key, workspace, product, day) cost rows into one <see cref="UserDailyUsageRecord"/> per actor per day,
/// summing dollars and requests across products and keys. Dollars are vendor-reported, so rows are not estimated.
/// <para>
/// Attribution comes from the actor type. <c>user_actor</c> is a <see cref="UsageAttribution.Person"/> (a personal key or
/// Console sign-in); <c>service_account_actor</c> and <c>api_actor</c> are <see cref="UsageAttribution.ApiKey"/>, and a legacy
/// workspace key is reported at key level that way; anything else, including a federated cloud identity, is
/// <see cref="UsageAttribution.Unrecognized"/>. Cost with no actor is kept in one synthetic bucket
/// (<see cref="UnattributedUserId"/>) so the per-user figures still add up to the daily total; it is never a person.
/// </para>
/// <para>
/// Rows from the legacy <c>cost_report</c> have no actor at all (the <c>ActorType</c> key is absent) and produce no per-user
/// rows: they were never per-person data. Warnings name actor types and counts only, never a person.
/// </para>
/// </summary>
public sealed class ClaudeApiPlatformUserUsageNormalizer : IVendorUserUsageNormalizer
{
    /// <summary>The stand-in user id for cost the API could not attribute to any actor.</summary>
    public const string UnattributedUserId = "unattributed";

    private readonly ILogger<ClaudeApiPlatformUserUsageNormalizer> _logger;

    public ClaudeApiPlatformUserUsageNormalizer(ILogger<ClaudeApiPlatformUserUsageNormalizer> logger)
    {
        _logger = logger;
    }

    public Guid VendorId => VendorCatalog.ClaudeApiPlatform.Id;

    public IReadOnlyList<UserDailyUsageRecord> Normalize(
        RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates)
    {
        _ = applicableRates;

        var records = new List<UserDailyUsageRecord>();
        var unrecognizedTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var (date, rows) in rawData.RecordsByDate.OrderBy(kvp => kvp.Key))
        {
            // Legacy rows carry no ActorType key and are not per-actor data.
            var actorRows = rows
                .Where(r => r.ContainsKey(ClaudeApiCostRowFields.ActorType) && r.ContainsKey(ClaudeApiCostRowFields.AmountUsd))
                .GroupBy(UserIdOf, StringComparer.Ordinal);

            foreach (var group in actorRows)
            {
                var first = group.First();
                var attribution = AttributionOf(first, group.Key);

                if (attribution == UsageAttribution.Unrecognized)
                {
                    var label = group.Key == UnattributedUserId ? "(no actor)" : TypeLabel(first);
                    unrecognizedTypes[label] = unrecognizedTypes.GetValueOrDefault(label) + 1;
                }

                var requests = group
                    .Select(r => r.TryGetValue(ClaudeApiCostRowFields.Requests, out var v) ? ToNullableLong(v) : null)
                    .Where(v => v is not null)
                    .ToList();

                records.Add(new UserDailyUsageRecord
                {
                    TenantId = rawData.TenantId,
                    VendorId = rawData.VendorId,
                    Date = date,
                    UserId = group.Key,
                    UserEmail = FirstText(group, ClaudeApiCostRowFields.UserEmail),
                    UserName = FirstText(group, ClaudeApiCostRowFields.UserName),
                    Amount = group.Sum(r => ToDecimal(r[ClaudeApiCostRowFields.AmountUsd])),
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
                "Claude API Platform tenant '{TenantId}': {Count} per-actor day(s) have an actor type other than "
                + "user_actor, service_account_actor or api_actor and were stored as unrecognized, not as people: {Types}.",
                rawData.TenantId, unrecognizedTypes.Values.Sum(),
                string.Join(", ", unrecognizedTypes.Select(kv => $"{kv.Key} x{kv.Value}")));
        }

        return records;
    }

    private static string? FirstText(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string key) =>
        rows.Select(r => r.TryGetValue(key, out var v) ? v as string : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string UserIdOf(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ClaudeApiCostRowFields.UserId, out var id) && id is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : UnattributedUserId;

    private static UsageAttribution AttributionOf(IReadOnlyDictionary<string, object?> row, string userId)
    {
        if (userId == UnattributedUserId)
        {
            return UsageAttribution.Unrecognized;
        }

        row.TryGetValue(ClaudeApiCostRowFields.ActorType, out var type);
        return (type as string)?.ToLowerInvariant() switch
        {
            "user_actor" => UsageAttribution.Person,
            "service_account_actor" or "api_actor" => UsageAttribution.ApiKey,
            _ => UsageAttribution.Unrecognized,
        };
    }

    private static string TypeLabel(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(ClaudeApiCostRowFields.ActorType, out var type) && type is string s && !string.IsNullOrWhiteSpace(s)
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
