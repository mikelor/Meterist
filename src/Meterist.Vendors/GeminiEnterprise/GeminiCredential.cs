namespace Meterist.Vendors.GeminiEnterprise;

/// <summary>
/// Stored as the JSON payload behind ISecretStore.GetCredentialAsync(tenantId,
/// "GeminiEnterprise") — bundles the service account key with the billing
/// export location it should query, since nothing else in the system tracks
/// per-tenant vendor *connection* config (only credentials). See
/// docs/architecture.md §6 and the plan context for why this lives here
/// rather than as a new general-purpose config subsystem.
/// <para>
/// The optional Discovery Engine settings below are only for the licence roster (issue #37), which reads a different
/// API in a possibly different project from the billing export. They are all absent from a credential stored before
/// that feature, which keeps working for billing; the roster then reports the tenant as not set up.
/// </para>
/// </summary>
public sealed record GeminiCredential
{
    public required string ServiceAccountJson { get; init; }

    public required string BillingProjectId { get; init; }

    public required string BillingDatasetId { get; init; }

    public required string BillingTableId { get; init; }

    /// <summary>The Google Cloud project that owns the Gemini Enterprise app (not necessarily the billing project). Null: the licence roster is not set up.</summary>
    public string? DiscoveryEngineProjectId { get; init; }

    /// <summary>The Discovery Engine location of the app: <c>global</c>, <c>us</c> or <c>eu</c>.</summary>
    public string DiscoveryEngineLocation { get; init; } = "global";

    /// <summary>
    /// The user store holding the licences. Google's reference does not name a default; <c>default_user_store</c> is what the
    /// Gemini Enterprise console uses and is unverified against a live tenant until the first roster is taken.
    /// </summary>
    public string UserStoreId { get; init; } = "default_user_store";

    /// <summary>True when the settings the licence roster needs are present.</summary>
    public bool HasLicenseSettings => !string.IsNullOrWhiteSpace(DiscoveryEngineProjectId);
}
