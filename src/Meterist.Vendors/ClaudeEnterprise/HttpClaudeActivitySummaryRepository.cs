using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>One day of the Claude activity summary: the date and every numeric field the API returned for it.</summary>
public sealed record ClaudeActivityDay(DateOnly Date, IReadOnlyDictionary<string, decimal?> Values);

/// <summary>Seam between the Claude activity-summary source and <c>GET /v1/organizations/analytics/summaries</c>.</summary>
public interface IClaudeActivitySummaryRepository
{
    /// <summary>
    /// One entry per day the API has a summary for within the period, with every numeric field it returned. Days it
    /// does not have yet (the newest is published the next day) are absent. Complete or throws.
    /// </summary>
    Task<IReadOnlyList<ClaudeActivityDay>> QueryAsync(
        string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IClaudeActivitySummaryRepository"/>. Live-tested 2026-11-20 against two tenants with the existing
/// Analytics key: the endpoint needs no beta header, the response is <c>data</c> (the same rows also appear under
/// <c>summaries</c>) plus <c>next_page</c>, <c>starting_date</c> is inclusive and <c>ending_date</c> exclusive, data
/// starts 2026-01-01 (earlier is a 400), the newest day available is yesterday (UTC) and an <c>ending_date</c>
/// beyond it is a 400 that names the latest available day. The span cap is 366 days.
/// <para>
/// Every numeric field is returned as it came, keyed by its API name, so a product the vendor adds later is not lost
/// and the mapping to stored rows (in <see cref="ClaudeEnterpriseActivitySummarySource"/>) decides what to keep.
/// Counts only: no names or emails are in this response.
/// </para>
/// </summary>
public sealed partial class HttpClaudeActivitySummaryRepository : IClaudeActivitySummaryRepository
{
    private const string AnthropicVersion = "2023-06-01";

    private static readonly DateOnly EarliestAllowedDate = new(2026, 1, 1);
    private const int MaxSpanDays = 366;
    private const int MaxWindows = 100;
    private const int MaxPagesPerWindow = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeActivitySummaryRepository> _logger;

    // Injected by Program.cs via AddHttpClient. The shared Analytics rate limit (60 requests a minute) is far above
    // what this needs, one request per 366-day window, and 429s are retried by the standard resilience handler.
    public HttpClaudeActivitySummaryRepository(HttpClient client, ILogger<HttpClaudeActivitySummaryRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClaudeActivityDay>> QueryAsync(
        string analyticsApiKey, DateRange period, CancellationToken cancellationToken = default)
    {
        var start = period.Start;
        if (start < EarliestAllowedDate)
        {
            _logger.LogDebug(
                "Claude activity summary start {Requested} is before the API's first day, using {Effective}.",
                period.Start, EarliestAllowedDate);
            start = EarliestAllowedDate;
        }

        // The newest day is published the next day, so the last day that can exist is yesterday (UTC).
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var end = period.End < yesterday ? period.End : yesterday;

        var days = new List<ClaudeActivityDay>();
        var windowStart = start;

        for (var windowIndex = 0; windowStart <= end; windowIndex++)
        {
            if (windowIndex >= MaxWindows)
            {
                throw new InvalidOperationException(
                    $"The Claude activity summary was still stepping through windows after {MaxWindows}; refusing to return a partial result.");
            }

            var windowEnd = windowStart.AddDays(MaxSpanDays - 1);
            if (windowEnd > end)
            {
                windowEnd = end;
            }

            var lastDay = await QueryWindowAsync(analyticsApiKey, windowStart, windowEnd, days, cancellationToken)
                .ConfigureAwait(false);

            // The API said the newest day it has is earlier than asked for, so nothing later exists.
            if (lastDay < windowEnd)
            {
                break;
            }

            windowStart = windowEnd.AddDays(1);
        }

        _logger.LogDebug(
            "The Claude activity summary returned {DayCount} day(s) for {PeriodStart} to {PeriodEnd}.",
            days.Count, period.Start, period.End);

        return days;
    }

    // Returns the last day the query could cover: windowEnd, or earlier when the API reported a lower latest day.
    private async Task<DateOnly> QueryWindowAsync(
        string analyticsApiKey,
        DateOnly windowStart,
        DateOnly windowEnd,
        List<ClaudeActivityDay> days,
        CancellationToken cancellationToken)
    {
        var added = days.Count;
        var coveredEnd = windowEnd;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await PageThroughAsync(analyticsApiKey, windowStart, coveredEnd, days, cancellationToken).ConfigureAwait(false);
                return coveredEnd;
            }
            catch (LatestAvailableException ex) when (attempt == 0 && ex.Latest < coveredEnd)
            {
                // Before the day's snapshot is published (around 13:00 UTC) yesterday may not exist yet. The API
                // says which day is the newest; ask again for just that, once.
                days.RemoveRange(added, days.Count - added);
                if (ex.Latest < windowStart)
                {
                    return ex.Latest;
                }

                _logger.LogDebug("The Claude activity summary's latest day is {Latest}; asking for up to that day.", ex.Latest);
                coveredEnd = ex.Latest;
            }
        }
    }

    private async Task PageThroughAsync(
        string analyticsApiKey,
        DateOnly windowStart,
        DateOnly windowEnd,
        List<ClaudeActivityDay> days,
        CancellationToken cancellationToken)
    {
        // ending_date is exclusive.
        var baseUrl = "v1/organizations/analytics/summaries"
            + $"?starting_date={windowStart:yyyy-MM-dd}&ending_date={windowEnd.AddDays(1):yyyy-MM-dd}";

        string? page = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPagesPerWindow; pageIndex++)
        {
            var url = page is null ? baseUrl : $"{baseUrl}&page={Uri.EscapeDataString(page)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", analyticsApiKey);

            _logger.LogDebug("Querying the Claude activity summary, {From} to {To}, page {Page}.", windowStart, windowEnd, pageIndex + 1);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response, body);

            var report = JsonSerializer.Deserialize<SummaryPageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("The Claude activity summary deserialized to null.");

            foreach (var row in report.Data ?? [])
            {
                if (row.StartingAt is null || !DateTime.TryParse(
                        row.StartingAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startingAtUtc))
                {
                    throw new InvalidOperationException(
                        "The Claude activity summary held a row with no usable starting_at; refusing to return a partial result.");
                }

                var values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
                foreach (var (name, element) in row.Extra ?? [])
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
                    {
                        values[name] = number;
                    }
                    else if (element.ValueKind == JsonValueKind.Null)
                    {
                        values[name] = null;
                    }
                }

                days.Add(new ClaudeActivityDay(DateOnly.FromDateTime(startingAtUtc), values));
            }

            if (string.IsNullOrEmpty(report.NextPage))
            {
                return;
            }

            // A repeated cursor means the server is looping, and the result would never end or would double count.
            if (!seenCursors.Add(report.NextPage))
            {
                throw new InvalidOperationException(
                    "The Claude activity summary returned a next_page cursor it had already returned; refusing to loop and returning no partial result.");
            }

            page = report.NextPage;
        }

        throw new InvalidOperationException(
            $"The Claude activity summary was still paginating after {MaxPagesPerWindow} pages; refusing to return a partial result.");
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var match = LatestAvailable().Match(body);
            if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var latest))
            {
                throw new LatestAvailableException(latest);
            }
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The Claude activity summary returned {(int)response.StatusCode}. Check that the Analytics API key is valid and has the read:analytics scope.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Claude activity summary returned 404. The analytics summaries endpoint needs a Claude Enterprise plan.");
        }

        response.EnsureSuccessStatusCode();
    }

    [GeneratedRegex(@"Latest available data for this query is (\d{4}-\d{2}-\d{2})")]
    private static partial Regex LatestAvailable();

    private sealed class LatestAvailableException(DateOnly latest) : Exception("The API reported its latest available day.")
    {
        public DateOnly Latest { get; } = latest;
    }

    private sealed class SummaryPageDto
    {
        [JsonPropertyName("data")]
        public List<SummaryRowDto>? Data { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class SummaryRowDto
    {
        [JsonPropertyName("starting_at")]
        public string? StartingAt { get; set; }

        // Everything else (every count and rate, including ones a later API version adds).
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
