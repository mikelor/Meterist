using System.Text.RegularExpressions;
using Meterist.Core.Models;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise's daily active-user and seat counts from the Analytics API's summaries endpoint, read with the
/// same Analytics key the tenant's spend extraction uses. A missing credential is a configuration problem, so it
/// fails loudly rather than reading as no activity.
/// <para>
/// Each API day becomes one <see cref="ActivitySummaryRecord.AllProducts"/> row (daily, weekly and monthly active
/// users, assigned seats, pending invites, adoption rates) plus one row per product whose fields follow the
/// <c>{product}_{daily|weekly|monthly}_active_user_count</c> pattern. The products are found by that pattern rather
/// than listed, so one the API adds later is stored without a code change. A product the API reports as null for a
/// day (a product the organisation does not have) gets no row.
/// </para>
/// </summary>
public sealed partial class ClaudeEnterpriseActivitySummarySource : IVendorActivitySummarySource
{
    private readonly ISecretStore _secretStore;
    private readonly IClaudeActivitySummaryRepository _repository;

    public ClaudeEnterpriseActivitySummarySource(ISecretStore secretStore, IClaudeActivitySummaryRepository repository)
    {
        _secretStore = secretStore;
        _repository = repository;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public async Task<IReadOnlyList<ActivitySummaryRecord>> FetchAsync(
        string tenantId, DateRange period, CancellationToken cancellationToken = default)
    {
        var apiKey = await _secretStore.GetCredentialAsync(tenantId, VendorId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No Claude Enterprise credential configured for tenant '{tenantId}'.");
        }

        var days = await _repository.QueryAsync(apiKey, period, cancellationToken).ConfigureAwait(false);

        // The span may start earlier than the period (windows are not trimmed to it), so keep only what was asked for.
        return Map(tenantId, VendorId, days.Where(d => d.Date >= period.Start && d.Date <= period.End));
    }

    /// <summary>Turns API days into stored rows. Public so the mapping can be tested without HTTP.</summary>
    public static IReadOnlyList<ActivitySummaryRecord> Map(string tenantId, Guid vendorId, IEnumerable<ClaudeActivityDay> days)
    {
        var rows = new List<ActivitySummaryRecord>();

        foreach (var day in days)
        {
            var values = day.Values;

            rows.Add(new ActivitySummaryRecord
            {
                TenantId = tenantId,
                VendorId = vendorId,
                Date = day.Date,
                Product = ActivitySummaryRecord.AllProducts,
                DailyActiveUsers = Count(values, "daily_active_user_count"),
                WeeklyActiveUsers = Count(values, "weekly_active_user_count"),
                MonthlyActiveUsers = Count(values, "monthly_active_user_count"),
                AssignedSeatCount = Count(values, "assigned_seat_count"),
                PendingInviteCount = Count(values, "pending_invite_count"),
                DailyAdoptionRate = Get(values, "daily_adoption_rate"),
                WeeklyAdoptionRate = Get(values, "weekly_adoption_rate"),
                MonthlyAdoptionRate = Get(values, "monthly_adoption_rate"),
            });

            var products = values.Keys
                .Select(k => ProductField().Match(k))
                .Where(m => m.Success)
                .Select(m => m.Groups["product"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            foreach (var product in products)
            {
                var daily = Count(values, $"{product}_daily_active_user_count");
                var weekly = Count(values, $"{product}_weekly_active_user_count");
                var monthly = Count(values, $"{product}_monthly_active_user_count");

                if (daily is null && weekly is null && monthly is null)
                {
                    continue;
                }

                rows.Add(new ActivitySummaryRecord
                {
                    TenantId = tenantId,
                    VendorId = vendorId,
                    Date = day.Date,
                    Product = product,
                    DailyActiveUsers = daily,
                    WeeklyActiveUsers = weekly,
                    MonthlyActiveUsers = monthly,
                });
            }
        }

        return rows;
    }

    private static decimal? Get(IReadOnlyDictionary<string, decimal?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private static int? Count(IReadOnlyDictionary<string, decimal?> values, string name) =>
        Get(values, name) is { } value ? (int)value : null;

    // {product}_{daily|weekly|monthly}_active_user_count with a non-empty product prefix. The unprefixed organisation
    // fields do not match (no prefix), and neither does science_entitled_user_count.
    [GeneratedRegex(@"^(?<product>.+)_(daily|weekly|monthly)_active_user_count$")]
    private static partial Regex ProductField();
}
