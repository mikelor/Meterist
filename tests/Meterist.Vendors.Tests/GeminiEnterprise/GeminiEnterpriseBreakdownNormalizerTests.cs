using System.Text.Json;
using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Meterist.Vendors.GeminiEnterprise;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Vendors.Tests.GeminiEnterprise;

public class GeminiEnterpriseBreakdownNormalizerTests
{
    private static readonly Guid Vendor = VendorCatalog.GeminiEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 1);
    private static readonly DateOnly D2 = new(2026, 11, 2);

    [Fact]
    public void Normalize_OneRowPerCategoryAndSku_WithTheKindsFlaggedUsageOrNot()
    {
        var rows = new GeminiEnterpriseBreakdownNormalizer().Normalize(Raw(
            (D1, [
                Row("Plus: Subscription", 11.5m, "Seat"),
                Row("Agent Platform Compute", 0.02m, "AgentPlatform"),
                Row("Agent Platform Memory", 0.01m, "AgentPlatform"),
                Row("Tax", 1.15m, "Tax", isUsage: false),
                Row("GCP Support Minimum fee", 3m, "Support", isUsage: false),
            ])));

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            [("AgentPlatform", true), ("AgentPlatform", true), ("Seat", true), ("Support", false), ("Tax", false)],
            rows.Select(r => (r.Category, r.IsUsage)).OrderBy(x => x.Category, StringComparer.Ordinal));
        var tax = Assert.Single(rows, r => r.Category == "Tax");
        Assert.Equal("Tax", tax.Detail);
        Assert.Equal(1.15m, tax.Amount);
        Assert.Equal(D1, tax.Date);
        Assert.Equal("t1", tax.TenantId);
        Assert.Equal(Vendor, tax.VendorId);
    }

    [Fact]
    public void Normalize_UsageRowsSumToTheDailyRecordsGrossSpend_AndNonUsageRowsNeverEnterIt()
    {
        var raw = Raw(
            (D1, [
                Row("Plus: Subscription", 11.5m, "Seat", credits: 0m),
                Row("Predictions", 0.25m, "ModelUsage", credits: -0.05m),
                Row("Tax", 1.15m, "Tax", isUsage: false),
                Row("Support fee", 3m, "Support", isUsage: false),
            ]),
            (D2, [Row("Plus: Subscription", 11.5m, "Seat")]));

        var daily = new GeminiEnterpriseSpendNormalizer(NullLogger<GeminiEnterpriseSpendNormalizer>.Instance)
            .Normalize(raw, []);
        var breakdown = new GeminiEnterpriseBreakdownNormalizer().Normalize(raw);

        foreach (var day in daily)
        {
            var usageSum = breakdown.Where(b => b.Date == day.Date && b.IsUsage).Sum(b => b.Amount);
            Assert.Equal(day.GrossSpend, usageSum);
        }

        var d1 = daily.Single(d => d.Date == D1);
        Assert.Equal(11.5m, d1.SeatFee);
        Assert.Equal(0.25m, d1.UsageOrOverage);
        Assert.Equal(11.75m, d1.GrossSpend);        // tax and support are not in it
        Assert.Equal(0.05m, d1.CreditsApplied);
        Assert.Equal(4.15m, breakdown.Where(b => b.Date == D1 && !b.IsUsage).Sum(b => b.Amount));
    }

    [Fact]
    public void DailyNormalizer_TakesTheSeatFeeFromTheSeatCategory_NotFromTheSkuText()
    {
        // A configured rule can call any line the seat; the SKU wording is only the fallback.
        var daily = new GeminiEnterpriseSpendNormalizer(NullLogger<GeminiEnterpriseSpendNormalizer>.Instance)
            .Normalize(Raw((D1, [Row("Some Licence Fee", 10m, "Seat"), Row("Plus: Subscription add-on", 2m, "EnterpriseOverage")])), []);

        var day = Assert.Single(daily);
        Assert.Equal(10m, day.SeatFee);
        Assert.Equal(2m, day.UsageOrOverage);   // "Subscription" in the text no longer makes it a seat
    }

    [Fact]
    public void Normalize_RowsFromBeforeCategoriesExisted_AreClassifiedFromTheirSku_AndAreUsage()
    {
        // Exactly the shape stored before this change: no Category, no IsUsage, no ServiceDescription.
        var legacy = new Dictionary<string, object?>
        {
            [GeminiBillingRowFields.RecordDate] = D1,
            [GeminiBillingRowFields.SkuDescription] = "Gemini Enterprise Plus: Subscription - one month term",
            [GeminiBillingRowFields.Cost] = 11.5m,
            [GeminiBillingRowFields.CreditsAmount] = 0m,
        };
        var raw = Raw((D1, [legacy]));

        var breakdown = Assert.Single(new GeminiEnterpriseBreakdownNormalizer().Normalize(raw));
        var daily = Assert.Single(new GeminiEnterpriseSpendNormalizer(NullLogger<GeminiEnterpriseSpendNormalizer>.Instance).Normalize(raw, []));

        Assert.Equal("Seat", breakdown.Category);
        Assert.True(breakdown.IsUsage);
        Assert.Equal(11.5m, daily.SeatFee);
    }

    [Fact]
    public void Normalize_SumsTheSameSkuUnderTwoServices_AndSkipsZeroCostRows()
    {
        var rows = new GeminiEnterpriseBreakdownNormalizer().Normalize(Raw(
            (D1, [
                Row("Compute", 1m, "AgentPlatform", service: "Vertex AI"),
                Row("Compute", 2m, "AgentPlatform", service: "Other Service"),
                Row("Free thing", 0m, "Other"),
            ])));

        var only = Assert.Single(rows);
        Assert.Equal(3m, only.Amount);
    }

    [Fact]
    public void Normalize_ADayWithNoRows_ProducesNothing()
    {
        Assert.Empty(new GeminiEnterpriseBreakdownNormalizer().Normalize(Raw((D1, []))));
    }

    [Fact]
    public async Task Backfill_RebuildsTheBreakdownFromStoredRawJson_AndCoversOnlyDaysThatHaveRawRecords()
    {
        // Raw rows as an extraction stores them: a DateOnly becomes a string, a decimal a number, a bool a bool.
        var stored = new Dictionary<DateOnly, string>
        {
            [D1] = JsonSerializer.Serialize(new object[]
            {
                Row("Plus: Subscription", 11.5m, "Seat"),
                Row("Tax", 1.15m, "Tax", isUsage: false),
            }),
        };
        var breakdowns = new CapturingBreakdownRepository();
        var backfill = new SpendBreakdownBackfill(new StoredRaw(stored), [new GeminiEnterpriseBreakdownNormalizer()], breakdowns);

        // The period spans D1..D1+9, but only D1 has a raw record: only D1 may be replaced.
        var result = await backfill.RunAsync("t1", Vendor, new DateRange(D1, D1.AddDays(9)), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DaysRead);
        Assert.Equal(2, result.RowsNormalized);
        Assert.Equal([D1], breakdowns.CoveredDays);
        var tax = Assert.Single(breakdowns.Written, r => r.Category == "Tax");
        Assert.False(tax.IsUsage);
        Assert.Equal(1.15m, tax.Amount);
        Assert.True(Assert.Single(breakdowns.Written, r => r.Category == "Seat").IsUsage);
    }

    [Fact]
    public async Task Backfill_DryRun_PlansAndWritesNothing()
    {
        var breakdowns = new CapturingBreakdownRepository();
        var backfill = new SpendBreakdownBackfill(
            new StoredRaw(new Dictionary<DateOnly, string> { [D1] = JsonSerializer.Serialize(new object[] { Row("Plus: Subscription", 11.5m, "Seat") }) }),
            [new GeminiEnterpriseBreakdownNormalizer()], breakdowns);

        var result = await backfill.RunAsync("t1", Vendor, new DateRange(D1, D1), dryRun: true, TestContext.Current.CancellationToken);

        Assert.Empty(breakdowns.Written);
        Assert.Equal(1, breakdowns.Planned);
        Assert.Equal(1, result.Replace.Inserted);
    }

    [Fact]
    public async Task Backfill_ForAVendorWithNoBreakdownNormalizer_SaysSo()
    {
        var backfill = new SpendBreakdownBackfill(
            new StoredRaw(new Dictionary<DateOnly, string>()), [new GeminiEnterpriseBreakdownNormalizer()], new CapturingBreakdownRepository());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.RunAsync(
            "t1", VendorCatalog.ChatGptEnterprise.Id, new DateRange(D1, D1), false, TestContext.Current.CancellationToken));

        Assert.Contains("ChatGPT Enterprise", ex.Message);
        Assert.Contains("nothing to backfill", ex.Message);
    }

    private static RawVendorSpendData Raw(params (DateOnly Day, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)[] days) => new()
    {
        VendorId = Vendor,
        TenantId = "t1",
        Period = new DateRange(days.Min(d => d.Day), days.Max(d => d.Day)),
        RecordsByDate = days.ToDictionary(d => d.Day, d => d.Rows),
    };

    private static Dictionary<string, object?> Row(
        string sku, decimal cost, string category, bool isUsage = true, decimal credits = 0m,
        string service = "Vertex AI Search") => new()
    {
        [GeminiBillingRowFields.RecordDate] = D1,
        [GeminiBillingRowFields.ServiceDescription] = service,
        [GeminiBillingRowFields.SkuDescription] = sku,
        [GeminiBillingRowFields.Category] = category,
        [GeminiBillingRowFields.IsUsage] = isUsage,
        [GeminiBillingRowFields.Cost] = cost,
        [GeminiBillingRowFields.CreditsAmount] = credits,
    };

    private sealed class StoredRaw(IReadOnlyDictionary<DateOnly, string> days) : IRawExtractionRepository
    {
        public Task UpsertAsync(
            string tenantId, Guid vendorId,
            IReadOnlyDictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> recordsByDate,
            DateTime extractedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<DateOnly, string>> GetRecordsJsonAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult(days);
    }

    private sealed class CapturingBreakdownRepository : IDailySpendBreakdownRepository
    {
        public List<DailySpendBreakdownRecord> Written { get; } = [];

        public IReadOnlyCollection<DateOnly> CoveredDays { get; private set; } = [];

        public int Planned { get; private set; }

        public Task<BreakdownReplaceResult> ReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Written.AddRange(list);
            CoveredDays = coveredDays;
            return Task.FromResult(new BreakdownReplaceResult(list.Count, 0, 0, 0));
        }

        public Task<BreakdownReplaceResult> PlanReplaceAsync(
            string tenantId, Guid vendorId, IReadOnlyCollection<DateOnly> coveredDays,
            IEnumerable<DailySpendBreakdownRecord> records, CancellationToken cancellationToken = default)
        {
            var count = records.Count();
            Planned += count;
            CoveredDays = coveredDays;
            return Task.FromResult(new BreakdownReplaceResult(count, 0, 0, 0));
        }

        public Task<IReadOnlyList<DailySpendBreakdownRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailySpendBreakdownRecord>>([]);
    }
}
