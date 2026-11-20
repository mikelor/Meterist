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
/// shape of the configurable filter, the category and non-usage handling, and the excluded-charge check.
/// </summary>
public class BigQueryGeminiBillingRepositoryTests
{
    private const string Table = "`project.dataset.table`";

    private static readonly DateRange Period = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    [Fact]
    public void DefaultFilter_MatchesTheVerifiedRealBillingData()
    {
        var filter = GeminiBillingFilter.Default;

        Assert.Contains(filter.Include, r => r.Service == "Vertex AI Search" && r.SkuLike == "%Subscription%" && r.Category == "Seat");
        Assert.Contains(filter.Include, r => r.Service == "Vertex AI Search" && r.SkuLike == "%Enterprise%" && r.Category == "EnterpriseOverage");
        // Usage-priced Gemini spend is counted by default: agent platform, model usage, pay as you go.
        Assert.Contains(filter.Include, r => r.Service == "Vertex AI" && r.SkuLike == "%Agent Platform%" && r.Category == "AgentPlatform");
        Assert.Contains(filter.Include, r => r.Service == "Vertex AI" && r.SkuLike == "%Predictions%" && r.Category == "ModelUsage");
        Assert.Contains(filter.Include, r => r.SkuLike.Contains("Pay as you go") && r.Category == "PayAsYouGo");
        Assert.Contains(filter.Include, r => r.SkuLike.Contains("Pay-as-you-go") && r.Category == "PayAsYouGo");

        // Subscription must be matched before Enterprise, because the seat SKU contains both words and
        // the first matching rule decides the category.
        var include = filter.Include.Select(r => r.SkuLike).ToList();
        Assert.True(include.IndexOf("%Subscription%") < include.IndexOf("%Enterprise%"));

        // Tax, support and rounding are recorded as non-usage, each with a category.
        Assert.Contains(filter.NonUsage, r => r.Service == "Invoice" && r.SkuLike == "%Tax%" && r.Category == "Tax");
        Assert.Contains(filter.NonUsage, r => r.Service == "Invoice" && r.SkuLike == "%Rounding%" && r.Category == "Rounding");
        Assert.Contains(filter.NonUsage, r => r.Service == "Support" && r.Category == "Support");

        // Only data-index storage is knowingly left out.
        var ignore = Assert.Single(filter.Ignore);
        Assert.Equal("%Data Index%", ignore.SkuLike);

        Assert.Contains("Agent Platform", filter.WatchTerms);
        filter.Validate();
    }

    [Fact]
    public void BuildSql_DefaultFilter_CountsUsageRulesAndRecordsNonUsageOnes()
    {
        var sql = BigQueryGeminiBillingRepository.BuildSql(Table, GeminiBillingFilter.Default);

        // Usage rules first, then the non-usage ones, both in the WHERE clause.
        Assert.Contains("WHERE (((service.description = @incService0 AND sku.description LIKE @incSku0) OR ", sql);
        Assert.Contains(") OR ((service.description = @nonService0 AND sku.description LIKE @nonSku0) OR ", sql);
        Assert.DoesNotContain("Gemini Enterprise", sql);

        // The category and the usage flag are decided in SQL by the same rule order.
        Assert.Contains("THEN @incCategory0 WHEN", sql);
        Assert.Contains("THEN @nonCategory0 WHEN", sql);
        Assert.Contains("AS meterist_category", sql);
        Assert.Contains("CASE WHEN ((service.description = @incService0", sql);
        Assert.Contains("THEN TRUE ELSE FALSE END AS meterist_is_usage", sql);
        Assert.Contains("GROUP BY record_date, service_description, sku_description, meterist_category, meterist_is_usage", sql);
        Assert.Contains("service.description AS service_description", sql);
    }

    [Fact]
    public void BuildSql_WithNoNonUsageRules_IsTheUsageOnlyFilter()
    {
        var filter = GeminiBillingFilter.Default;
        filter.NonUsage.Clear();
        filter.Include.RemoveAll(r => r.Category != "Seat");

        var sql = BigQueryGeminiBillingRepository.BuildSql(Table, filter);

        Assert.Contains("WHERE ((service.description = @incService0 AND sku.description LIKE @incSku0))", sql);
        Assert.DoesNotContain("@non", sql);
    }

    [Fact]
    public void BuildSql_MultipleIncludeRules_AreOrdered_AndEachGetsItsOwnParameters()
    {
        var filter = GeminiBillingFilter.Default;
        filter.NonUsage.Clear();
        filter.Include.Clear();
        filter.Include.Add(new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Subscription%", Category = "Seat" });
        filter.Include.Add(new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Agent Platform%", Category = "AgentPlatform" });

        var sql = BigQueryGeminiBillingRepository.BuildSql(Table, filter);
        var parameters = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, forExcludedQuery: false);

        Assert.Contains(
            "((service.description = @incService0 AND sku.description LIKE @incSku0) OR "
            + "(service.description = @incService1 AND sku.description LIKE @incSku1))",
            sql);
        Assert.Equal("Vertex AI", parameters.Single(p => p.Name == "incService1").Value);
        Assert.Equal("%Agent Platform%", parameters.Single(p => p.Name == "incSku1").Value);
        Assert.Equal("AgentPlatform", parameters.Single(p => p.Name == "incCategory1").Value);
    }

    [Fact]
    public void BuildParameters_MainQuery_CarriesCategoriesForBothRuleLists_AndNullForARuleWithNone()
    {
        var filter = GeminiBillingFilter.Default;
        filter.Include.Add(new GeminiBillingRule { Service = "Vertex AI", SkuLike = "%Custom%", Category = null });

        var parameters = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, forExcludedQuery: false);

        Assert.Equal("Seat", parameters.Single(p => p.Name == "incCategory0").Value);
        Assert.Null(parameters.Single(p => p.Name == $"incCategory{filter.Include.Count - 1}").Value);
        Assert.Equal("Tax", parameters.Single(p => p.Name == "nonCategory0").Value);
        Assert.Equal("Support", parameters.Single(p => p.Name == "nonCategory2").Value);
        Assert.DoesNotContain(parameters, p => p.Name.StartsWith("watch") || p.Name.StartsWith("ign"));
    }

    [Fact]
    public void BuildExcludedSql_TreatsCountedAndNonUsageLinesAsHandled_ButWatchesTerms_AndSkipsZeroCost()
    {
        var sql = BigQueryGeminiBillingRepository.BuildExcludedSql(Table, GeminiBillingFilter.Default);

        // A line that is counted, or recorded as non-usage, has been dealt with, so it is not "excluded".
        Assert.Contains("NOT ((COALESCE(service.description, '') = @incService0 AND COALESCE(sku.description, '') LIKE @incSku0) OR ", sql);
        Assert.Contains(" OR (COALESCE(service.description, '') = @nonService0 AND COALESCE(sku.description, '') LIKE @nonSku0)", sql);
        Assert.Contains("AND NOT ((COALESCE(service.description, '') = @ignService0 AND COALESCE(sku.description, '') LIKE @ignSku0))", sql);
        Assert.Contains("LOWER(COALESCE(sku.description, '')) LIKE @watch0", sql);
        Assert.Contains("HAVING SUM(cost) != 0", sql);
        // Found against live BigQuery, not by a unit test: aliasing the sum as cost makes HAVING SUM(cost)
        // an aggregate of an aggregate, which BigQuery rejects. The alias must differ from the column.
        Assert.Contains("SUM(cost) AS total_cost", sql);
        Assert.DoesNotContain("AS cost", sql);
        // The excluded query never reads categories.
        Assert.DoesNotContain("meterist_category", sql);
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
    public void BuildParameters_ExcludedQuery_HasEveryParameterItReferences_AndNoCategories()
    {
        var filter = GeminiBillingFilter.Default;

        var main = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, forExcludedQuery: false);
        var excluded = BigQueryGeminiBillingRepository.BuildParameters(filter, Period, forExcludedQuery: true);

        Assert.DoesNotContain(main, p => p.Name.StartsWith("watch") || p.Name.StartsWith("ign"));
        Assert.Equal("%agent platform%", excluded.Single(p => p.Name == "watch1").Value);
        Assert.Contains(excluded, p => p.Name == "ignSku0");
        Assert.Contains(excluded, p => p.Name == "nonSku0");
        Assert.DoesNotContain(excluded, p => p.Name.Contains("Category"));
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

    [Fact]
    public void Validate_ANonUsageRuleNeedsACategory()
    {
        var filter = GeminiBillingFilter.Default;
        filter.NonUsage.Add(new GeminiBillingRule { Service = "Invoice", SkuLike = "%Other%", Category = null });

        var ex = Assert.Throws<InvalidOperationException>(filter.Validate);

        Assert.Contains("NonUsage", ex.Message);
        Assert.Contains("Category", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" Seat")]
    [InlineData("Seat ")]
    public void Validate_ACategoryThatIsBlankOrPadded_IsRejected(string category)
    {
        var filter = GeminiBillingFilter.Default;
        filter.Include[0].Category = category;

        Assert.Throws<InvalidOperationException>(filter.Validate);
    }

    [Fact]
    public void Validate_ACategoryLongerThanTheTableAllows_IsRejected()
    {
        var filter = GeminiBillingFilter.Default;
        filter.Include[0].Category = new string('x', GeminiBillingFilter.MaxCategoryLength + 1);

        Assert.Throws<InvalidOperationException>(filter.Validate);

        filter.Include[0].Category = new string('x', GeminiBillingFilter.MaxCategoryLength);
        filter.Validate();
    }

    [Fact]
    public void Validate_AnIncludeRuleMayLeaveTheCategoryOut_ForAnOlderConfiguration()
    {
        var filter = GeminiBillingFilter.Default;
        filter.Include.Clear();
        filter.Include.Add(new GeminiBillingRule { Service = "Vertex AI Search", SkuLike = "%Enterprise%" });

        filter.Validate();
    }
}
