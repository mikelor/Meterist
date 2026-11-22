using Google.Apis.Auth.OAuth2;

namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>An access token for the cloud-platform scope from the tenant's service-account key. Never logged.</summary>
public sealed class ServiceAccountAccessTokenProvider : IGeminiAccessTokenProvider
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    public async Task<string> GetAccessTokenAsync(GeminiCredential credential, CancellationToken cancellationToken = default)
    {
        // GoogleCredential.FromJson(string) is obsolete (security risk): go through CredentialFactory, as the billing client does.
        var googleCredential = CredentialFactory
            .FromJson<ServiceAccountCredential>(credential.ServiceAccountJson)
            .ToGoogleCredential()
            .CreateScoped(CloudPlatformScope);

        return await ((ITokenAccess)googleCredential).GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
