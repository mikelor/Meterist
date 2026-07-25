using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Real IClaudeCostReportRepository, against the Anthropic Admin API's Cost
/// Report endpoint — see docs/vendor-integration-reference.md for the
/// verified endpoint/schema detail (spiked 2026-07-22 against the public
/// platform.claude.com API reference). Simpler than the ChatGPT/Gemini
/// adapters: a single authenticated GET, no redirect/signed-URL dance, no
/// per-tenant config beyond the bare Admin API key.
/// </summary>
public sealed class HttpClaudeCostReportRepository : IClaudeCostReportRepository
{
    private const string AnthropicVersion = "2023-06-01";

    // Documented max bucket count for 1d granularity (per the Usage API's
    // published limits table; cost_report doesn't restate its own ceiling
    // but is daily-only, same grain) — kept low to minimize round-trips
    // while the has_more/next_page loop still handles whatever the server
    // actually enforces.
    private const int PageLimit = 31;

    // Backstop against an unexpected has_more=true loop that never
    // terminates — not expected to ever trigger.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeCostReportRepository> _logger;

    public HttpClaudeCostReportRepository(HttpClient client, ILogger<HttpClaudeCostReportRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        string adminApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        var startingAt = FormatTimestamp(period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var endingAt = FormatTimestamp(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        string? page = null;

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = "v1/organizations/cost_report"
                + $"?starting_at={Uri.EscapeDataString(startingAt)}&ending_at={Uri.EscapeDataString(endingAt)}"
                + "&group_by[]=description&group_by[]=workspace_id"
                + $"&limit={PageLimit}"
                + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", adminApiKey);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meterist", "1.0"));

            _logger.LogDebug("Querying Claude API Platform cost_report: {Url}", url);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<CostReportDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("Claude API Platform cost_report response deserialized to null.");

            foreach (var bucket in report.Data ?? [])
            {
                // DateOnly.TryParse rejects a full ISO datetime string with a time/"Z"
                // component ("contains parts which are not specific to the DateOnly") —
                // parse as DateTime first, then take just the date.
                if (bucket.StartingAt is null || !DateTime.TryParse(
                        bucket.StartingAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startingAtUtc))
                {
                    continue;
                }

                var recordDate = DateOnly.FromDateTime(startingAtUtc);

                foreach (var result in bucket.Results ?? [])
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        [ClaudeApiCostRowFields.RecordDate] = recordDate,
                        // "amount" is a decimal string in cents (e.g. "123.45" == $1.23) — see
                        // the class doc comment; dividing by 100 here is load-bearing, not cosmetic.
                        [ClaudeApiCostRowFields.AmountUsd] = ParseAmountCentsToDollars(result.Amount),
                        [ClaudeApiCostRowFields.Description] = result.Description,
                        [ClaudeApiCostRowFields.Model] = result.Model,
                        [ClaudeApiCostRowFields.TokenType] = result.TokenType,
                        [ClaudeApiCostRowFields.CostType] = result.CostType,
                        [ClaudeApiCostRowFields.ServiceTier] = result.ServiceTier,
                        [ClaudeApiCostRowFields.WorkspaceId] = result.WorkspaceId,
                    });
                }
            }

            if (!report.HasMore || report.NextPage is null)
            {
                break;
            }

            page = report.NextPage;
        }

        _logger.LogDebug(
            "Claude API Platform cost_report returned {RowCount} cost line item(s) for {PeriodStart} to {PeriodEnd}.",
            rows.Count, period.Start, period.End);

        return rows;
    }

    private static decimal ParseAmountCentsToDollars(string? amount) =>
        amount is null ? 0m : decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture) / 100m;

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private sealed class CostReportDto
    {
        [JsonPropertyName("data")]
        public List<CostReportBucketDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class CostReportBucketDto
    {
        [JsonPropertyName("starting_at")]
        public string? StartingAt { get; set; }

        [JsonPropertyName("ending_at")]
        public string? EndingAt { get; set; }

        [JsonPropertyName("results")]
        public List<CostReportResultDto>? Results { get; set; }
    }

    private sealed class CostReportResultDto
    {
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("cost_type")]
        public string? CostType { get; set; }

        [JsonPropertyName("service_tier")]
        public string? ServiceTier { get; set; }

        [JsonPropertyName("workspace_id")]
        public string? WorkspaceId { get; set; }
    }
}
