using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>Seam between the Claude Enterprise roster source and the Admin API's member list.</summary>
public interface IClaudeMemberRosterRepository
{
    /// <summary>
    /// Every member of the organisation the key belongs to, following the cursor to the end. Returns the complete
    /// list or throws: a silently truncated list would look like people having left.
    /// </summary>
    Task<IReadOnlyList<VendorMember>> ListMembersAsync(string apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IClaudeMemberRosterRepository"/>, against <c>GET /v1/organizations/users</c> (the Admin API's
/// user management, out of beta 2026-08-19). Needs a key with <c>read:members</c> (or <c>read:org_audit</c>).
/// Live-tested 2026-11-20: the Analytics key Meterist already stores for each tenant is accepted by this endpoint,
/// so no second credential is needed, and a member object is <c>id</c>, <c>type</c>, <c>email</c>, <c>name</c>,
/// <c>role</c> and <c>added_at</c>. See docs/vendor-integration-reference.md.
/// </summary>
public sealed class HttpClaudeMemberRosterRepository : IClaudeMemberRosterRepository
{
    private const string AnthropicVersion = "2023-06-01";
    private const int PageLimit = 1000;

    // Backstop against a cursor loop. Reaching it throws rather than returning what was read so far.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly ILogger<HttpClaudeMemberRosterRepository> _logger;

    public HttpClaudeMemberRosterRepository(HttpClient client, ILogger<HttpClaudeMemberRosterRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VendorMember>> ListMembersAsync(
        string apiKey, CancellationToken cancellationToken = default)
    {
        var members = new List<VendorMember>();
        string? after = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = $"v1/organizations/users?limit={PageLimit}"
                + (after is null ? string.Empty : $"&after_id={Uri.EscapeDataString(after)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("x-api-key", apiKey);

            // Deliberately not the URL: after the first page it carries a cursor, which is a member id.
            _logger.LogDebug("Listing Claude Enterprise members, page {Page} (limit {Limit}).", pageIndex + 1, PageLimit);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<MemberPageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("The Claude Enterprise member list deserialized to null.");

            foreach (var dto in page.Data ?? [])
            {
                // A member with no id cannot be keyed or compared across snapshots, and silently skipping one
                // would make the roster smaller than the organisation, so it is an error.
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    throw new InvalidOperationException(
                        "The Claude Enterprise member list contained a member with no id; refusing to store a partial roster.");
                }

                members.Add(new VendorMember
                {
                    Id = dto.Id,
                    Email = dto.Email,
                    Name = dto.Name,
                    Role = dto.Role,
                    AddedAt = DateTimeOffset.TryParse(
                        dto.AddedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var added)
                        ? added.ToUniversalTime()
                        : null,
                });
            }

            if (!page.HasMore)
            {
                // Counts only: the list is personal data.
                _logger.LogDebug("The Claude Enterprise member list returned {MemberCount} member(s) in {PageCount} page(s).", members.Count, pageIndex + 1);
                return members;
            }

            // Checked before another request goes out: a blank cursor would be sent as an empty parameter, and a
            // repeated one means the server is looping. Either way the list would be incomplete.
            if (string.IsNullOrWhiteSpace(page.LastId))
            {
                throw new InvalidOperationException(
                    "The Claude Enterprise member list reported has_more but gave a missing or blank last_id cursor; "
                    + "refusing to store a partial roster.");
            }

            if (!seenCursors.Add(page.LastId))
            {
                throw new InvalidOperationException(
                    "The Claude Enterprise member list returned a cursor it had already returned; "
                    + "refusing to loop and returning no partial roster.");
            }

            after = page.LastId;
        }

        throw new InvalidOperationException(
            $"The Claude Enterprise member list was still paginating after {MaxPages} pages; refusing to return a partial roster.");
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
                $"The Claude Enterprise member list returned {(int)response.StatusCode}. Check that the stored key is valid "
                + "and carries the read:members scope (or read:org_audit); the Analytics key used for spend may not.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Claude Enterprise member list returned 404. User management is an Enterprise feature; check that "
                + "this tenant's key belongs to a Claude Enterprise organisation.");
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class MemberPageDto
    {
        [JsonPropertyName("data")]
        public List<MemberDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("last_id")]
        public string? LastId { get; set; }
    }

    private sealed class MemberDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("added_at")]
        public string? AddedAt { get; set; }
    }
}
