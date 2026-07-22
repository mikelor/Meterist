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

        // SetApplicationName pins a stable "application discriminator" that gets
        // baked into every key derivation. Without it, DataProtection derives one
        // implicitly from ContentRootPath — meaning previously-encrypted secrets
        // silently become undecryptable ("The payload was invalid") the moment
        // ContentRootPath changes for any reason (different working directory,
        // the app copied elsewhere, etc.). A hardcoded name makes decryption
        // independent of where/how the binary is invoked, permanently.
        services.AddDataProtection()
            .SetApplicationName("Meterist")
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .ProtectKeysWithDpapi();

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        return services;
    }
}
