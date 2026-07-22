using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Real IGeminiBillingQueryRepository, querying the tenant's Cloud Billing
/// BigQuery export. Filters on service.description rather than an exact SKU
/// string so newly added Gemini Enterprise SKU lines (Agent Gateway, Memory
/// Bank/Sessions — see docs/vendor-integration-reference.md) keep being
/// picked up without a code change.
///
/// Aggregates to (day, SKU) grain at the SQL level (GROUP BY DATE(...), sku)
/// rather than returning raw hourly rows for the extractor to bucket later —
/// this is what makes "daily is the raw grain" a guarantee of the query
/// itself, not a convention, and it moves less data over the wire.
/// </summary>
public sealed class BigQueryGeminiBillingRepository : IGeminiBillingQueryRepository
{
    private const string ServiceDescriptionFilter = "%Gemini Enterprise%";

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

        var sql = $"""
            SELECT
              DATE(usage_start_time) AS record_date,
              sku.description AS sku_description,
              SUM(cost) AS cost,
              IFNULL(SUM((SELECT SUM(c.amount) FROM UNNEST(credits) AS c)), 0) AS credits_amount
            FROM {tableRef}
            WHERE service.description LIKE @serviceFilter
              AND DATE(usage_start_time) BETWEEN @periodStart AND @periodEnd
            GROUP BY record_date, sku_description
            ORDER BY record_date
            """;

        var parameters = new[]
        {
            new BigQueryParameter("serviceFilter", BigQueryDbType.String, ServiceDescriptionFilter),
            new BigQueryParameter("periodStart", BigQueryDbType.Date, period.Start.ToDateTime(TimeOnly.MinValue)),
            new BigQueryParameter("periodEnd", BigQueryDbType.Date, period.End.ToDateTime(TimeOnly.MinValue)),
        };

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

        return rows;
    }

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
