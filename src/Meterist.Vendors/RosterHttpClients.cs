using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeEnterprise;
using Microsoft.Extensions.DependencyInjection;

namespace Meterist.Vendors;

/// <summary>
/// Registers the two member-list HTTP clients. Pagination for both puts a cursor in the query string that is a member
/// id, so these clients are set up to keep their URLs out of logs: the HttpClient factory's own request logging, which
/// writes the full URI, is removed for them (the repositories already avoid logging it themselves, but the factory's
/// handlers are outside their control and a level setting in appsettings is configuration, not a guarantee). Their
/// requests are also marked sensitive (<see cref="Core.Http.SensitiveRequest"/>) so tracing leaves them out.
/// </summary>
public static class RosterHttpClients
{
    public static IServiceCollection AddChatGptRosterHttpClient(this IServiceCollection services)
    {
        // Auto-redirect stays off: every request carries the bearer token, and a redirect has no business carrying it anywhere else.
        services
            .AddHttpClient<IChatGptWorkspaceUserRepository, HttpChatGptWorkspaceUserRepository>(client =>
            {
                client.BaseAddress = new Uri("https://api.chatgpt.com/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>
    /// The ChatGPT Spend Controls list. Same treatment as the roster client: its cursor is opaque but may encode a
    /// member, so the factory's request logging is removed and requests are marked sensitive; auto-redirect is off.
    /// </summary>
    public static IServiceCollection AddChatGptSpendControlsHttpClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<IChatGptSpendControlsRepository, HttpChatGptSpendControlsRepository>(client =>
            {
                client.BaseAddress = new Uri("https://api.chatgpt.com/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>
    /// The Claude per-person activity endpoint. Its pagination cursor is base64 of JSON holding a member's email address, so the
    /// factory's request logging is removed and requests are marked sensitive (see <see cref="HttpClaudeUserActivityRepository"/>).
    /// </summary>
    public static IServiceCollection AddClaudeUserActivityHttpClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<IClaudeUserActivityRepository, HttpClaudeUserActivityRepository>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
            })
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>
    /// The Gemini Enterprise user licence list. A page token for a list of people may encode one, so the factory's request
    /// logging is removed and requests are marked sensitive; auto-redirect is off because every request carries a bearer token.
    /// No base address: the host depends on the tenant's Discovery Engine location, which the repository resolves.
    /// </summary>
    public static IServiceCollection AddGeminiUserLicenseHttpClient(this IServiceCollection services)
    {
        services.AddSingleton<GeminiEnterprise.IGeminiAccessTokenProvider, GeminiEnterprise.ServiceAccountAccessTokenProvider>();
        services
            .AddHttpClient<GeminiEnterprise.IGeminiUserLicenseRepository, GeminiEnterprise.HttpGeminiUserLicenseRepository>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        return services;
    }

    public static IServiceCollection AddClaudeRosterHttpClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<IClaudeMemberRosterRepository, HttpClaudeMemberRosterRepository>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
            })
            .RemoveAllLoggers();

        return services;
    }
}
