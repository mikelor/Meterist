namespace Meterist.Vendors.ClaudeApiPlatform;

/// <summary>
/// Settings for the Claude API Platform adapter, bound from the <c>Meterist:ClaudeApiPlatform</c> configuration
/// section (appsettings, or the environment variable <c>Meterist__ClaudeApiPlatform__UseLegacyCostReport</c>).
/// </summary>
public sealed class ClaudeApiPlatformOptions
{
    public const string ConfigurationSection = "Meterist:ClaudeApiPlatform";

    /// <summary>
    /// False (the default) reads cost from the newer <c>/analytics/cost</c> endpoint with the same Admin key, which attributes
    /// usage to an actor (person, service account or key). True falls back to the legacy <c>cost_report</c>, which only knows
    /// workspaces and descriptions, for example if the beta endpoint changes or ends. The setting is global, not per tenant.
    /// </summary>
    public bool UseLegacyCostReport { get; set; }
}
