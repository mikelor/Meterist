using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>
/// Real IChatGptCostLogRepository, against the OpenAI Programmatic Admin
/// Platform's COSTS compliance log export — see
/// docs/vendor-integration-reference.md for the verified endpoint/schema
/// detail (spiked 2026-07-22 against both the OpenAPI spec and a real
/// zelleri pull).
/// </summary>
public sealed class HttpChatGptCostLogRepository : IChatGptCostLogRepository
{
    // Compliance log files expire after a 30-day retention window — stay one
    // day inside it since "day" boundaries are approximate relative to the
    // exact retention cutoff.
    private const int RetentionDays = 29;

    private const int ListPageLimit = 100;

    // Backstop against an unexpected has_more=true loop that never terminates
    // (e.g. a vendor-side pagination bug) — not expected to ever trigger.
    private const int MaxListPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    // Injected by Program.cs via AddHttpClient<IChatGptCostLogRepository,
    // HttpChatGptCostLogRepository>(...).ConfigurePrimaryHttpMessageHandler(() =>
    // new HttpClientHandler { AllowAutoRedirect = false }) — auto-redirect MUST
    // stay off, or the download call below would silently follow the signed
    // URL itself and forward our Authorization header to whatever host issued it.
    private readonly HttpClient _authenticatedClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpChatGptCostLogRepository> _logger;

    public HttpChatGptCostLogRepository(
        HttpClient authenticatedClient,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpChatGptCostLogRepository> logger)
    {
        _authenticatedClient = authenticatedClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCostRowsAsync(
        ChatGptCredential credential, DateRange period, CancellationToken cancellationToken = default)
    {
        var earliestRetrievable = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-RetentionDays));
        var effectiveStart = period.Start;
        if (effectiveStart < earliestRetrievable)
        {
            _logger.LogWarning(
                "Requested ChatGPT Enterprise COSTS period start {RequestedStart} is older than the "
                + "{RetentionDays}-day compliance log retention window — clamping to {EffectiveStart}. "
                + "Older days will be missing from this pull.",
                period.Start, RetentionDays, earliestRetrievable);
            effectiveStart = earliestRetrievable;
        }

        var files = await ListFilesAsync(credential, effectiveStart, period.End, cancellationToken)
            .ConfigureAwait(false);

        var seenEventIds = new HashSet<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var file in files)
        {
            var events = await DownloadAndParseFileAsync(credential, file.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var costsEvent in events)
            {
                // "At least once" delivery — duplicates across files are expected.
                if (costsEvent.EventId is null || !seenEventIds.Add(costsEvent.EventId))
                {
                    continue;
                }

                if (costsEvent.Payload?.Day is null || !DateOnly.TryParse(costsEvent.Payload.Day, out var recordDate))
                {
                    continue;
                }

                // The list endpoint filters by file end_time (write time), not
                // event time, so individual events can fall outside the
                // requested window — filter precisely here.
                if (recordDate < period.Start || recordDate > period.End)
                {
                    continue;
                }

                foreach (var line in costsEvent.Payload.Measures?.Billing ?? [])
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        [ChatGptCostRowFields.RecordDate] = recordDate,
                        [ChatGptCostRowFields.Sku] = line.Sku,
                        [ChatGptCostRowFields.CostValue] = line.Cost?.Value ?? 0m,
                        [ChatGptCostRowFields.CostUnit] = line.Cost?.Unit,
                        [ChatGptCostRowFields.EstimatedCostUsdValue] = line.EstimatedCostUsd?.Value,
                        [ChatGptCostRowFields.UserId] = costsEvent.Payload.Identity?.UserId,
                        [ChatGptCostRowFields.UserEmail] = costsEvent.Payload.Identity?.Email,
                        [ChatGptCostRowFields.UserName] = costsEvent.Payload.Identity?.Name,
                        [ChatGptCostRowFields.EventId] = costsEvent.EventId,
                    });
                }
            }
        }

        _logger.LogDebug(
            "ChatGPT Enterprise COSTS export returned {FileCount} file(s), {EventCount} deduped event(s), "
            + "{RowCount} billing-line row(s) for {PeriodStart} to {PeriodEnd}.",
            files.Count, seenEventIds.Count, rows.Count, period.Start, period.End);

        return rows;
    }

    private async Task<List<ComplianceLogFileMetadataDto>> ListFilesAsync(
        ChatGptCredential credential, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var files = new List<ComplianceLogFileMetadataDto>();
        var after = FormatTimestamp(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var before = FormatTimestamp(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        for (var page = 0; page < MaxListPages; page++)
        {
            var url = $"v1/compliance/organizations/{credential.OrganizationId}/logs"
                + $"?event_type=COSTS&after={Uri.EscapeDataString(after)}&before={Uri.EscapeDataString(before)}"
                + $"&limit={ListPageLimit}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AdminApiKey);

            _logger.LogDebug("Listing ChatGPT Enterprise COSTS files: {Url}", url);

            using var response = await _authenticatedClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<ComplianceLogFileListDto>(body, JsonOptions)
                ?? throw new InvalidOperationException(
                    "ChatGPT Enterprise COSTS file list response deserialized to null.");

            files.AddRange(list.Data ?? []);

            if (!list.HasMore || list.LastEndTime is null)
            {
                break;
            }

            after = list.LastEndTime;
        }

        return files;
    }

    private async Task<List<ChatGptCostsEventDto>> DownloadAndParseFileAsync(
        ChatGptCredential credential, string logFileId, CancellationToken cancellationToken)
    {
        var url = $"v1/compliance/organizations/{credential.OrganizationId}/logs/{logFileId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AdminApiKey);

        using var response = await _authenticatedClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.TemporaryRedirect || response.Headers.Location is null)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException(
                $"Expected a 307 redirect with a Location header when downloading ChatGPT Enterprise "
                + $"COSTS file '{logFileId}', got {(int)response.StatusCode}.");
        }

        var signedUrl = response.Headers.Location;

        // A fresh, unauthenticated client — the signed URL is pre-authenticated
        // and the admin bearer token has no business being sent to whatever
        // host issued it.
        var anonymousClient = _httpClientFactory.CreateClient();
        using var fileResponse = await anonymousClient.GetAsync(signedUrl, cancellationToken).ConfigureAwait(false);
        fileResponse.EnsureSuccessStatusCode();

        var body = await fileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<ChatGptCostsEventDto>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var costsEvent = JsonSerializer.Deserialize<ChatGptCostsEventDto>(line, JsonOptions);
            if (costsEvent is not null)
            {
                events.Add(costsEvent);
            }
        }

        return events;
    }

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private sealed class ComplianceLogFileListDto
    {
        [JsonPropertyName("data")]
        public List<ComplianceLogFileMetadataDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("last_end_time")]
        public string? LastEndTime { get; set; }
    }

    private sealed class ComplianceLogFileMetadataDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ChatGptCostsEventDto
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("payload")]
        public ChatGptCostsPayloadDto? Payload { get; set; }
    }

    private sealed class ChatGptCostsPayloadDto
    {
        [JsonPropertyName("day")]
        public string? Day { get; set; }

        [JsonPropertyName("identity")]
        public ChatGptCostsIdentityDto? Identity { get; set; }

        [JsonPropertyName("measures")]
        public ChatGptCostsMeasuresDto? Measures { get; set; }
    }

    private sealed class ChatGptCostsIdentityDto
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ChatGptCostsMeasuresDto
    {
        [JsonPropertyName("billing")]
        public List<ChatGptCostsBillingLineDto>? Billing { get; set; }
    }

    private sealed class ChatGptCostsBillingLineDto
    {
        [JsonPropertyName("sku")]
        public string? Sku { get; set; }

        [JsonPropertyName("cost")]
        public ChatGptCostsAmountDto? Cost { get; set; }

        [JsonPropertyName("estimated_cost_usd")]
        public ChatGptCostsAmountDto? EstimatedCostUsd { get; set; }
    }

    private sealed class ChatGptCostsAmountDto
    {
        [JsonPropertyName("value")]
        public decimal Value { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }
    }
}
