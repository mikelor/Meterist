using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Real IChatGptDailyUsageRepository, against the ChatGPT admin API's Daily
/// Usage endpoint (GA 2026-09-17). Unlike the COSTS compliance-log export it
/// is a plain query API with no 30-day file expiry, so it can return days the
/// export has already lost. Needs the admin key's "Enterprise analytics API:
/// Read" permission and the workspace UUID (not the org-... Organization ID).
/// See docs/vendor-integration-reference.md.
/// </summary>
public sealed class HttpChatGptDailyUsageRepository : IChatGptDailyUsageRepository
{
    private const int PageLimit = 1000;

    // Backstop against a pagination loop that never terminates. Reaching it
    // throws rather than returning what was read so far: a silently truncated
    // result would look like days with no usage.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    // Injected by Program.cs via AddHttpClient with auto-redirect off: this
    // client sends a bearer token per request, and a redirect has no business
    // carrying it anywhere else.
    private readonly HttpClient _client;
    private readonly ILogger<HttpChatGptDailyUsageRepository> _logger;

    public HttpChatGptDailyUsageRepository(HttpClient client, ILogger<HttpChatGptDailyUsageRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChatGptDailyUsageRow>> QueryDailyUsageAsync(
        ChatGptCredential credential,
        DateRange period,
        ChatGptUsageGrouping grouping,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.WorkspaceId))
        {
            throw new InvalidOperationException(
                "The ChatGPT Daily Usage API needs the workspace UUID, but this credential has no WorkspaceId. "
                + "Add it and run 'credentials set' again (see docs/user-guide.md, ChatGPT Enterprise).");
        }

        // start_time is inclusive and end_time exclusive, both read as UTC
        // calendar dates, so the inclusive period end becomes the next midnight.
        var startTime = ToUnixSeconds(period.Start);
        var endTime = ToUnixSeconds(period.End.AddDays(1));

        var baseUrl = $"v1/analytics/workspaces/{Uri.EscapeDataString(credential.WorkspaceId)}/usage"
            + $"?start_time={startTime}&end_time={endTime}&limit={PageLimit}"
            + (grouping == ChatGptUsageGrouping.Workspace ? "&group=workspace" : string.Empty);

        var rows = new List<ChatGptDailyUsageRow>();
        string? page = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            var url = page is null ? baseUrl : $"{baseUrl}&page={Uri.EscapeDataString(page)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AdminApiKey);

            _logger.LogDebug("Querying ChatGPT Enterprise Daily Usage ({Grouping}): {Url}", grouping, baseUrl);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<UsagePageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("ChatGPT Enterprise Daily Usage response deserialized to null.");

            foreach (var dto in result.Data ?? [])
            {
                rows.Add(ToRow(dto));
            }

            if (!result.HasMore)
            {
                _logger.LogDebug(
                    "ChatGPT Enterprise Daily Usage ({Grouping}) returned {RowCount} row(s) in {PageCount} page(s) "
                    + "for {PeriodStart} to {PeriodEnd}.",
                    grouping, rows.Count, pageNumber + 1, period.Start, period.End);
                return rows;
            }

            // All three failures below are checked before another request goes
            // out: a blank cursor would be sent as an empty page parameter, and
            // a cursor the server has already handed us means it is looping.
            // Either way the result would be incomplete or never end, so fail
            // now instead of after MaxPages wasted calls.
            if (string.IsNullOrWhiteSpace(result.NextPage))
            {
                throw new InvalidOperationException(
                    "ChatGPT Enterprise Daily Usage reported has_more but gave a missing or blank next_page cursor.");
            }

            if (!seenCursors.Add(result.NextPage))
            {
                throw new InvalidOperationException(
                    "ChatGPT Enterprise Daily Usage returned a next_page cursor it had already returned; "
                    + "refusing to loop and returning no partial result.");
            }

            page = result.NextPage;
        }

        throw new InvalidOperationException(
            $"ChatGPT Enterprise Daily Usage was still paginating after {MaxPages} pages; refusing to return a partial result.");
    }

    private static long ToUnixSeconds(DateOnly utcDate) =>
        new DateTimeOffset(utcDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Worth spelling out: a 403 is what a key gets for a workspace it does
        // not belong to as well as for a missing permission, and the response
        // body says nothing more specific than "Forbidden".
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"ChatGPT Enterprise Daily Usage returned {(int)response.StatusCode}. Check that the admin key has "
                + "\"Enterprise analytics API: Read\" and that WorkspaceId is the workspace this key belongs to.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static ChatGptDailyUsageRow ToRow(UsageRowDto dto)
    {
        var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(dto.StartTime).UtcDateTime);
        var totals = dto.Totals ?? new UsageTotalsDto();

        return new ChatGptDailyUsageRow(
            day,
            dto.UserId,
            dto.Actor?.Email,
            totals.Chats,
            totals.Messages,
            totals.Credits,
            totals.CostUsd,
            totals.EstimatedCostUsd,
            totals.UncachedTextInputTokens,
            totals.CachedTextInputTokens,
            totals.TextOutputTokens,
            totals.TextTotalTokens);
    }

    private sealed class UsagePageDto
    {
        [JsonPropertyName("data")]
        public List<UsageRowDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class UsageRowDto
    {
        [JsonPropertyName("start_time")]
        public long StartTime { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("actor")]
        public ActorDto? Actor { get; set; }

        [JsonPropertyName("totals")]
        public UsageTotalsDto? Totals { get; set; }
    }

    private sealed class ActorDto
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    private sealed class UsageTotalsDto
    {
        [JsonPropertyName("chats")]
        public int Chats { get; set; }

        [JsonPropertyName("messages")]
        public int Messages { get; set; }

        [JsonPropertyName("credits")]
        public decimal? Credits { get; set; }

        [JsonPropertyName("cost_usd")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("estimated_cost_usd")]
        public decimal? EstimatedCostUsd { get; set; }

        [JsonPropertyName("uncached_text_input_tokens")]
        public long? UncachedTextInputTokens { get; set; }

        [JsonPropertyName("cached_text_input_tokens")]
        public long? CachedTextInputTokens { get; set; }

        [JsonPropertyName("text_output_tokens")]
        public long? TextOutputTokens { get; set; }

        [JsonPropertyName("text_total_tokens")]
        public long? TextTotalTokens { get; set; }
    }
}
