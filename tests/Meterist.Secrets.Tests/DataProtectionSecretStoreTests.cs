using System.Runtime.Versioning;
using Meterist.Core.Secrets;
using Meterist.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Secrets.Tests;

/// <summary>
/// These use real encrypted files, written and read through real DataProtection key rings in temp folders. A file
/// written under one key ring and read under another is exactly what a scheduled task running as a different
/// Windows account looks like to DPAPI: the key cannot be recovered.
/// </summary>
// The store encrypts with DPAPI, so it and these tests are Windows-only, as the CLI assembly already declares.
[SupportedOSPlatform("windows")]
public sealed class DataProtectionSecretStoreTests : IDisposable
{
    private static readonly Guid Claude = Guid.Parse("8f14e45f-ceea-467e-adde-3f82edcd1a11");
    private static readonly Guid ChatGpt = Guid.Parse("3d6f1c2e-8a4b-4e3a-8b2f-7c5e9a1d4f33");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "meterist-secrets-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ACredentialRoundTrips_AndAnUnknownOneIsNull()
    {
        var store = Store(keyRing: "ring-a");

        await store.SetCredentialAsync("t1", Claude, "claude-secret", TestContext.Current.CancellationToken);

        Assert.Equal("claude-secret", await store.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetCredentialAsync("t1", ChatGpt, TestContext.Current.CancellationToken));   // tenant has a file, not this vendor
        Assert.Null(await store.GetCredentialAsync("never-stored", Claude, TestContext.Current.CancellationToken)); // no file at all
    }

    [Fact]
    public async Task AFileThatCannotBeDecrypted_IsAnErrorOnRead_NotNull()
    {
        // Stored under one key ring, read under another: the key is unrecoverable, as for a different Windows account.
        await Store(keyRing: "ring-a").SetCredentialAsync("t1", Claude, "claude-secret", TestContext.Current.CancellationToken);
        var other = Store(keyRing: "ring-b");

        var ex = await Assert.ThrowsAsync<UnreadableCredentialException>(
            () => other.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));

        Assert.Equal("t1", ex.TenantId);
        Assert.Contains("'t1'", ex.Message);
        Assert.Contains("Windows account", ex.Message);
        Assert.Contains("credentials set", ex.Message);
        Assert.IsAssignableFrom<System.Security.Cryptography.CryptographicException>(ex.InnerException);
        Assert.DoesNotContain("claude-secret", ex.Message);
    }

    [Fact]
    public async Task EveryVendorOfAnUnreadableTenant_IsAnError_NotJustTheOneThatWasStored()
    {
        // A tenant whose file cannot be read must not look like it uses a different set of vendors.
        await Store(keyRing: "ring-a").SetCredentialAsync("t1", Claude, "claude-secret", TestContext.Current.CancellationToken);
        var other = Store(keyRing: "ring-b");

        await Assert.ThrowsAsync<UnreadableCredentialException>(() => other.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnreadableCredentialException>(() => other.GetCredentialAsync("t1", ChatGpt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADamagedFile_IsAnErrorOnRead_NotNull()
    {
        var store = Store(keyRing: "ring-a");
        Directory.CreateDirectory(SecretsDir);
        await File.WriteAllBytesAsync(Path.Combine(SecretsDir, "t1.bin"), [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnreadableCredentialException>(
            () => store.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFileThatDecryptsToSomethingThatIsNotJson_IsAnErrorOnRead_NotNull()
    {
        var provider = Provider("ring-a");
        Directory.CreateDirectory(SecretsDir);
        var notJson = provider.CreateProtector("Meterist.Tenant.t1").Protect("this is not json {"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(SecretsDir, "t1.bin"), notJson, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<UnreadableCredentialException>(
            () => new DataProtectionSecretStore(provider, NullLogger<DataProtectionSecretStore>.Instance, SecretsDir)
                .GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task AWrite_StillRecoversFromAnUnreadableFile_SoAnOperatorCanAlwaysResetATenant()
    {
        await Store(keyRing: "ring-a").SetCredentialAsync("t1", Claude, "old-claude", TestContext.Current.CancellationToken);
        await Store(keyRing: "ring-a").SetCredentialAsync("t1", ChatGpt, "old-chatgpt", TestContext.Current.CancellationToken);
        var logger = new CapturingLogger<DataProtectionSecretStore>();
        var other = Store(keyRing: "ring-b", logger);

        // Reading is blocked, but resetting one vendor must work and say what it dropped.
        await Assert.ThrowsAsync<UnreadableCredentialException>(() => other.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
        await other.SetCredentialAsync("t1", Claude, "new-claude", TestContext.Current.CancellationToken);

        Assert.Equal("new-claude", await other.GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
        Assert.Null(await other.GetCredentialAsync("t1", ChatGpt, TestContext.Current.CancellationToken));   // lost with the unreadable file, as warned

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("'t1'", warning);
        Assert.Contains("re-set", warning);
        Assert.DoesNotContain("old-", warning);
        Assert.DoesNotContain("new-claude", warning);
    }

    [Fact]
    public async Task AReadThatFails_NeverChangesTheFile()
    {
        await Store(keyRing: "ring-a").SetCredentialAsync("t1", Claude, "claude-secret", TestContext.Current.CancellationToken);
        var path = Path.Combine(SecretsDir, "t1.bin");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnreadableCredentialException>(
            () => Store(keyRing: "ring-b").GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        // Still readable by the account that stored it.
        Assert.Equal("claude-secret", await Store(keyRing: "ring-a").GetCredentialAsync("t1", Claude, TestContext.Current.CancellationToken));
    }

    private string SecretsDir => Path.Combine(_root, "secrets");

    private IDataProtectionProvider Provider(string keyRing) =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys", keyRing)));

    private DataProtectionSecretStore Store(string keyRing, ILogger<DataProtectionSecretStore>? logger = null) =>
        new(Provider(keyRing), logger ?? NullLogger<DataProtectionSecretStore>.Instance, SecretsDir);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IEnumerable<string> Warnings => _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
