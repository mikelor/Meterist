using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Meterist.Vendors.ClaudeApiPlatform;
using Meterist.Vendors.ClaudeEnterprise;
using Meterist.Vendors.GeminiEnterprise;
using Meterist.Vendors.Tests.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests;

/// <summary>
/// Regression tests for the cross-vendor overage/per-user capability matrix
/// documented in docs/vendor-integration-reference.md — these flags are
/// architecturally load-bearing (see IVendorSpendExtractor), so an accidental
/// flip here should fail loudly rather than silently drift from the docs.
/// </summary>
public class CapabilityMatrixTests
{
    public static IEnumerable<object[]> Extractors()
    {
        yield return [new ClaudeEnterpriseSpendExtractor(), VendorCatalog.ClaudeEnterprise, true, true];
        yield return [new ClaudeApiPlatformSpendExtractor(), VendorCatalog.ClaudeApiPlatform, false, false];
        yield return [new ChatGptEnterpriseSpendExtractor(), VendorCatalog.ChatGptEnterprise, true, true];
        yield return [
            new GeminiEnterpriseSpendExtractor(
                new FakeSecretStore(), new FakeGeminiBillingQueryRepository(),
                NullLogger<GeminiEnterpriseSpendExtractor>.Instance),
            VendorCatalog.GeminiEnterprise, true, true,
        ];
    }

    [Theory]
    [MemberData(nameof(Extractors))]
    public void CapabilityFlags_MatchTheDocumentedMatrix(
        IVendorSpendExtractor extractor,
        VendorIdentity expectedIdentity,
        bool expectedSupportsOverage,
        bool expectedSupportsPerUserBreakdown)
    {
        Assert.Equal(expectedIdentity.Id, extractor.VendorId);
        Assert.Equal(expectedSupportsOverage, extractor.SupportsOverage);
        Assert.Equal(expectedSupportsPerUserBreakdown, extractor.SupportsPerUserBreakdown);
    }

    [Fact]
    public void VendorCatalog_HasNoDuplicateIdsOrShortNames()
    {
        Assert.Equal(VendorCatalog.All.Count, VendorCatalog.All.Select(v => v.Id).Distinct().Count());
        Assert.Equal(
            VendorCatalog.All.Count,
            VendorCatalog.All.Select(v => v.ShortName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task UnimplementedExtractors_ThrowNotImplemented_NotSilentlyReturnEmpty()
    {
        var extractor = new ChatGptEnterpriseSpendExtractor();
        var period = new DateRange(new DateOnly(2026, 7, 19), new DateOnly(2026, 7, 25));

        await Assert.ThrowsAsync<NotImplementedException>(
            () => extractor.ExtractAsync("ecosync", period, TestContext.Current.CancellationToken));
    }
}
