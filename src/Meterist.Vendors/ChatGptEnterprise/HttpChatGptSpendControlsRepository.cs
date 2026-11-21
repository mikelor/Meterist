using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Http;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>Seam between the ChatGPT spend-limit source and the admin API's Spend Controls user list.</summary>
public interface IChatGptSpendControlsRepository
{
    /// <summary>
    /// Every member's effective monthly limit and usage, following the cursor to the end. Returns the complete list or
    /// throws: a silently truncated list would look like members with no limit.
    /// </summary>
    Task<IReadOnlyList<VendorSpendLimit>> ListUserLimitsAsync(ChatGptCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IChatGptSpendControlsRepository"/>, against
/// <c>GET /v1/manage/workspaces/{workspace_id}/usage_limits/users</c> (read-only; the write endpoints are deliberately
/// not used). Needs the <c>chatgpt.enterprise.usage_limit.read</c> scope. Live-tested 2026-11-20 against two tenants: the
/// admin key Meterist already stores is accepted, so no second credential is needed.
/// <para>
/// A row is <c>id</c>, <c>name</c>, <c>email</c>, <c>role</c>, <c>seat_type</c>, <c>current_month_usage</c> with
/// <c>current_month_usage_amount</c> <c>{amount, unit}</c>, <c>last_month_usage</c> (null on both tenants), and
/// <c>effective_monthly_usage_limit</c>, which is <b>null when the member has no limit</b> and otherwise
/// <c>{limit: {limit, type}, source: {kind, ...}}</c>. Usage and limits were in <b>credits</b> on both tenants (a
/// USD-billed workspace uses <c>usd</c>), and the unit is kept as the vendor states it. Pagination is
/// <c>has_more</c> plus an opaque <c>cursor</c> that is sent back as <c>cursor</c> (not the last id).
/// </para>
/// <para>
/// Usage that is missing is stored as missing, never as zero (the API says it does not convert unavailable usage to
/// zero). The limit kinds are <c>individual_override</c>, <c>group_default</c>, <c>workspace_default</c> and
/// <c>role_based_personal_budget</c>; the stored detail is the group name, seat type or role that the limit comes from.
/// See docs/vendor-integration-reference.md.
/// </para>
/// </summary>
public sealed class HttpChatGptSpendControlsRepository : IChatGptSpendControlsRepository
{
    private const int PageLimit = 500;

    // Backstop against a cursor loop. Reaching it throws rather than returning what was read so far.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    // Injected via RosterHttpClients.AddChatGptSpendControlsHttpClient: auto-redirect off (every request carries the
    // bearer token) and the factory's request logging removed (the cursor is opaque but may encode a member).
    private readonly HttpClient _client;
    private readonly ILogger<HttpChatGptSpendControlsRepository> _logger;

    public HttpChatGptSpendControlsRepository(HttpClient client, ILogger<HttpChatGptSpendControlsRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VendorSpendLimit>> ListUserLimitsAsync(
        ChatGptCredential credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.WorkspaceId))
        {
            throw new InvalidOperationException(
                "The ChatGPT Enterprise spend limits need the workspace UUID, but this credential has no WorkspaceId. "
                + "Add it and run 'credentials set' again (see docs/user-guide.md, ChatGPT Enterprise).");
        }

        var baseUrl = $"v1/manage/workspaces/{Uri.EscapeDataString(credential.WorkspaceId)}/usage_limits/users?limit={PageLimit}";
        var limits = new List<VendorSpendLimit>();
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = cursor is null ? baseUrl : $"{baseUrl}&cursor={Uri.EscapeDataString(cursor)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AdminApiKey);
            request.MarkSensitive();

            // Page number and counts only, never a URL or a row: rows name members and the cursor can encode one.
            _logger.LogDebug("Listing ChatGPT Enterprise spend limits, page {Page} (limit {Limit}).", pageIndex + 1, PageLimit);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<UserPageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("The ChatGPT Enterprise spend limits response deserialized to null.");

            foreach (var row in page.Data ?? [])
            {
                if (string.IsNullOrWhiteSpace(row.Id))
                {
                    throw new InvalidOperationException(
                        "The ChatGPT Enterprise spend limits held a row with no member id; refusing to store a partial result.");
                }

                // The same member twice (a page boundary shifting under the cursor) would double count them.
                if (!seenMembers.Add(row.Id))
                {
                    throw new InvalidOperationException(
                        "The ChatGPT Enterprise spend limits listed a member more than once; refusing to store an unreliable result.");
                }

                limits.Add(Map(row));
            }

            if (!page.HasMore)
            {
                _logger.LogDebug(
                    "The ChatGPT Enterprise spend limits returned {Count} member(s) in {PageCount} page(s).", limits.Count, pageIndex + 1);
                return limits;
            }

            // Checked before another request goes out: a blank cursor cannot be sent, and a repeated one means the
            // server is looping. Either way the list would be incomplete.
            if (string.IsNullOrWhiteSpace(page.Cursor))
            {
                throw new InvalidOperationException(
                    "The ChatGPT Enterprise spend limits reported has_more but gave a missing or blank cursor; "
                    + "refusing to store a partial result.");
            }

            if (!seenCursors.Add(page.Cursor))
            {
                throw new InvalidOperationException(
                    "The ChatGPT Enterprise spend limits returned a cursor it had already returned; "
                    + "refusing to loop and returning no partial result.");
            }

            cursor = page.Cursor;
        }

        throw new InvalidOperationException(
            $"The ChatGPT Enterprise spend limits were still paginating after {MaxPages} pages; refusing to return a partial result.");
    }

    private static VendorSpendLimit Map(UserLimitDto row)
    {
        // Native amount first (added 2026-09-25), falling back to the older number and its unit.
        var spent = row.CurrentMonthUsageAmount?.Amount ?? row.CurrentMonthUsage;
        var unit = row.CurrentMonthUsageAmount?.Unit ?? row.CurrentMonthUsageUnit;

        decimal? limit = null;
        string? source = null;
        string? detail = null;

        if (row.EffectiveLimit is { } effective)
        {
            switch (effective.Limit?.Type)
            {
                case "unlimited":
                    break;   // an explicit "no limit"

                case "limited":
                    limit = effective.Limit.LimitAmount?.Amount
                        ?? effective.Limit.Limit
                        ?? throw new InvalidOperationException(
                            "The ChatGPT Enterprise spend limits held a limited limit with no amount; refusing to store an unreadable figure.");
                    unit ??= effective.Limit.LimitAmount?.Unit;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"The ChatGPT Enterprise spend limits held a limit of an unknown type ({effective.Limit?.Type ?? "none"}); "
                        + "refusing to store a figure that could be misread.");
            }

            source = effective.Source?.Kind;
            detail = effective.Source?.GroupName ?? effective.Source?.GroupId ?? effective.Source?.SeatType
                ?? effective.Source?.RoleName ?? effective.Source?.RoleId;
        }

        return new VendorSpendLimit
        {
            MemberId = row.Id!,
            Email = row.Email,
            Name = row.Name,
            LimitAmount = limit,
            PeriodToDateSpend = spent,
            PriorPeriodSpend = row.LastMonthUsage,
            Currency = unit,
            Period = "monthly",
            SourceType = source,
            SourceDetail = detail,
        };
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A 403 is what a key gets for a workspace it does not belong to as well as for a missing permission.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"The ChatGPT Enterprise spend limits returned {(int)response.StatusCode}. Check that the admin key can read "
                + "usage limits (scope chatgpt.enterprise.usage_limit.read) and that WorkspaceId is the workspace this key belongs to.");
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class UserPageDto
    {
        [JsonPropertyName("data")]
        public List<UserLimitDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("cursor")]
        public string? Cursor { get; set; }
    }

    private sealed class UserLimitDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("current_month_usage")]
        public decimal? CurrentMonthUsage { get; set; }

        [JsonPropertyName("current_month_usage_unit")]
        public string? CurrentMonthUsageUnit { get; set; }

        [JsonPropertyName("current_month_usage_amount")]
        public AmountDto? CurrentMonthUsageAmount { get; set; }

        [JsonPropertyName("last_month_usage")]
        public decimal? LastMonthUsage { get; set; }

        [JsonPropertyName("effective_monthly_usage_limit")]
        public EffectiveLimitDto? EffectiveLimit { get; set; }
    }

    private sealed class AmountDto
    {
        [JsonPropertyName("amount")]
        public decimal? Amount { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }
    }

    private sealed class EffectiveLimitDto
    {
        [JsonPropertyName("limit")]
        public LimitDto? Limit { get; set; }

        [JsonPropertyName("source")]
        public SourceDto? Source { get; set; }
    }

    private sealed class LimitDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        // The older integer form (credits), deprecated in favour of limit_amount.
        [JsonPropertyName("limit")]
        public decimal? Limit { get; set; }

        [JsonPropertyName("limit_amount")]
        public AmountDto? LimitAmount { get; set; }
    }

    private sealed class SourceDto
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("group_id")]
        public string? GroupId { get; set; }

        [JsonPropertyName("group_name")]
        public string? GroupName { get; set; }

        [JsonPropertyName("seat_type")]
        public string? SeatType { get; set; }

        [JsonPropertyName("role_id")]
        public string? RoleId { get; set; }

        [JsonPropertyName("role_name")]
        public string? RoleName { get; set; }
    }
}
