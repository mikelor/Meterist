using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>Seam between the Claude spend-limit source and <c>GET /v1/organizations/spend_limits/effective</c>.</summary>
public interface IClaudeSpendLimitRepository
{
    /// <summary>
    /// Every current member's effective limit, following the cursor to the end. Returns the complete list or throws:
    /// a silently truncated list would look like members with no limit.
    /// </summary>
    Task<IReadOnlyList<VendorSpendLimit>> ListEffectiveLimitsAsync(string apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IClaudeSpendLimitRepository"/> (Claude Enterprise only). Needs <c>read:spend_limits</c>.
/// Live-tested 2026-11-20 against two tenants: the Analytics key Meterist already stores for each tenant carries that
/// scope together with <c>read:analytics</c> and <c>read:members</c>, so no second credential is needed, and the
/// endpoint needs no beta header. A row is <c>scope</c> (<c>user</c> and <c>user_id</c>), <c>actor</c> (<c>user_id</c>,
/// <c>name</c>, <c>email_address</c>), <c>amount</c>, <c>currency</c>, <c>period</c>, <c>source</c>,
/// <c>spend_limit_id</c> and <c>period_to_date_spend</c>. <b>Both <c>amount</c> and <c>period_to_date_spend</c> are
/// decimal strings in cents</b> (the sum of the latter equalled the cost report in cents for one tenant), so they are
/// divided by 100. A null <c>amount</c> means the member has no limit. See docs/vendor-integration-reference.md.
/// </summary>
public sealed class HttpClaudeSpendLimitRepository : IClaudeSpendLimitRepository
{
    private const string AnthropicVersion = "2023-06-01";
    private const int PageLimit = 1000;

    // Backstop against a cursor loop. Reaching it throws rather than returning what was read so far.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeSpendLimitRepository> _logger;

    public HttpClaudeSpendLimitRepository(HttpClient client, ILogger<HttpClaudeSpendLimitRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VendorSpendLimit>> ListEffectiveLimitsAsync(
        string apiKey, CancellationToken cancellationToken = default)
    {
        var limits = new List<VendorSpendLimit>();
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = $"v1/organizations/spend_limits/effective?limit={PageLimit}"
                + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", apiKey);

            // Page number and counts only, never a URL or a row: rows name members.
            _logger.LogDebug("Listing Claude spend limits, page {Page} (limit {Limit}).", pageIndex + 1, PageLimit);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<LimitPageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("The Claude spend limits response deserialized to null.");

            foreach (var row in report.Data ?? [])
            {
                var memberId = row.Actor?.UserId ?? row.Scope?.UserId;
                if (string.IsNullOrWhiteSpace(memberId))
                {
                    throw new InvalidOperationException(
                        "The Claude spend limits response held a row with no member id; refusing to store a partial result.");
                }

                // The same member twice (a page boundary shifting under the cursor) would double count them.
                if (!seenMembers.Add(memberId))
                {
                    throw new InvalidOperationException(
                        "The Claude spend limits response listed a member more than once; refusing to store an unreliable result.");
                }

                limits.Add(new VendorSpendLimit
                {
                    MemberId = memberId,
                    Email = row.Actor?.EmailAddress,
                    Name = row.Actor?.Name,
                    LimitAmount = CentsToDollars(row.Amount, "amount"),
                    PeriodToDateSpend = CentsToDollars(row.PeriodToDateSpend, "period_to_date_spend"),
                    Currency = row.Currency,
                    Period = row.Period,
                    SourceType = row.Source?.Type,
                    // The limit's origin: the seat tier or group it is inherited from. A per-member limit has none.
                    SourceDetail = row.Source?.SeatTier ?? row.Source?.RbacGroupId,
                    SpendLimitId = row.SpendLimitId,
                });
            }

            if (string.IsNullOrEmpty(report.NextPage))
            {
                _logger.LogDebug(
                    "The Claude spend limits returned {Count} member(s) in {PageCount} page(s).", limits.Count, pageIndex + 1);
                return limits;
            }

            // A cursor that is only whitespace is not the end of the list and cannot be sent back: stop here rather
            // than make another request with an unusable page parameter.
            if (string.IsNullOrWhiteSpace(report.NextPage))
            {
                throw new InvalidOperationException(
                    "The Claude spend limits gave a blank next_page cursor; refusing to store a partial result.");
            }

            if (!seenCursors.Add(report.NextPage))
            {
                throw new InvalidOperationException(
                    "The Claude spend limits returned a next_page cursor it had already returned; refusing to loop and returning no partial result.");
            }

            page = report.NextPage;
        }

        throw new InvalidOperationException(
            $"The Claude spend limits were still paginating after {MaxPages} pages; refusing to return a partial result.");
    }

    // A decimal string in cents; null (or absent) is null, not zero: for the amount it means "no limit".
    private static decimal? CentsToDollars(string? cents, string field)
    {
        if (cents is null)
        {
            return null;
        }

        if (!decimal.TryParse(cents, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"The Claude spend limits field {field} was not a number; refusing to store an unreadable figure.");
        }

        return value / 100m;
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The Claude spend limits returned {(int)response.StatusCode}. Check that the key has the read:spend_limits scope "
                + "and that usage credits are turned on for the organisation.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Claude spend limits returned 404. The Spend Limits API is for Claude Enterprise organisations only.");
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class LimitPageDto
    {
        [JsonPropertyName("data")]
        public List<LimitDto>? Data { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    private sealed class LimitDto
    {
        [JsonPropertyName("scope")]
        public ScopeDto? Scope { get; set; }

        [JsonPropertyName("actor")]
        public ActorDto? Actor { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("period")]
        public string? Period { get; set; }

        [JsonPropertyName("source")]
        public SourceDto? Source { get; set; }

        [JsonPropertyName("spend_limit_id")]
        public string? SpendLimitId { get; set; }

        [JsonPropertyName("period_to_date_spend")]
        public string? PeriodToDateSpend { get; set; }
    }

    private sealed class ScopeDto
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

    private sealed class ActorDto
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email_address")]
        public string? EmailAddress { get; set; }
    }

    private sealed class SourceDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("seat_tier")]
        public string? SeatTier { get; set; }

        [JsonPropertyName("rbac_group_id")]
        public string? RbacGroupId { get; set; }
    }
}
