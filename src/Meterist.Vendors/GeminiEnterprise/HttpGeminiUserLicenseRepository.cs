using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Meterist.Core.Http;
using Meterist.Core.Vendors;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Real <see cref="IGeminiUserLicenseRepository"/>, against Discovery Engine's
/// <c>GET /v1/projects/{project}/locations/{location}/userStores/{userStore}/userLicenses</c> (GA v1), with the tenant's
/// service account. A record is <c>userPrincipal</c> (the email, and the only identifier), <c>userProfile</c> (the full name),
/// <c>licenseAssignmentState</c> (<c>ASSIGNED</c>, <c>NO_LICENSE</c>, <c>NO_LICENSE_ATTEMPTED_LOGIN</c>, <c>BLOCKED</c>,
/// <c>UNASSIGNED</c>), <c>licenseConfig</c>, <c>createTime</c>, <c>updateTime</c> and <c>lastLoginTime</c> (empty until the
/// first login). See docs/vendor-integration-reference.md; built from Google's reference, not yet read from a live tenant.
/// <para>
/// Pages are 1000 records, followed by <c>nextPageToken</c>. Every request is marked sensitive and the page token is never
/// logged, since a token for a list of people could encode one. The licence state is kept as the member's seat type, whatever
/// it is, so a state Google adds later is stored rather than dropped; the last-login time is not stored (the roster has no
/// column for it yet).
/// </para>
/// </summary>
public sealed partial class HttpGeminiUserLicenseRepository : IGeminiUserLicenseRepository
{
    private const int PageSize = 1000;

    // Backstop against a token loop. Reaching it throws rather than returning what was read so far.
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _client;
    private readonly IGeminiAccessTokenProvider _tokens;
    private readonly ILogger<HttpGeminiUserLicenseRepository> _logger;

    public HttpGeminiUserLicenseRepository(
        HttpClient client, IGeminiAccessTokenProvider tokens, ILogger<HttpGeminiUserLicenseRepository> logger)
    {
        _client = client;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VendorMember>> ListUserLicensesAsync(
        GeminiCredential credential, CancellationToken cancellationToken = default)
    {
        var project = RequireSafe(credential.DiscoveryEngineProjectId, nameof(GeminiCredential.DiscoveryEngineProjectId));
        var location = RequireSafe(credential.DiscoveryEngineLocation, nameof(GeminiCredential.DiscoveryEngineLocation));
        var userStore = RequireSafe(credential.UserStoreId, nameof(GeminiCredential.UserStoreId));

        var path = $"v1/projects/{project}/locations/{location}/userStores/{userStore}/userLicenses?pageSize={PageSize}";
        var baseAddress = _client.BaseAddress ?? new Uri(HostFor(location));

        var token = await _tokens.GetAccessTokenAsync(credential, cancellationToken).ConfigureAwait(false);

        var members = new List<VendorMember>();
        string? pageToken = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var url = new Uri(baseAddress, path + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.MarkSensitive();

            // Deliberately not the URL: after the first page it carries a page token.
            _logger.LogDebug("Listing Gemini Enterprise user licences, page {Page}.", pageIndex + 1);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ThrowIfNotSuccess(response);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ListResponseDto page;
            try
            {
                page = JsonSerializer.Deserialize<ListResponseDto>(body, JsonOptions)
                    ?? throw new InvalidOperationException("The Gemini user licence list response was empty.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The Gemini user licence list response was not a valid list; refusing to store a partial roster.");
            }

            // An empty page leaves out the array (protobuf JSON omits an empty list), so a missing array is no records, not an error.
            foreach (var licence in page.UserLicenses ?? [])
            {
                members.Add(ToMember(licence));
            }

            if (string.IsNullOrEmpty(page.NextPageToken))
            {
                return members;
            }

            if (string.IsNullOrWhiteSpace(page.NextPageToken))
            {
                throw new InvalidOperationException("The Gemini user licence list gave a blank page token; refusing to return a partial roster.");
            }

            if (!seenTokens.Add(page.NextPageToken))
            {
                throw new InvalidOperationException("The Gemini user licence list repeated a page token; refusing to loop.");
            }

            pageToken = page.NextPageToken;
        }

        throw new InvalidOperationException($"The Gemini user licence list was still paginating after {MaxPages} pages; refusing to return a partial roster.");
    }

    /// <summary>The API host for a location: <c>global</c> is the plain host, a region is prefixed.</summary>
    public static string HostFor(string location) =>
        string.Equals(location, "global", StringComparison.OrdinalIgnoreCase)
            ? "https://discoveryengine.googleapis.com/"
            : string.Create(CultureInfo.InvariantCulture, $"https://{location.ToLowerInvariant()}-discoveryengine.googleapis.com/");

    private static VendorMember ToMember(UserLicenseDto licence)
    {
        if (licence is null)
        {
            throw new InvalidOperationException("A Gemini user licence list entry was null; refusing to store a partial roster.");
        }

        // The principal is the only identifier there is. A record without one cannot be keyed, so the list is not trustworthy.
        if (string.IsNullOrWhiteSpace(licence.UserPrincipal))
        {
            throw new InvalidOperationException("A Gemini user licence record has no userPrincipal; refusing to store a partial roster.");
        }

        var principal = licence.UserPrincipal.Trim();
        return new VendorMember
        {
            Id = principal,
            Email = principal.Contains('@', StringComparison.Ordinal) ? principal : null,
            Name = string.IsNullOrWhiteSpace(licence.UserProfile) ? null : licence.UserProfile.Trim(),
            // Protobuf JSON leaves out the zero value of an enum, so a record with no state is the unspecified one.
            SeatType = string.IsNullOrWhiteSpace(licence.LicenseAssignmentState)
                ? "LICENSE_ASSIGNMENT_STATE_UNSPECIFIED"
                : licence.LicenseAssignmentState.Trim(),
            AddedAt = DateTimeOffset.TryParse(
                licence.CreateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : null,
        };
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
                $"The Gemini user licence list returned {(int)response.StatusCode}. The service account needs the "
                + "discoveryengine.userStores.listUserLicenses permission on the project (a Discovery Engine role that includes it), "
                + "and the Discovery Engine API must be enabled there.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The Gemini user licence list returned 404. Check DiscoveryEngineProjectId, DiscoveryEngineLocation and UserStoreId "
                + "in the credential: the user store id is not documented by Google and defaults to default_user_store.");
        }

        response.EnsureSuccessStatusCode();
    }

    // These go into a URL path, so only the characters Google allows in such ids.
    private static string RequireSafe(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeId().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"The Gemini credential setting {setting} is missing or has a character not allowed in a Google Cloud id.");
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$")]
    private static partial Regex SafeId();

    private sealed class ListResponseDto
    {
        [JsonPropertyName("userLicenses")]
        public List<UserLicenseDto>? UserLicenses { get; set; }

        [JsonPropertyName("nextPageToken")]
        public string? NextPageToken { get; set; }
    }

    private sealed class UserLicenseDto
    {
        [JsonPropertyName("userPrincipal")]
        public string? UserPrincipal { get; set; }

        [JsonPropertyName("userProfile")]
        public string? UserProfile { get; set; }

        [JsonPropertyName("licenseAssignmentState")]
        public string? LicenseAssignmentState { get; set; }

        [JsonPropertyName("createTime")]
        public string? CreateTime { get; set; }
    }
}
