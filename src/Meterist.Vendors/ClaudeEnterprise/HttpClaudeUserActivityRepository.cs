using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meterist.Core.Http;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// One person's activity for one day as the API returned it: who they are, and the rest of the row as JSON (every product's
/// metrics, with the <c>user</c> identity taken out because it is kept in the two fields here). Personal data (an email).
/// </summary>
public sealed class ClaudeUserActivityRow
{
    public required string UserId { get; init; }

    public string? Email { get; init; }

    public required string MetricsJson { get; init; }
}

/// <summary>Seam between the Claude user-activity source and <c>GET /v1/organizations/analytics/users</c>.</summary>
public interface IClaudeUserActivityRepository
{
    /// <summary>
    /// Every active person's row for one day, following the cursor to the end. Returns the complete day or throws. Returns
    /// null when the API has no data for that day (not published yet, or before its data begins), which is not an error.
    /// </summary>
    Task<IReadOnlyList<ClaudeUserActivityRow>?> QueryDayAsync(
        string apiKey, DateOnly day, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IClaudeUserActivityRepository"/>, in single-day mode (<c>date=</c>). Live-tested 2026-11-21 against two
/// tenants with the existing Analytics key and no beta header. One day is one request for an organisation this size; rows
/// exist only for people active that day; and the sum of seven single days equalled the seven-day rollup for every user, so
/// single days lose nothing. A date past the newest published day is a 400 naming it ("Latest available data for this query is
/// ..."), and one before 2026-01-01 is a 400 too; both are returned as null.
/// <para>
/// <b>The cursor is an email address in disguise.</b> <c>next_page</c> is base64 of a small JSON object whose
/// <c>last_email</c> is the last person returned, so a URL carrying it is personal data. Every request is marked sensitive
/// (the OpenTelemetry HTTP instrumentation drops it), the client is registered without the factory's request logging, and
/// nothing here logs a URL. See docs/vendor-integration-reference.md.
/// </para>
/// </summary>
public sealed partial class HttpClaudeUserActivityRepository : IClaudeUserActivityRepository
{
    private const string AnthropicVersion = "2023-06-01";
    private const int PageLimit = 1000;

    // Backstop against a cursor loop. Reaching it throws rather than returning a partial day.
    private const int MaxPagesPerDay = 100;

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeUserActivityRepository> _logger;

    public HttpClaudeUserActivityRepository(HttpClient client, ILogger<HttpClaudeUserActivityRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClaudeUserActivityRow>?> QueryDayAsync(
        string apiKey, DateOnly day, CancellationToken cancellationToken = default)
    {
        var rows = new List<ClaudeUserActivityRow>();
        var seenUsers = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;

        for (var pageIndex = 0; pageIndex < MaxPagesPerDay; pageIndex++)
        {
            var url = $"v1/organizations/analytics/users?date={day:yyyy-MM-dd}&limit={PageLimit}"
                + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", apiKey);
            request.MarkSensitive();

            // The day and page number only: the cursor holds an email address.
            _logger.LogDebug("Querying Claude user activity for {Day}, page {Page}.", day, pageIndex + 1);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (IsNotAvailable(response, body))
            {
                // Not published yet, or before the data begins. Nothing was read for the day, so report that and not a partial day.
                return null;
            }

            ThrowIfNotSuccess(response);

            var root = JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("The Claude user activity response was not a JSON object.");

            foreach (var item in root["data"] as JsonArray ?? [])
            {
                if (item is not JsonObject row)
                {
                    throw new InvalidOperationException("The Claude user activity response held a row that is not an object; refusing to return a partial day.");
                }

                var user = row["user"] as JsonObject;
                var userId = user?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(userId))
                {
                    throw new InvalidOperationException(
                        $"The Claude user activity for {day:yyyy-MM-dd} held a row with no user id; refusing to return a partial day.");
                }

                // The same person twice (a page boundary shifting under the cursor) would double count them.
                if (!seenUsers.Add(userId))
                {
                    throw new InvalidOperationException(
                        $"The Claude user activity for {day:yyyy-MM-dd} listed a person more than once; refusing to return an unreliable day.");
                }

                var email = user?["email_address"]?.GetValue<string>();
                row.Remove("user");   // identity is kept in the two fields; the rest is the detail
                rows.Add(new ClaudeUserActivityRow { UserId = userId, Email = email, MetricsJson = row.ToJsonString() });
            }

            var next = root["next_page"]?.GetValue<string>();
            if (string.IsNullOrEmpty(next))
            {
                _logger.LogDebug("Claude user activity for {Day}: {Count} active person(s) in {PageCount} page(s).", day, rows.Count, pageIndex + 1);
                return rows;
            }

            // A cursor that is only whitespace cannot be sent back and is not the end of the day.
            if (string.IsNullOrWhiteSpace(next))
            {
                throw new InvalidOperationException(
                    $"The Claude user activity for {day:yyyy-MM-dd} gave a blank next_page cursor; refusing to return a partial day.");
            }

            if (!seenCursors.Add(next))
            {
                throw new InvalidOperationException(
                    $"The Claude user activity for {day:yyyy-MM-dd} returned a cursor it had already returned; refusing to loop and returning no partial day.");
            }

            page = next;
        }

        throw new InvalidOperationException(
            $"The Claude user activity for {day:yyyy-MM-dd} was still paginating after {MaxPagesPerDay} pages; refusing to return a partial day.");
    }

    // The two 400s the API gives for a day it does not have, recognised by their text. Anything else is an error.
    private static bool IsNotAvailable(HttpResponseMessage response, string body) =>
        response.StatusCode == HttpStatusCode.BadRequest
        && (LatestAvailable().IsMatch(body) || body.Contains("Data is only available from", StringComparison.Ordinal));

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The Claude user activity returned {(int)response.StatusCode}. Check that the Analytics API key is valid and has the read:analytics scope.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Claude user activity returned 404. The analytics users endpoint needs a Claude Enterprise plan.");
        }

        response.EnsureSuccessStatusCode();
    }

    [GeneratedRegex(@"Latest available data for this query is \d{4}-\d{2}-\d{2}")]
    private static partial Regex LatestAvailable();
}
