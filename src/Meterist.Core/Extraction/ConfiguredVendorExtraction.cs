using Meterist.Core.Models;
using Meterist.Core.Secrets;
using Meterist.Core.Vendors;

namespace Meterist.Core.Extraction;

/// <summary>
/// Extraction limited to the vendors a tenant actually has a credential for, so a tenant
/// that uses only some of the vendors needs no special handling: the others come back as
/// <see cref="VendorExtractionStatus.Skipped"/> rather than as failures.
/// </summary>
public sealed class ConfiguredVendorExtraction
{
    private readonly SpendExtractionService _extractionService;
    private readonly ISecretStore _secretStore;

    public ConfiguredVendorExtraction(SpendExtractionService extractionService, ISecretStore secretStore)
    {
        _extractionService = extractionService;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<VendorExtractionResult>> ExtractAsync(
        string tenantId,
        DateRange period,
        Guid? vendorFilter = null,
        CancellationToken cancellationToken = default,
        bool dryRun = false)
    {
        var results = new List<VendorExtractionResult>();

        foreach (var vendor in VendorCatalog.All.Where(v => vendorFilter is null || v.Id == vendorFilter))
        {
            string? credential;
            try
            {
                credential = await _secretStore
                    .GetCredentialAsync(tenantId, vendor.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Credentials that exist but cannot be read (wrong Windows account, damaged file) are a failure of
                // this vendor, not "no credential": skipping it would let a scheduled run exit 0 having collected
                // nothing. The other vendors still run.
                results.Add(new VendorExtractionResult(
                    vendor.Id, vendor.DisplayName, VendorExtractionStatus.Failed, 0, DescribeCredentialFailure(ex)));
                continue;
            }

            if (string.IsNullOrWhiteSpace(credential))
            {
                results.Add(new VendorExtractionResult(
                    vendor.Id, vendor.DisplayName, VendorExtractionStatus.Skipped, 0,
                    "no credential stored for this tenant"));
                continue;
            }

            results.AddRange(await _extractionService
                .ExtractAsync(tenantId, period, vendor.Id, cancellationToken, dryRun)
                .ConfigureAwait(false));
        }

        return results;
    }

    // The message of an operator-facing failure is already clear; anything else gets its type, which says what broke.
    private static string DescribeCredentialFailure(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : $"Could not read the stored credential: {ex.GetType().Name}: {ex.Message}";
}

public static class ExtractionExitCode
{
    /// <summary>
    /// 1 if any vendor extraction failed, otherwise 0. A scheduled run has to be able to tell
    /// that something broke; skipped and not-implemented vendors are not failures.
    /// </summary>
    public static int For(IReadOnlyList<VendorExtractionResult> results) =>
        results.Any(r => r.Status == VendorExtractionStatus.Failed) ? 1 : 0;
}
