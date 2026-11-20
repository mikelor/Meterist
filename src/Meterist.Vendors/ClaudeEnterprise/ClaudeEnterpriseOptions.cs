namespace Meterist.Vendors.ClaudeEnterprise;

/// <summary>
/// Settings for the Claude Enterprise adapter, bound from the <c>Meterist:ClaudeEnterprise</c>
/// configuration section (appsettings, or the environment variable
/// <c>Meterist__ClaudeEnterprise__UseLegacyCostReport</c>).
/// </summary>
public sealed class ClaudeEnterpriseOptions
{
    public const string ConfigurationSection = "Meterist:ClaudeEnterprise";

    /// <summary>
    /// False (the default) reads cost from the newer <c>/analytics/cost</c> endpoint, which also carries
    /// request counts and the actor's type. True falls back to the legacy <c>user_cost_report</c>, for
    /// example if the beta endpoint changes or ends. The two returned identical daily totals in the
    /// 2026-11-20 live test. The setting is global, not per tenant.
    /// </summary>
    public bool UseLegacyCostReport { get; set; }
}
