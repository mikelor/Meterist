namespace Meterist.Core.Models;

/// <summary>
/// What one person did in the vendor's products on one day, as the vendor counts it: messages, Claude Code sessions and
/// commits, Cowork sessions, Office add-in use and so on. Natural key is (TenantId, VendorId, Date, UserId). A row exists
/// only for a person who did something that day. This is <b>activity</b>, not cost: on a seat-based plan a person can
/// be busy and cost nothing, and a message is not a model request (one message with tool use is several requests), so these
/// counts sit beside <see cref="UserDailyUsageRecord"/> and do not replace it.
/// <para>
/// The columns are the headline counts, enough to rank and chart; <see cref="DetailJson"/> keeps everything the vendor
/// returned for the person that day (every product's full metrics) so nothing is lost and a count promoted to a column
/// later can be rebuilt from it without calling the vendor again.
/// </para>
/// <para>
/// Holds email, which is personal data: keep it out of logs at Information level and above and out of anything
/// committed. A class with the default <c>ToString</c>, not a record, so logging an instance by accident prints a type name.
/// </para>
/// </summary>
public sealed class UserActivityRecord
{
    public required string TenantId { get; init; }

    public required Guid VendorId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>The vendor's stable user id, the same as the member id on the roster. Part of the natural key, so not an email.</summary>
    public required string UserId { get; init; }

    public string? UserEmail { get; init; }

    /// <summary>Chat messages sent.</summary>
    public long ChatMessages { get; init; }

    public long ChatConversations { get; init; }

    public long ClaudeCodeSessions { get; init; }

    public long ClaudeCodeCommits { get; init; }

    public long ClaudeCodePullRequests { get; init; }

    public long ClaudeCodeLinesAdded { get; init; }

    public long ClaudeCodeLinesRemoved { get; init; }

    public long CoworkSessions { get; init; }

    public long CoworkActions { get; init; }

    public long CoworkMessages { get; init; }

    /// <summary>Office add-in sessions, summed across Excel, PowerPoint, Word and Outlook.</summary>
    public long OfficeSessions { get; init; }

    public long OfficeMessages { get; init; }

    public long DesignMessages { get; init; }

    public long ScienceMessages { get; init; }

    public long WebSearches { get; init; }

    /// <summary>The vendor's whole record for the person that day, as JSON, without the user's identity (that is in the columns above).</summary>
    public string? DetailJson { get; init; }

    /// <summary>True when any headline count is above zero. A row can exist for a day the person only touched a product the columns do not cover.</summary>
    public bool HasHeadlineActivity =>
        ChatMessages > 0 || ChatConversations > 0 || ClaudeCodeSessions > 0 || CoworkSessions > 0 || CoworkMessages > 0
        || OfficeSessions > 0 || OfficeMessages > 0 || DesignMessages > 0 || ScienceMessages > 0 || WebSearches > 0
        || ClaudeCodeCommits > 0 || ClaudeCodePullRequests > 0 || CoworkActions > 0;
}
