using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise cost through the newer Usage and Cost Analytics API
/// (<c>GET /v1/organizations/analytics/cost</c>, beta), grouped by actor and product. The default
/// source for the Claude Enterprise adapter; <see cref="HttpClaudeAnalyticsCostReportRepository"/>
/// (the legacy <c>user_cost_report</c>) remains behind the
/// <see cref="ClaudeEnterpriseOptions.UseLegacyCostReport"/> setting.
/// <para>
/// Live-tested 2026-11-20 against two tenants (docs/vendor-integration-reference.md, spike #21): for a
/// full closed month the daily totals equalled the legacy report to the cent, the existing Analytics
/// key works with the beta header, and the header is mandatory (the same URL is a 404 without it).
/// </para>
/// <para>
/// Differences from the legacy report that this class handles: <c>time_bucket_width</c> (no default),
/// <c>ending_at</c> mandatory once a bucket width is set, bracketed repeated <c>group_by[]</c>, the
/// actor's email arrives as <c>email_address</c>, and buckets with no usage produce no row (the
/// extractor zero-fills days). Every actor is requested, not only people, so the daily total counts all
/// cost; each row keeps the actor type so per-user reporting can tell a person from anything else.
/// </para>
/// <para>
/// <c>requests</c> comes from the cost rows themselves (<c>cost.requests</c>), which the spike found
/// equal to the usage endpoint's count, so no second API call is needed. On a seat-based plan the cost
/// is usage credits only and is near zero by design, while requests count seat-covered activity too.
/// </para>
/// </summary>
public sealed class HttpClaudeAnalyticsCostRepository : IClaudeAnalyticsCostReportRepository
{
    public const string BetaHeaderValue = "analytics-usage-cost-2026-08-24";

    private const string AnthropicVersion = "2023-06-01";

    // "starting_at ... must be within last 365 days, no earlier than 2026-01-01T00:00:00Z".
    private static readonly DateOnly EarliestAllowedDate = new(2026, 1, 1);
    private const int MaxLookbackDays = 365;

    // A request spans at most 31 days, distinct from page-size pagination.
    private const int MaxWindowSpanDays = 31;

    private const int PageLimit = 1000;

    // Backstops against a loop that never ends. Reaching one throws rather than returning what was
    // read so far: a silently truncated result would look like days with less spend.
    private const int MaxWindows = 1000;
    private const int MaxPagesPerWindow = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeAnalyticsCostRepository> _logger;

    public HttpClaudeAnalyticsCostRepository(HttpClient client, ILogger<HttpClaudeAnalyticsCostRepository> logger)
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
                "Requested Claude Enterprise analytics period start {RequestedStart} is older than what the "
                + "API allows, clamping to {EffectiveStart}. Older days will be missing from this pull.",
                period.Start, earliestAllowed);
            effectiveStart = earliestAllowed;
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var windowStart = effectiveStart;
        var periodEndExclusive = period.End.AddDays(1);

        for (var windowIndex = 0; windowStart <= period.End; windowIndex++)
        {
            if (windowIndex >= MaxWindows)
            {
                throw new InvalidOperationException(
                    $"Claude Enterprise analytics cost was still stepping through windows after {MaxWindows}; "
                    + "refusing to return a partial result.");
            }

            var windowEndExclusive = windowStart.AddDays(MaxWindowSpanDays);
            var actualEndingAt = windowEndExclusive < periodEndExclusive ? windowEndExclusive : periodEndExclusive;

            await QueryWindowAsync(analyticsApiKey, windowStart, actualEndingAt, rows, cancellationToken)
                .ConfigureAwait(false);

            windowStart = windowStart.AddDays(MaxWindowSpanDays);
        }

        _logger.LogDebug(
            "Claude Enterprise analytics cost returned {RowCount} (actor, product, day) row(s) for {PeriodStart} to {PeriodEnd}.",
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

        // Cursors are bound to the query that issued them, so every page repeats the same parameters.
        var baseUrl = "v1/organizations/analytics/cost"
            + $"?starting_at={Uri.EscapeDataString(startingAt)}&ending_at={Uri.EscapeDataString(endingAt)}"
            + $"&time_bucket_width=1d&group_by%5B%5D=actor&group_by%5B%5D=product&limit={PageLimit}";

        string? page = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPagesPerWindow; pageIndex++)
        {
            var url = page is null ? baseUrl : $"{baseUrl}&page={Uri.EscapeDataString(page)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("anthropic-beta", BetaHeaderValue);
            request.Headers.Add("x-api-key", analyticsApiKey);

            _logger.LogDebug("Querying Claude Enterprise analytics cost: {Url}", url);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<CostReportDto>(body, JsonOptions)
                ?? throw new InvalidOperationException(
                    "Claude Enterprise analytics cost response deserialized to null.");

            foreach (var row in report.Data ?? [])
            {
                // DateOnly.TryParse rejects a full ISO datetime with a time/"Z" part, so parse as a
                // DateTime first and take the date (the same pitfall as the other Claude endpoints).
                if (row.StartingAt is null || !DateTime.TryParse(
                        row.StartingAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startingAtUtc))
                {
                    continue;
                }

                var actor = row.Group?.Actor;
                rows.Add(new Dictionary<string, object?>
                {
                    [ClaudeAnalyticsCostRowFields.RecordDate] = DateOnly.FromDateTime(startingAtUtc),
                    // "amount" is a decimal string in cents, so dividing by 100 is load-bearing.
                    [ClaudeAnalyticsCostRowFields.AmountUsd] = ParseAmountCentsToDollars(row.Cost?.Amount),
                    [ClaudeAnalyticsCostRowFields.UserId] = actor?.UserId,
                    [ClaudeAnalyticsCostRowFields.UserEmail] = actor?.EmailAddress,
                    [ClaudeAnalyticsCostRowFields.UserName] = actor?.Name,
                    [ClaudeAnalyticsCostRowFields.Product] = row.Group?.Product,
                    [ClaudeAnalyticsCostRowFields.Requests] = row.Cost?.Requests,
                    [ClaudeAnalyticsCostRowFields.ActorType] = actor?.Type,
                    [ClaudeAnalyticsCostRowFields.ActorDeleted] = actor?.Deleted,
                });
            }

            if (!report.HasMore)
            {
                return;
            }

            // Checked before another request goes out: a blank cursor would be sent as an empty
            // page parameter, and a repeated one means the server is looping. Either way the result
            // would be incomplete or never end, and a partial month must not read as a successful one.
            if (string.IsNullOrWhiteSpace(report.NextPage))
            {
                throw new InvalidOperationException(
                    "Claude Enterprise analytics cost reported has_more but gave a missing or blank next_page cursor; "
                    + "refusing to return a partial result.");
            }

            if (!seenCursors.Add(report.NextPage))
            {
                throw new InvalidOperationException(
                    "Claude Enterprise analytics cost returned a next_page cursor it had already returned; "
                    + "refusing to loop and returning no partial result.");
            }

            page = report.NextPage;
        }

        throw new InvalidOperationException(
            $"Claude Enterprise analytics cost was still paginating after {MaxPagesPerWindow} pages for one window; "
            + "refusing to return a partial result.");
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The beta header is always sent, so a 404 means the beta changed or ended, not a missing header.
            throw new InvalidOperationException(
                "The Claude analytics cost endpoint returned 404. It is a beta endpoint "
                + $"(anthropic-beta: {BetaHeaderValue}), so this may mean the beta has changed or ended. "
                + $"Set {ClaudeEnterpriseOptions.ConfigurationSection}:UseLegacyCostReport to true to use the legacy "
                + "user_cost_report endpoint instead.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The Claude analytics cost endpoint returned {(int)response.StatusCode}. Check that the Analytics API "
                + "key is valid and has the read:analytics scope.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static decimal ParseAmountCentsToDollars(string? amount) =>
        amount is null ? 0m : decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture) / 100m;

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private sealed class CostReportDto
    {
        [JsonPropertyName("data")]
        public List<CostRowDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class CostRowDto
    {
        [JsonPropertyName("starting_at")]
        public string? StartingAt { get; set; }

        [JsonPropertyName("group")]
        public GroupDto? Group { get; set; }

        [JsonPropertyName("cost")]
        public CostDto? Cost { get; set; }
    }

    private sealed class GroupDto
    {
        [JsonPropertyName("actor")]
        public ActorDto? Actor { get; set; }

        [JsonPropertyName("product")]
        public string? Product { get; set; }
    }

    private sealed class CostDto
    {
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("requests")]
        public long? Requests { get; set; }
    }

    private sealed class ActorDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("email_address")]
        public string? EmailAddress { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("deleted")]
        public bool? Deleted { get; set; }
    }
}
