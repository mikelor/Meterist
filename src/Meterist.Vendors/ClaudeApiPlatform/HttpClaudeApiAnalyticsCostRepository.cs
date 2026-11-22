using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Claude API Platform cost through the newer Usage and Cost Analytics API
/// (<c>GET /v1/organizations/analytics/cost</c>, beta) with the same Admin API key as the legacy
/// <c>cost_report</c>, grouped by actor, API key, workspace and product. The default source for the adapter;
/// <see cref="HttpClaudeCostReportRepository"/> remains behind <see cref="ClaudeApiPlatformOptions.UseLegacyCostReport"/>.
/// <para>
/// Attribution: a personal key or service-account key reports the person or service account as the actor (the key is in
/// <c>api_key_id</c>); a legacy workspace key is linked to no identity, so the key itself is the actor. Every actor is
/// requested, so the daily total counts all cost.
/// </para>
/// <para>
/// Differences from the legacy report handled here: <c>time_bucket_width</c> has no default and needs an explicit
/// <c>ending_at</c>, requests span at most 31 days, <c>group_by[]</c> is a bracketed repeated parameter, <c>description</c>
/// grouping is gone (so model and token type are not kept), buckets with no usage produce no row (the extractor and
/// normalizer treat a missing day as zero), and a response can be revised for about 7 days after its month ends.
/// Priority Tier requests carry zero token cost under a commitment, as in the legacy report.
/// </para>
/// </summary>
public sealed class HttpClaudeApiAnalyticsCostRepository : IClaudeCostReportRepository
{
    public const string BetaHeaderValue = "analytics-usage-cost-2026-08-24";

    private const string AnthropicVersion = "2023-06-01";

    // "starting_at ... must be within last 365 days, no earlier than 2026-01-01T00:00:00Z".
    private static readonly DateOnly EarliestAllowedDate = new(2026, 1, 1);
    private const int MaxLookbackDays = 365;
    private const int MaxWindowSpanDays = 31;
    private const int PageLimit = 1000;

    // Backstops against a loop that never ends. Reaching one throws rather than returning what was read so far:
    // a silently truncated result would look like days with less spend.
    private const int MaxWindows = 1000;
    private const int MaxPagesPerWindow = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeApiAnalyticsCostRepository> _logger;

    public HttpClaudeApiAnalyticsCostRepository(HttpClient client, ILogger<HttpClaudeApiAnalyticsCostRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        string adminApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        var earliestByLookback = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-MaxLookbackDays));
        var earliestAllowed = earliestByLookback > EarliestAllowedDate ? earliestByLookback : EarliestAllowedDate;

        var effectiveStart = period.Start;
        if (effectiveStart < earliestAllowed)
        {
            _logger.LogWarning(
                "Requested Claude API Platform analytics period start {RequestedStart} is older than what the API allows, "
                + "clamping to {EffectiveStart}. Older days will be missing from this pull.",
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
                    $"Claude API Platform analytics cost was still stepping through windows after {MaxWindows}; "
                    + "refusing to return a partial result.");
            }

            var windowEndExclusive = windowStart.AddDays(MaxWindowSpanDays);
            var actualEndingAt = windowEndExclusive < periodEndExclusive ? windowEndExclusive : periodEndExclusive;

            await QueryWindowAsync(adminApiKey, windowStart, actualEndingAt, rows, cancellationToken).ConfigureAwait(false);

            windowStart = windowStart.AddDays(MaxWindowSpanDays);
        }

        _logger.LogDebug(
            "Claude API Platform analytics cost returned {RowCount} row(s) for {PeriodStart} to {PeriodEnd}.",
            rows.Count, period.Start, period.End);

        return rows;
    }

    private async Task QueryWindowAsync(
        string adminApiKey,
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
            + "&time_bucket_width=1d"
            + "&group_by%5B%5D=actor&group_by%5B%5D=api_key_id&group_by%5B%5D=workspace_id&group_by%5B%5D=product"
            + $"&limit={PageLimit}";

        string? page = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPagesPerWindow; pageIndex++)
        {
            var url = page is null ? baseUrl : $"{baseUrl}&page={Uri.EscapeDataString(page)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("anthropic-beta", BetaHeaderValue);
            request.Headers.Add("x-api-key", adminApiKey);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<CostReportDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("Claude API Platform analytics cost response deserialized to null.");

            // A response with no data array is malformed, not an empty day: storing it as zero spend would overwrite real days.
            if (report.Data is null)
            {
                throw new InvalidOperationException(
                    "Claude API Platform analytics cost response had no data array; refusing to treat it as an empty period.");
            }

            foreach (var row in report.Data)
            {
                // DateOnly.TryParse rejects a full ISO datetime, so parse as a DateTime first and take the date.
                if (row.StartingAt is null || !DateTime.TryParse(
                        row.StartingAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startingAtUtc))
                {
                    continue;
                }

                var actor = row.Group?.Actor;
                rows.Add(new Dictionary<string, object?>
                {
                    [ClaudeApiCostRowFields.RecordDate] = DateOnly.FromDateTime(startingAtUtc),
                    // "amount" is a decimal string in cents, so dividing by 100 is load-bearing.
                    [ClaudeApiCostRowFields.AmountUsd] = ParseAmountCentsToDollars(row.Cost?.Amount),
                    [ClaudeApiCostRowFields.WorkspaceId] = row.Group?.WorkspaceId,
                    [ClaudeApiCostRowFields.ApiKeyId] = row.Group?.ApiKeyId,
                    [ClaudeApiCostRowFields.Product] = row.Group?.Product,
                    [ClaudeApiCostRowFields.Requests] = row.Cost?.Requests,
                    [ClaudeApiCostRowFields.UserId] = ActorIdOf(actor),
                    [ClaudeApiCostRowFields.UserEmail] = actor?.EmailAddress,
                    [ClaudeApiCostRowFields.UserName] = actor?.Name,
                    [ClaudeApiCostRowFields.ActorType] = actor?.Type,
                    [ClaudeApiCostRowFields.ActorDeleted] = actor?.Deleted,
                });
            }

            if (!report.HasMore)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(report.NextPage))
            {
                throw new InvalidOperationException(
                    "Claude API Platform analytics cost reported has_more but gave a missing or blank next_page cursor; "
                    + "refusing to return a partial result.");
            }

            if (!seenCursors.Add(report.NextPage))
            {
                throw new InvalidOperationException(
                    "Claude API Platform analytics cost returned a next_page cursor it had already returned; "
                    + "refusing to loop and returning no partial result.");
            }

            page = report.NextPage;
        }

        throw new InvalidOperationException(
            $"Claude API Platform analytics cost was still paginating after {MaxPagesPerWindow} pages for one window; "
            + "refusing to return a partial result.");
    }

    // The id that names the actor, by type. A federated identity has no id of its own; its subject is the nearest thing.
    private static string? ActorIdOf(ActorDto? actor) => actor?.Type switch
    {
        null => null,
        "user_actor" => actor.UserId,
        "service_account_actor" => actor.ServiceAccountId,
        "api_actor" => actor.ApiKeyId,
        "federated_actor" => actor.Subject,
        _ => actor.UserId ?? actor.ServiceAccountId ?? actor.ApiKeyId,
    };

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Claude analytics cost endpoint returned 404. It is a beta endpoint "
                + $"(anthropic-beta: {BetaHeaderValue}), so this may mean the beta has changed or ended. "
                + $"Set {ClaudeApiPlatformOptions.ConfigurationSection}:UseLegacyCostReport to true to use the legacy "
                + "cost_report endpoint instead.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The Claude analytics cost endpoint returned {(int)response.StatusCode}. Check that the Admin API key is valid.");
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

        [JsonPropertyName("api_key_id")]
        public string? ApiKeyId { get; set; }

        [JsonPropertyName("workspace_id")]
        public string? WorkspaceId { get; set; }

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

        [JsonPropertyName("service_account_id")]
        public string? ServiceAccountId { get; set; }

        [JsonPropertyName("api_key_id")]
        public string? ApiKeyId { get; set; }

        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("email_address")]
        public string? EmailAddress { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("deleted")]
        public bool? Deleted { get; set; }
    }
}
