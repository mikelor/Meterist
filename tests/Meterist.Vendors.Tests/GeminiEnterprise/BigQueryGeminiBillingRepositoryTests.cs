using Meterist.Vendors.GeminiEnterprise;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

/// <summary>
/// Regression test for the 2026-07-22 filter fix: verified against a live
/// test account that Google bills the Gemini Enterprise subscription/overage
/// lines under service.description "Vertex AI Search" (never a service
/// literally named "Gemini Enterprise" — that string only appears in the SKU
/// description). Guards against silently reverting to the original,
/// documentation-based guess that turned out to be wrong.
/// </summary>
public class BigQueryGeminiBillingRepositoryTests
{
    [Fact]
    public void BuildSql_FiltersOnVertexAiSearchServiceAndEnterpriseSku()
    {
        var sql = BigQueryGeminiBillingRepository.BuildSql("`project.dataset.table`");

        Assert.Contains("WHERE service.description = @serviceName", sql);
        Assert.Contains("AND sku.description LIKE @skuFilter", sql);
        Assert.DoesNotContain("Gemini Enterprise", sql);
    }

    [Fact]
    public void ServiceNameAndSkuFilter_MatchTheVerifiedRealBillingData()
    {
        Assert.Equal("Vertex AI Search", BigQueryGeminiBillingRepository.ServiceName);
        Assert.Equal("%Enterprise%", BigQueryGeminiBillingRepository.SkuDescriptionFilter);
    }
}
