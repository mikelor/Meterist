using Meterist.Core.Vendors;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>Seam between the Gemini roster source and the Discovery Engine user licence list.</summary>
public interface IGeminiUserLicenseRepository
{
    /// <summary>
    /// Every user licence record in the tenant's user store, following the page token to the end. Returns the complete list or
    /// throws: a silently truncated list would look like people having left.
    /// </summary>
    Task<IReadOnlyList<VendorMember>> ListUserLicensesAsync(GeminiCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>Gets an OAuth access token for the tenant's service account. A seam so the HTTP client can be tested without Google.</summary>
public interface IGeminiAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(GeminiCredential credential, CancellationToken cancellationToken = default);
}
