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
/// usage instead.
///
/// Which service/SKU pairs count is a <see cref="GeminiBillingFilter"/> (configuration,
/// not code), and <see cref="FindExcludedChargesAsync"/> reports Gemini-looking lines the
/// filter leaves out, so a new SKU is noticed instead of silently dropped. Each returned
/// row also carries the kind of charge the matching rule names (its category) and whether it
/// is usage or a non-usage charge such as tax or support, decided in SQL by the same first-match
/// rule order that decides inclusion, so the two cannot disagree.
///
/// Aggregates to (day, service, SKU) grain at the SQL level (GROUP BY DATE(...)...)
/// rather than returning raw hourly rows for the extractor to bucket later —
/// this is what makes "daily is the raw grain" a guarantee of the query
/// itself, not a convention, and it moves less data over the wire.
/// </summary>
public sealed class BigQueryGeminiBillingRepository : IGeminiBillingQueryRepository
{
    private const string ServiceColumn = "service.description";
    private const string SkuColumn = "sku.description";

    private readonly GeminiBillingFilter _filter;
    private readonly ILogger<BigQueryGeminiBillingRepository> _logger;

    public BigQueryGeminiBillingRepository(
        GeminiBillingFilter filter, ILogger<BigQueryGeminiBillingRepository> logger)
    {
        filter.Validate();
        _filter = filter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryBillingRowsAsync(
        GeminiCredential credential,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        var tableRef = BuildQualifiedTableReference(credential);
        var client = await CreateClientAsync(credential).ConfigureAwait(false);

        var sql = BuildSql(tableRef, _filter);
        var parameters = BuildParameters(_filter, period, forExcludedQuery: false);

        // Deliberately not logging credential.ServiceAccountJson — everything else
        // here is safe to log and is exactly what you'd want when a query comes
        // back empty and you need to know precisely what was asked for.
        _logger.LogDebug(
            "Querying Gemini Enterprise billing export {Table} for {PeriodStart} to {PeriodEnd} "
            + "with {RuleCount} include and {NonUsageCount} non-usage rule(s). SQL: {Sql}",
            tableRef, period.Start, period.End, _filter.Include.Count, _filter.NonUsage.Count, sql);

        var results = await client.ExecuteQueryAsync(sql, parameters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in results)
        {
            rows.Add(new Dictionary<string, object?>
            {
                [GeminiBillingRowFields.RecordDate] = ToDateOnly(row["record_date"]),
                [GeminiBillingRowFields.ServiceDescription] = row["service_description"]?.ToString(),
                [GeminiBillingRowFields.SkuDescription] = row["sku_description"]?.ToString(),
                [GeminiBillingRowFields.Category] = row["meterist_category"]?.ToString(),
                [GeminiBillingRowFields.IsUsage] = row["meterist_is_usage"] is bool isUsage && isUsage,
                [GeminiBillingRowFields.Cost] = ToDecimal(row["cost"]),
                [GeminiBillingRowFields.CreditsAmount] = ToDecimal(row["credits_amount"]),
            });
        }

        _logger.LogDebug(
            "Gemini Enterprise billing export {Table} returned {RowCount} (day, SKU) row(s) for {PeriodStart} to {PeriodEnd}.",
            tableRef, rows.Count, period.Start, period.End);

        return rows;
    }

    public async Task<IReadOnlyList<ExcludedBillingLine>> FindExcludedChargesAsync(
        GeminiCredential credential,
        DateRange period,
        CancellationToken cancellationToken = default)
    {
        if (_filter.WatchTerms.Count == 0)
        {
            return [];
        }

        var tableRef = BuildQualifiedTableReference(credential);
        var client = await CreateClientAsync(credential).ConfigureAwait(false);

        var sql = BuildExcludedSql(tableRef, _filter);
        var parameters = BuildParameters(_filter, period, forExcludedQuery: true);

        _logger.LogDebug("Looking for excluded Gemini-looking charges in {Table}. SQL: {Sql}", tableRef, sql);

        var results = await client.ExecuteQueryAsync(sql, parameters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<ExcludedBillingLine>();
        foreach (var row in results)
        {
            lines.Add(new ExcludedBillingLine(
                row["service_description"]?.ToString() ?? string.Empty,
                row["sku_description"]?.ToString() ?? string.Empty,
                ToDecimal(row["total_cost"]),
                ToDateOnly(row["first_date"]),
                ToDateOnly(row["last_date"])));
        }

        return lines;
    }

    // Extracted purely so the WHERE-clause shape is unit-testable without a live
    // BigQuery connection — see BigQueryGeminiBillingRepositoryTests, added
    // specifically to guard against silently reverting the 2026-07-22 fix above.
    //
    // Aliases are prefixed meterist_ and the cost sum is aliased as cost only because nothing here
    // aggregates it again; the excluded query below must not (BigQuery rejects an aggregate of an
    // aggregate when an alias shadows the column).
    public static string BuildSql(string tableRef, GeminiBillingFilter filter)
    {
        var usage = IncludeClause(filter.Include, "inc", ServiceColumn, SkuColumn);
        var counted = filter.NonUsage.Count == 0
            ? $"({usage})"
            : $"(({usage}) OR ({IncludeClause(filter.NonUsage, "non", ServiceColumn, SkuColumn)}))";

        return $"""
            SELECT
              DATE(usage_start_time) AS record_date,
              {ServiceColumn} AS service_description,
              {SkuColumn} AS sku_description,
              {CategoryCase(filter)} AS meterist_category,
              CASE WHEN ({usage}) THEN TRUE ELSE FALSE END AS meterist_is_usage,
              SUM(cost) AS cost,
              IFNULL(SUM((SELECT SUM(c.amount) FROM UNNEST(credits) AS c)), 0) AS credits_amount
            FROM {tableRef}
            WHERE {counted}
              AND DATE(usage_start_time) BETWEEN @periodStart AND @periodEnd
            GROUP BY record_date, service_description, sku_description, meterist_category, meterist_is_usage
            ORDER BY record_date
            """;
    }

    /// <summary>
    /// Lines the include and non-usage rules leave out, that are not on the ignore list, and that match a
    /// watch term in their service or SKU, with a non-zero total cost. NULL descriptions are
    /// coalesced so a line with a missing SKU is still seen rather than lost to SQL's NULL logic.
    /// </summary>
    public static string BuildExcludedSql(string tableRef, GeminiBillingFilter filter)
    {
        const string Service = "COALESCE(service.description, '')";
        const string Sku = "COALESCE(sku.description, '')";

        var watch = string.Join(
            " OR ",
            filter.WatchTerms.Select((_, i) => $"LOWER({Service}) LIKE @watch{i} OR LOWER({Sku}) LIKE @watch{i}"));

        // A line that is counted or recorded as non-usage has been dealt with, so it is not "excluded".
        var counted = IncludeClause(filter.Include, "inc", Service, Sku);
        if (filter.NonUsage.Count > 0)
        {
            counted += " OR " + IncludeClause(filter.NonUsage, "non", Service, Sku);
        }

        var ignore = filter.Ignore.Count == 0
            ? string.Empty
            : $"\n  AND NOT ({IncludeClause(filter.Ignore, "ign", Service, Sku)})";

        return $"""
            SELECT
              {Service} AS service_description,
              {Sku} AS sku_description,
              SUM(cost) AS total_cost,
              MIN(DATE(usage_start_time)) AS first_date,
              MAX(DATE(usage_start_time)) AS last_date
            FROM {tableRef}
            WHERE DATE(usage_start_time) BETWEEN @periodStart AND @periodEnd
              AND NOT ({counted}){ignore}
              AND ({watch})
            GROUP BY service_description, sku_description
            HAVING SUM(cost) != 0
            ORDER BY service_description, sku_description
            """;
    }

    /// <summary>
    /// Query parameters. The main query takes the include and non-usage rules with their categories; the
    /// excluded-charges query takes the same rules without categories (it never reads them) plus the ignore
    /// rules and watch terms. Every parameter given is referenced by its query.
    /// </summary>
    public static BigQueryParameter[] BuildParameters(
        GeminiBillingFilter filter, DateRange period, bool forExcludedQuery)
    {
        var parameters = new List<BigQueryParameter>
        {
            new("periodStart", BigQueryDbType.Date, period.Start.ToDateTime(TimeOnly.MinValue)),
            new("periodEnd", BigQueryDbType.Date, period.End.ToDateTime(TimeOnly.MinValue)),
        };

        AddRuleParameters(parameters, filter.Include, "inc", withCategory: !forExcludedQuery);
        AddRuleParameters(parameters, filter.NonUsage, "non", withCategory: !forExcludedQuery);

        if (forExcludedQuery)
        {
            AddRuleParameters(parameters, filter.Ignore, "ign", withCategory: false);
            for (var i = 0; i < filter.WatchTerms.Count; i++)
            {
                parameters.Add(new BigQueryParameter(
                    $"watch{i}", BigQueryDbType.String, $"%{filter.WatchTerms[i].Trim().ToLowerInvariant()}%"));
            }
        }

        return [.. parameters];
    }

    // First matching rule wins, include rules before non-usage ones. A rule with no category yields NULL,
    // which the normalizer replaces by classifying the SKU text, so a configuration written before categories
    // existed keeps behaving as it did.
    private static string CategoryCase(GeminiBillingFilter filter)
    {
        var whens = filter.Include
            .Select((_, i) => $"WHEN ({ServiceColumn} = @incService{i} AND {SkuColumn} LIKE @incSku{i}) THEN @incCategory{i}")
            .Concat(filter.NonUsage.Select((_, i) =>
                $"WHEN ({ServiceColumn} = @nonService{i} AND {SkuColumn} LIKE @nonSku{i}) THEN @nonCategory{i}"));

        return "CASE " + string.Join(" ", whens) + " END";
    }

    private static string IncludeClause(
        IReadOnlyList<GeminiBillingRule> rules, string prefix, string serviceColumn, string skuColumn) =>
        string.Join(
            " OR ",
            rules.Select((_, i) => $"({serviceColumn} = @{prefix}Service{i} AND {skuColumn} LIKE @{prefix}Sku{i})"));

    private static void AddRuleParameters(
        List<BigQueryParameter> parameters, IReadOnlyList<GeminiBillingRule> rules, string prefix, bool withCategory)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            parameters.Add(new BigQueryParameter($"{prefix}Service{i}", BigQueryDbType.String, rules[i].Service));
            parameters.Add(new BigQueryParameter($"{prefix}Sku{i}", BigQueryDbType.String, rules[i].SkuLike));
            if (withCategory)
            {
                parameters.Add(new BigQueryParameter($"{prefix}Category{i}", BigQueryDbType.String, rules[i].Category));
            }
        }
    }

    private static async Task<BigQueryClient> CreateClientAsync(GeminiCredential credential)
    {
        // GoogleCredential.FromJson(string) is obsolete (security risk) — go through
        // CredentialFactory for a concrete service-account credential instead.
        var googleCredential = CredentialFactory
            .FromJson<ServiceAccountCredential>(credential.ServiceAccountJson)
            .ToGoogleCredential();

        return await BigQueryClient.CreateAsync(credential.BillingProjectId, googleCredential)
            .ConfigureAwait(false);
    }

    private static DateOnly ToDateOnly(object? value) => value switch
    {
        DateOnly dateOnly => dateOnly,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.UtcDateTime),
        _ => throw new InvalidOperationException(
            $"Expected a date value, got '{value?.GetType().Name ?? "null"}'."),
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
