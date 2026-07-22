using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Real IGeminiBillingQueryRepository, querying the tenant's Cloud Billing
/// BigQuery export.
///
/// Filter corrected 2026-07-22 against a live test account: Google bills the
/// Gemini Enterprise seat/subscription line and its per-request overage under
/// <c>service.description = "Vertex AI Search"</c> — NOT a service literally
/// named "Gemini Enterprise" (that string only appears in the SKU
/// description, e.g. "Gemini Enterprise Plus: Subscription - one month
/// term"). Plain "Vertex AI" (no "Search") carries raw Gemini model token
/// usage instead — a different, pay-as-you-go product/billing surface, out
/// of scope for this adapter. Within "Vertex AI Search," the SKU filter
/// (LIKE '%Enterprise%') deliberately excludes sibling SKUs like "Vertex AI
/// Search and Conversation: Data Index," which isn't specific to the
/// Enterprise plan.
///
/// Aggregates to (day, SKU) grain at the SQL level (GROUP BY DATE(...), sku)
/// rather than returning raw hourly rows for the extractor to bucket later —
/// this is what makes "daily is the raw grain" a guarantee of the query
/// itself, not a convention, and it moves less data over the wire.
/// </summary>
public sealed class BigQueryGeminiBillingRepository : IGeminiBillingQueryRepository
{
    public const string ServiceName = "Vertex AI Search";
    public const string SkuDescriptionFilter = "%Enterprise%";

    private readonly ILogger<BigQueryGeminiBillingRepository> _logger;

    public BigQueryGeminiBillingRepository(ILogger<BigQueryGeminiBillingRepository> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryBillingRowsAsync(
        GeminiCredential credential,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        var tableRef = BuildQualifiedTableReference(credential);

        // GoogleCredential.FromJson(string) is obsolete (security risk) — go through
        // CredentialFactory for a concrete service-account credential instead.
        var googleCredential = CredentialFactory
            .FromJson<ServiceAccountCredential>(credential.ServiceAccountJson)
            .ToGoogleCredential();

        var client = await BigQueryClient.CreateAsync(credential.BillingProjectId, googleCredential)
            .ConfigureAwait(false);

        var sql = BuildSql(tableRef);

        var parameters = new[]
        {
            new BigQueryParameter("serviceName", BigQueryDbType.String, ServiceName),
            new BigQueryParameter("skuFilter", BigQueryDbType.String, SkuDescriptionFilter),
            new BigQueryParameter("periodStart", BigQueryDbType.Date, period.Start.ToDateTime(TimeOnly.MinValue)),
            new BigQueryParameter("periodEnd", BigQueryDbType.Date, period.End.ToDateTime(TimeOnly.MinValue)),
        };

        // Deliberately not logging credential.ServiceAccountJson — everything else
        // here is safe to log and is exactly what you'd want when a query comes
        // back empty and you need to know precisely what was asked for.
        _logger.LogDebug(
            "Querying Gemini Enterprise billing export {Table} for {PeriodStart} to {PeriodEnd} "
            + "with service '{ServiceName}' and SKU filter '{SkuFilter}'. SQL: {Sql}",
            tableRef, period.Start, period.End, ServiceName, SkuDescriptionFilter, sql);

        var results = await client.ExecuteQueryAsync(sql, parameters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in results)
        {
            rows.Add(new Dictionary<string, object?>
            {
                [GeminiBillingRowFields.RecordDate] = ToDateOnly(row["record_date"]),
                [GeminiBillingRowFields.SkuDescription] = row["sku_description"]?.ToString(),
                [GeminiBillingRowFields.Cost] = ToDecimal(row["cost"]),
                [GeminiBillingRowFields.CreditsAmount] = ToDecimal(row["credits_amount"]),
            });
        }

        _logger.LogDebug(
            "Gemini Enterprise billing export {Table} returned {RowCount} (day, SKU) row(s) for {PeriodStart} to {PeriodEnd}.",
            tableRef, rows.Count, period.Start, period.End);

        return rows;
    }

    // Extracted purely so the WHERE-clause shape is unit-testable without a live
    // BigQuery connection — see BigQueryGeminiBillingRepositoryTests, added
    // specifically to guard against silently reverting the 2026-07-22 fix above.
    public static string BuildSql(string tableRef) => $"""
        SELECT
          DATE(usage_start_time) AS record_date,
          sku.description AS sku_description,
          SUM(cost) AS cost,
          IFNULL(SUM((SELECT SUM(c.amount) FROM UNNEST(credits) AS c)), 0) AS credits_amount
        FROM {tableRef}
        WHERE service.description = @serviceName
          AND sku.description LIKE @skuFilter
          AND DATE(usage_start_time) BETWEEN @periodStart AND @periodEnd
        GROUP BY record_date, sku_description
        ORDER BY record_date
        """;

    private static DateOnly ToDateOnly(object? value) => value switch
    {
        DateOnly dateOnly => dateOnly,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.UtcDateTime),
        _ => throw new InvalidOperationException(
            $"Expected a date value for 'record_date', got '{value?.GetType().Name ?? "null"}'."),
    };

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value),
    };

    // Project/dataset/table come from tenant-owned config (GeminiCredential), not
    // external user input at this boundary — but BigQuery can't parameterize a
    // table reference, so it's interpolated directly. Reject backticks defensively
    // since a malformed config value could otherwise break out of the identifier.
    private static string BuildQualifiedTableReference(GeminiCredential credential)
    {
        foreach (var part in new[] { credential.BillingProjectId, credential.BillingDatasetId, credential.BillingTableId })
        {
            if (part.Contains('`'))
            {
                throw new InvalidOperationException(
                    "Gemini Enterprise billing export project/dataset/table identifiers may not contain a backtick.");
            }
        }

        return $"`{credential.BillingProjectId}.{credential.BillingDatasetId}.{credential.BillingTableId}`";
    }
}
