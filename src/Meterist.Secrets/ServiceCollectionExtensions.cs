using Meterist.Core.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Meterist.Secrets;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the v1 DPAPI-backed ISecretStore. ProtectKeysWithDpapi() is
    /// Windows-only by design — this matches the "local deployment for v1"
    /// decision in docs/architecture.md §9; it is not meant to run cross-platform.
    /// </summary>
    public static IServiceCollection AddMeteristSecretStore(
        this IServiceCollection services,
        string? keyStorageDirectory = null)
    {
        var directory = keyStorageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meterist",
            "keys");

        // Key storage already lives in a Meterist-only directory, so a separate
        // application-name discriminator isn't needed for isolation here.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .ProtectKeysWithDpapi();

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        return services;
    }
}
