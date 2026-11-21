using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.ChatGptEnterprise;

/// <summary>Seam between the ChatGPT roster source and the admin API's workspace user list.</summary>
public interface IChatGptWorkspaceUserRepository
{
    /// <summary>
    /// Every active member of the workspace, following the cursor to the end. Returns the complete list or throws:
    /// a silently truncated list would look like people having left.
    /// </summary>
    Task<IReadOnlyList<VendorMember>> ListUsersAsync(ChatGptCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real <see cref="IChatGptWorkspaceUserRepository"/>, against <c>GET /v1/manage/workspaces/{workspace_id}/users</c>
/// (added 2026-08-21). The list is of <b>active members only</b> and excludes service accounts, and it is eventually
/// consistent. Live-tested 2026-11-20: the admin key Meterist already stores for each tenant is accepted, so no
/// second credential is needed, and a member is <c>id</c>, <c>email</c>, <c>name</c>, <c>role</c>, <c>seat_type</c>
/// and <c>is_scim_managed</c>; there is no join date and no last-active field. Pagination is <c>after=</c> the
/// previous page's <c>last_id</c>. See docs/vendor-integration-reference.md.
/// </summary>
public sealed class HttpChatGptWorkspaceUserRepository : IChatGptWorkspaceUserRepository
{
    private const int PageLimit = 1000;

    // Backstop against a cursor loop. Reaching it throws rather than returning what was read so far.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    // Injected by Program.cs via AddHttpClient with auto-redirect off: this client sends a bearer token on every
    // request, and a redirect has no business carrying it anywhere else.
    private readonly HttpClient _client;
    private readonly ILogger<HttpChatGptWorkspaceUserRepository> _logger;

    public HttpChatGptWorkspaceUserRepository(HttpClient client, ILogger<HttpChatGptWorkspaceUserRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VendorMember>> ListUsersAsync(
        ChatGptCredential credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.WorkspaceId))
        {
            throw new InvalidOperationException(
                "The ChatGPT Enterprise member list needs the workspace UUID, but this credential has no WorkspaceId. "
                + "Add it and run 'credentials set' again (see docs/user-guide.md, ChatGPT Enterprise).");
        }

        var baseUrl = $"v1/manage/workspaces/{Uri.EscapeDataString(credential.WorkspaceId)}/users?limit={PageLimit}";
        var members = new List<VendorMember>();
        string? after = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = after is null ? baseUrl : $"{baseUrl}&after={Uri.EscapeDataString(after)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AdminApiKey);

            // Deliberately not the URL: after the first page it carries a cursor, which is a member id.
            _logger.LogDebug("Listing ChatGPT Enterprise workspace users, page {Page} (limit {Limit}).", pageIndex + 1, PageLimit);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<UserPageDto>(body, JsonOptions)
                ?? throw new InvalidOperationException("The ChatGPT Enterprise member list deserialized to null.");

            foreach (var dto in page.Data ?? [])
            {
                // A member with no id cannot be keyed or compared across snapshots, and silently skipping one would
                // make the roster smaller than the workspace, so it is an error.
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    throw new InvalidOperationException(
                        "The ChatGPT Enterprise member list contained a member with no id; refusing to store a partial roster.");
                }

                members.Add(new VendorMember
                {
                    Id = dto.Id,
                    Email = dto.Email,
                    Name = dto.Name,
                    Role = dto.Role,
                    SeatType = dto.SeatType,
                    IsScimManaged = dto.IsScimManaged,
                });
            }

            if (!page.HasMore)
            {
                // Counts only: the list is personal data.
                _logger.LogDebug(
                    "The ChatGPT Enterprise member list returned {MemberCount} member(s) in {PageCount} page(s).",
                    members.Count, pageIndex + 1);
                return members;
            }

            // Checked before another request goes out: a blank cursor would be sent as an empty parameter, and a
            // repeated one means the server is looping. Either way the list would be incomplete.
            if (string.IsNullOrWhiteSpace(page.LastId))
            {
                throw new InvalidOperationException(
                    "The ChatGPT Enterprise member list reported has_more but gave a missing or blank last_id cursor; "
                    + "refusing to store a partial roster.");
            }

            if (!seenCursors.Add(page.LastId))
            {
                throw new InvalidOperationException(
                    "The ChatGPT Enterprise member list returned a cursor it had already returned; "
                    + "refusing to loop and returning no partial roster.");
            }

            after = page.LastId;
        }

        throw new InvalidOperationException(
            $"The ChatGPT Enterprise member list was still paginating after {MaxPages} pages; refusing to return a partial roster.");
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A 403 is what a key gets for a workspace it does not belong to as well as for a missing permission, and the
        // body says nothing more specific.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"The ChatGPT Enterprise member list returned {(int)response.StatusCode}. Check that the admin key can read "
                + "workspace users (scope chatgpt.enterprise.user.read) and that WorkspaceId is the workspace this key belongs to.");
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class UserPageDto
    {
        [JsonPropertyName("data")]
        public List<UserDto>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("last_id")]
        public string? LastId { get; set; }
    }

    private sealed class UserDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("seat_type")]
        public string? SeatType { get; set; }

        [JsonPropertyName("is_scim_managed")]
        public bool? IsScimManaged { get; set; }
    }
}
