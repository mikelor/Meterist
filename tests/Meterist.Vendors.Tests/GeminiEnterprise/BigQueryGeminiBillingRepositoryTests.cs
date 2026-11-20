using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

/// <summary>
/// Regression tests for the 2026-07-22 filter fix: verified against a live
/// test account that Google bills the Gemini Enterprise subscription/overage
/// lines under service.description "Vertex AI Search" (never a service
/// literally named "Gemini Enterprise" — that string only appears in the SKU
/// description). Guards against silently reverting to the original,
/// documentation-based guess that turned out to be wrong, and pins the
/// shape of the configurable filter and the excluded-charge check.
/// </summary>
public class BigQueryGeminiBillingRepositoryTests
{
    private const string Table = "`project.dataset.table`";

    private static readonly DateRange Period = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    [Fact]
    public void DefaultFilter_MatchesTheVerifiedRealBillingData()
    {
        var filter = GeminiBillingFilter.Default;

        var include = Assert.Single(filter.Include);
        Assert.Equal("Vertex AI Search", include.Service);
        Assert.Equal("%Enterprise%", include.SkuLike);

        Assert.Contains(filter.Ignore, r => r.Service == "Vertex AI Search" && r.SkuLike == "%Data Index%");
        Assert.Contains(filter.Ignore, r => r.Service == "Vertex AI" && r.SkuLike == "%Predictions%");

        // Agent Platform SKUs are watched, not counted: a visible decision, not an accident.
        Assert.Contains("Agent Platform", filter.WatchTerms);
        Assert.DoesNotContain(filter.Include, r => r.Service == "Vertex AI");
    }

    [Fact]
    public void BuildSql_DefaultFilter_FiltersOnVertexAiSearchServiceAndEnterpriseSku()
    {
        var sql = BigQueryGeminiBillingRepository.BuildSql(Table, GeminiBillingFilter.Default);

        Assert.Contains("WHERE ((service.description = @incService0 AND sku.description LIKE @incSku0))", sql);
        Assert.DoesNotContain("Gemini Enterprise", sql);
    }

    [Fact]
    public void BuildSql_MultipleIncludeRules_AreOrdered_AndEachGetsItsOwnParameters()
    {
        var filter = GeminiBillingFilter.Default;
        filter.Include.Add(new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Agent Platform%" });

        var sql = BigQueryGeminiBillingRepository.BuildSql(Table, filter);
        var parameters = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, includeExclusionParameters: false);

        Assert.Contains(
            "((service.description = @incService0 AND sku.description LIKE @incSku0) OR "
            + "(service.description = @incService1 AND sku.description LIKE @incSku1))",
            sql);
        Assert.Equal("Vertex AI", parameters.Single(p => p.Name == "incService1").Value);
        Assert.Equal("%Agent Platform%", parameters.Single(p => p.Name == "incSku1").Value);
    }

    [Fact]
    public void BuildExcludedSql_ExcludesIncludedAndIgnored_ButWatchesTerms_AndSkipsZeroCost()
    {
        var sql = BigQueryGeminiBillingRepository.BuildExcludedSql(Table, GeminiBillingFilter.Default);

        Assert.Contains("NOT ((COALESCE(service.description, '') = @incService0 AND COALESCE(sku.description, '') LIKE @incSku0))", sql);
        Assert.Contains("AND NOT ((COALESCE(service.description, '') = @ignService0 AND COALESCE(sku.description, '') LIKE @ignSku0) OR (COALESCE(service.description, '') = @ignService1", sql);
        Assert.Contains("LOWER(COALESCE(sku.description, '')) LIKE @watch0", sql);
        Assert.Contains("HAVING SUM(cost) != 0", sql);
        // Found against live BigQuery, not by a unit test: aliasing the sum as cost makes HAVING SUM(cost)
        // an aggregate of an aggregate, which BigQuery rejects. The alias must differ from the column.
        Assert.Contains("SUM(cost) AS total_cost", sql);
        Assert.DoesNotContain("AS cost", sql);
    }

    [Fact]
    public void BuildExcludedSql_WithNoIgnoreRules_OmitsTheIgnoreClause()
    {
        var filter = GeminiBillingFilter.Default;
        filter.Ignore.Clear();

        var sql = BigQueryGeminiBillingRepository.BuildExcludedSql(Table, filter);

        Assert.DoesNotContain("ign", sql);
    }

    [Fact]
    public void BuildParameters_WatchTermsAreLowercasedAndWrapped_OnlyForTheExclusionQuery()
    {
        var filter = GeminiBillingFilter.Default;

        var main = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, includeExclusionParameters: false);
        var excluded = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, includeExclusionParameters: true);

        Assert.DoesNotContain(main, p => p.Name.StartsWith("watch") || p.Name.StartsWith("ign"));
        Assert.Equal("%agent platform%", excluded.Single(p => p.Name == "watch1").Value);
        Assert.Contains(excluded, p => p.Name == "ignSku0");
    }

    [Fact]
    public void Validate_RejectsAnEmptyIncludeList_AndBlankRules()
    {
        Assert.Throws<InvalidOperationException>(() => new GeminiBillingFilter().Validate());

        var blank = GeminiBillingFilter.Default;
        blank.Include.Add(new GeminiBillingRule { Service = " ", SkuLike = "%x%" });
        Assert.Throws<InvalidOperationException>(blank.Validate);

        var blankWatch = GeminiBillingFilter.Default;
        blankWatch.WatchTerms.Add("");
        Assert.Throws<InvalidOperationException>(blankWatch.Validate);

        GeminiBillingFilter.Default.Validate();
    }
}
