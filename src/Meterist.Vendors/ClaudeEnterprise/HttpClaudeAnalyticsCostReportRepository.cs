using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Real IClaudeAnalyticsCostReportRepository, against the Claude Enterprise
/// Analytics API's user_cost_report endpoint — see
/// docs/vendor-integration-reference.md for the verified endpoint/schema
/// detail (spiked 2026-07-22 against the public platform.claude.com API
/// reference).
/// </summary>
public sealed class HttpClaudeAnalyticsCostReportRepository : IClaudeAnalyticsCostReportRepository
{
    private const string AnthropicVersion = "2023-06-01";

    // "starting_at ... must be within last 365 days, no earlier than
    // 2026-01-01T00:00:00Z" — both bounds enforced by the API itself.
    private static readonly DateOnly EarliestAllowedDate = new(2026, 1, 1);
    private const int MaxLookbackDays = 365;

    // "ending_at ... Max span: 31 days" — a hard cap on a single query's date
    // range, distinct from page-size pagination. A multi-month extraction
    // period requires stepping through multiple 31-day windows, each with
    // its own has_more/next_page pagination inside it.
    private const int MaxWindowSpanDays = 31;

    private const int PageLimit = 1000;

    // Backstops against unexpected infinite loops — not expected to trigger.
    private const int MaxWindows = 1000;
    private const int MaxPagesPerWindow = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeAnalyticsCostReportRepository> _logger;

    public HttpClaudeAnalyticsCostReportRepository(
        HttpClient client, ILogger<HttpClaudeAnalyticsCostReportRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryUserCostRowsAsync(
        string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        var earliestByLookback = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-MaxLookbackDays));
        var earliestAllowed = earliestByLookback > EarliestAllowedDate ? earliestByLookback : EarliestAllowedDate;

        var effectiveStart = period.Start;
        if (effectiveStart < earliestAllowed)
        {
            _logger.LogWarning(
                "Requested Claude Enterprise Analytics period start {RequestedStart} is older than what the "
                + "API allows — clamping to {EffectiveStart}. Older days will be missing from this pull.",
                period.Start, earliestAllowed);
            effectiveStart = earliestAllowed;
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var windowStart = effectiveStart;

        for (var windowIndex = 0; windowIndex < MaxWindows && windowStart <= period.End; windowIndex++)
        {
            var windowEndExclusive = windowStart.AddDays(MaxWindowSpanDays);
            var periodEndExclusive = period.End.AddDays(1);
            var actualEndingAt = windowEndExclusive < periodEndExclusive ? windowEndExclusive : periodEndExclusive;

            await QueryWindowAsync(analyticsApiKey, windowStart, actualEndingAt, rows, cancellationToken)
                .ConfigureAwait(false);

            windowStart = windowStart.AddDays(MaxWindowSpanDays);
        }

        _logger.LogDebug(
            "Claude Enterprise Analytics API returned {RowCount} (user, day) row(s) for {PeriodStart} to {PeriodEnd}.",
            rows.Count, period.Start, period.End);

        return rows;
    }

    private async Task QueryWindowAsync(
        string analyticsApiKey,
        DateOnly windowStart,
        DateOnly windowEndExclusive,
        List<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        var startingAt = FormatTimestamp(windowStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var endingAt = FormatTimestamp(windowEndExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        string? page = null;

        for (var pageIndex = 0; pageIndex < MaxPagesPerWindow; pageIndex++)
        {
            var url = "v1/organizations/analytics/user_cost_report"
                + $"?starting_at={Uri.EscapeDataString(startingAt)}&ending_at={Uri.EscapeDataString(endingAt)}"
                + $"&bucket_width=1d&limit={PageLimit}"
                + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", analyticsApiKey);

            _logger.LogDebug("Querying Claude Enterprise Analytics user_cost_report: {Url}", url);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<UserCostReportDto>(body, JsonOptions)
                ?? throw new InvalidOperationException(
                    "Claude Enterprise Analytics user_cost_report response deserialized to null.");

            foreach (var row in report.Data ?? [])
            {
                // DateOnly.TryParse rejects a full ISO datetime string with a
                // time/"Z" component — parse as DateTime first, then take just
                // the date (the same fix HttpClaudeCostReportRepository needed
                // for Claude API Platform's cost_report).
                if (row.StartingAt is null || !DateTime.TryParse(
                        row.StartingAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startingAtUtc))
                {
                    continue;
                }

                rows.Add(new Dictionary<string, object?>
                {
                    [ClaudeAnalyticsCostRowFields.RecordDate] = DateOnly.FromDateTime(startingAtUtc),
                    // "amount" is a decimal string in cents — dividing by 100 here
                    // is load-bearing, not cosmetic (same pitfall as cost_report).
                    [ClaudeAnalyticsCostRowFields.AmountUsd] = ParseAmountCentsToDollars(row.Amount),
                    [ClaudeAnalyticsCostRowFields.UserId] = row.Actor?.UserId,
                    [ClaudeAnalyticsCostRowFields.UserEmail] = row.Actor?.Email,
                    [ClaudeAnalyticsCostRowFields.UserName] = row.Actor?.Name,
                });
            }

            if (!report.HasMore || report.NextPage is null)
            {
                break;
            }

            page = report.NextPage;
        }
    }

    private static decimal ParseAmountCentsToDollars(string? amount) =>
        amount is null ? 0m : decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture) / 100m;

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private sealed class UserCostReportDto
    {
        [JsonPropertyName("data")]
        public List<UserCostRowDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class UserCostRowDto
    {
        [JsonPropertyName("actor")]
        public ActorDto? Actor { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("starting_at")]
        public string? StartingAt { get; set; }
    }

    private sealed class ActorDto
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
