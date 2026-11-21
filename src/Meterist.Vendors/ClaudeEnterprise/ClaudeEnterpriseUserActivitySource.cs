using System.Text.Json;
using Meterist.Core.Models;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Claude Enterprise's per-person activity by day from the Analytics API's users endpoint, read with the same Analytics key the
/// tenant's spend extraction uses. A missing credential is a configuration problem, so it fails loudly rather than reading as
/// no activity. One request per requested day (days before 2026-01-01 and days not yet published are skipped, not errors), and
/// the caller decides which days are worth asking for, so a daily run asks for a handful rather than the whole year.
/// <para>
/// Each person-day becomes one <see cref="UserActivityRecord"/>: the headline counts as columns, and the vendor's whole row (every
/// product's metrics) as <see cref="UserActivityRecord.DetailJson"/>.
/// </para>
/// </summary>
public sealed class ClaudeEnterpriseUserActivitySource : IVendorUserActivitySource
{
    // The API's data begins here; earlier days are a 400.
    private static readonly DateOnly EarliestDay = new(2026, 1, 1);

    private readonly ISecretStore _secretStore;
    private readonly IClaudeUserActivityRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ClaudeEnterpriseUserActivitySource(
        ISecretStore secretStore, IClaudeUserActivityRepository repository, TimeProvider? timeProvider = null)
    {
        _secretStore = secretStore;
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid VendorId => VendorCatalog.ClaudeEnterprise.Id;

    public async Task<UserActivityFetch> FetchAsync(
        string tenantId, IReadOnlyCollection<DateOnly> days, CancellationToken cancellationToken = default)
    {
        var apiKey = await _secretStore.GetCredentialAsync(tenantId, VendorId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No Claude Enterprise credential configured for tenant '{tenantId}'.");
        }

        var newest = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime).AddDays(-1);
        var records = new List<UserActivityRecord>();
        var covered = new List<DateOnly>();

        foreach (var day in days.Where(d => d >= EarliestDay && d <= newest).Distinct().Order())
        {
            var rows = await _repository.QueryDayAsync(apiKey, day, cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                continue;   // not published yet: not looked at, so whatever is stored for it stays
            }

            covered.Add(day);
            records.AddRange(rows.Select(row => Map(tenantId, VendorId, day, row)));
        }

        return new UserActivityFetch(records, covered);
    }

    /// <summary>Turns an API row into a stored record. Public so the mapping can be tested without HTTP.</summary>
    public static UserActivityRecord Map(string tenantId, Guid vendorId, DateOnly date, ClaudeUserActivityRow row)
    {
        using var doc = JsonDocument.Parse(row.MetricsJson);
        var m = doc.RootElement;

        long Office(string metric) =>
            new[] { "excel", "powerpoint", "word", "outlook" }.Sum(app => Count(m, "office_metrics", app, metric));

        return new UserActivityRecord
        {
            TenantId = tenantId,
            VendorId = vendorId,
            Date = date,
            UserId = row.UserId,
            UserEmail = row.Email,
            ChatMessages = Count(m, "chat_metrics", "message_count"),
            ChatConversations = Count(m, "chat_metrics", "distinct_conversation_count"),
            ClaudeCodeSessions = Count(m, "claude_code_metrics", "core_metrics", "distinct_session_count"),
            ClaudeCodeCommits = Count(m, "claude_code_metrics", "core_metrics", "commit_count"),
            ClaudeCodePullRequests = Count(m, "claude_code_metrics", "core_metrics", "pull_request_count"),
            ClaudeCodeLinesAdded = Count(m, "claude_code_metrics", "core_metrics", "lines_of_code", "added_count"),
            ClaudeCodeLinesRemoved = Count(m, "claude_code_metrics", "core_metrics", "lines_of_code", "removed_count"),
            CoworkSessions = Count(m, "cowork_metrics", "distinct_session_count"),
            CoworkActions = Count(m, "cowork_metrics", "action_count"),
            CoworkMessages = Count(m, "cowork_metrics", "message_count"),
            OfficeSessions = Office("distinct_session_count"),
            OfficeMessages = Office("message_count"),
            DesignMessages = Count(m, "design_metrics", "message_count"),
            ScienceMessages = Count(m, "science_metrics", "message_count"),
            WebSearches = Count(m, "web_search_count"),
            DetailJson = row.MetricsJson,
        };
    }

    // A whole number at a path, or zero when the product, the metric or the value is absent or null: a person who did not
    // touch a product has no object for it, which is zero activity there, not an unknown. A fractional or negative count is
    // not a count and is refused rather than truncated.
    private static long Count(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return 0;
            }
        }

        if (current.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (current.ValueKind != JsonValueKind.Number || !current.TryGetInt64(out var value) || value < 0)
        {
            throw new InvalidOperationException(
                $"The Claude user activity field {string.Join('.', path)} is not a whole, non-negative count; refusing to store a figure that would be altered.");
        }

        return value;
    }
}
