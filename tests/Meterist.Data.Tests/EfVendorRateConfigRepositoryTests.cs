using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfVendorRateConfigRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfVendorRateConfigRepository _repository;

    public EfVendorRateConfigRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.EnsureCreated();
        _repository = new EfVendorRateConfigRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetApplicableRatesAsync_TenantOverride_ReplacesPublicDefaultForSameModelOrSku()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        await _repository.AddAsync(Rate(null, vendorId, "seat", 25m, period.Start), TestContext.Current.CancellationToken);
        await _repository.AddAsync(Rate("zelleri", vendorId, "seat", 30m, period.Start), TestContext.Current.CancellationToken);

        var rates = await _repository.GetApplicableRatesAsync(
            "zelleri", vendorId, period, TestContext.Current.CancellationToken);

        var rate = Assert.Single(rates);
        Assert.Equal("zelleri", rate.TenantId);
        Assert.Equal(30m, rate.Rate);
    }

    [Fact]
    public async Task GetApplicableRatesAsync_DistinctModelOrSkuBuckets_AreBothReturned()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        await _repository.AddAsync(Rate("zelleri", vendorId, "seat", 30m, period.Start), TestContext.Current.CancellationToken);
        await _repository.AddAsync(Rate("zelleri", vendorId, "credit-usd", 0.07m, period.Start), TestContext.Current.CancellationToken);

        var rates = await _repository.GetApplicableRatesAsync(
            "zelleri", vendorId, period, TestContext.Current.CancellationToken);

        Assert.Equal(2, rates.Count);
        Assert.Contains(rates, r => r.ModelOrSku == "seat");
        Assert.Contains(rates, r => r.ModelOrSku == "credit-usd");
    }

    [Fact]
    public async Task GetApplicableRatesAsync_OtherTenantsRatesAreNeverReturned()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        await _repository.AddAsync(Rate("other-tenant", vendorId, "seat", 99m, period.Start), TestContext.Current.CancellationToken);

        var rates = await _repository.GetApplicableRatesAsync(
            "zelleri", vendorId, period, TestContext.Current.CancellationToken);

        Assert.Empty(rates);
    }

    [Fact]
    public async Task GetApplicableRatesAsync_ExcludesRowsWhosePeriodDoesNotOverlap()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;

        var expired = Rate("zelleri", vendorId, "seat", 20m, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        await _repository.AddAsync(expired, TestContext.Current.CancellationToken);

        var notYetEffective = Rate("zelleri", vendorId, "seat", 40m, new DateOnly(2026, 9, 1));
        await _repository.AddAsync(notYetEffective, TestContext.Current.CancellationToken);

        var openEnded = Rate("zelleri", vendorId, "credit-usd", 0.07m, new DateOnly(2026, 1, 1));
        await _repository.AddAsync(openEnded, TestContext.Current.CancellationToken);

        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var rates = await _repository.GetApplicableRatesAsync(
            "zelleri", vendorId, period, TestContext.Current.CancellationToken);

        var rate = Assert.Single(rates);
        Assert.Equal("credit-usd", rate.ModelOrSku);
    }

    [Fact]
    public async Task CloseOpenEndedRateAsync_ClosesPreviousOpenEndedRow_InTheSameScope()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        var original = Rate("zelleri", vendorId, "seat", 25m, new DateOnly(2026, 1, 1));
        await _repository.AddAsync(original, TestContext.Current.CancellationToken);

        var closedCount = await _repository.CloseOpenEndedRateAsync(
            "zelleri", vendorId, "seat", new DateOnly(2026, 8, 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, closedCount);

        var period = new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var rates = await _repository.GetApplicableRatesAsync(
            "zelleri", vendorId, period, TestContext.Current.CancellationToken);

        var rate = Assert.Single(rates);
        Assert.Equal(new DateOnly(2026, 7, 31), rate.EffectiveTo);
    }

    [Fact]
    public async Task CloseOpenEndedRateAsync_DoesNotTouchRowsInOtherScopes()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;

        var otherTenant = Rate("other-tenant", vendorId, "seat", 25m, new DateOnly(2026, 1, 1));
        var otherModelOrSku = Rate("zelleri", vendorId, "credit-usd", 0.07m, new DateOnly(2026, 1, 1));
        var alreadyClosed = Rate("zelleri", vendorId, "seat", 20m, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        await _repository.AddAsync(otherTenant, TestContext.Current.CancellationToken);
        await _repository.AddAsync(otherModelOrSku, TestContext.Current.CancellationToken);
        await _repository.AddAsync(alreadyClosed, TestContext.Current.CancellationToken);

        var closedCount = await _repository.CloseOpenEndedRateAsync(
            "zelleri", vendorId, "seat", new DateOnly(2026, 8, 1), TestContext.Current.CancellationToken);

        Assert.Equal(0, closedCount);
    }

    [Fact]
    public async Task FindNextEffectiveFromAsync_ReturnsEarliestLaterRate_InSameScope()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 40m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 45m, new DateOnly(2027, 1, 1)), TestContext.Current.CancellationToken);

        var next = await _repository.FindNextEffectiveFromAsync(
            "zelleri", vendorId, "seat", new DateOnly(2025, 12, 5), TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 7, 14), next);
    }

    [Fact]
    public async Task FindNextEffectiveFromAsync_ReturnsNull_WhenNoLaterRateExists()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 40m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);

        var next = await _repository.FindNextEffectiveFromAsync(
            "zelleri", vendorId, "seat", new DateOnly(2026, 8, 1), TestContext.Current.CancellationToken);

        Assert.Null(next);
    }

    [Fact]
    public async Task FindNextEffectiveFromAsync_IgnoresRowsInDifferentScope()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("other-tenant", vendorId, "seat", 40m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "credit-usd", 0.07m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);

        var next = await _repository.FindNextEffectiveFromAsync(
            "zelleri", vendorId, "seat", new DateOnly(2025, 12, 5), TestContext.Current.CancellationToken);

        Assert.Null(next);
    }

    [Fact]
    public async Task FindOverlappingRatesAsync_DetectsOverlap_WithOpenEndedRow()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 40m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);

        var overlaps = await _repository.FindOverlappingRatesAsync(
            "zelleri", vendorId, "seat", new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 1),
            TestContext.Current.CancellationToken);

        Assert.Single(overlaps);
    }

    [Fact]
    public async Task FindOverlappingRatesAsync_DetectsOverlap_WithClosedRow()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 33m, new DateOnly(2025, 7, 14), new DateOnly(2026, 7, 13)),
            TestContext.Current.CancellationToken);

        var overlaps = await _repository.FindOverlappingRatesAsync(
            "zelleri", vendorId, "seat", new DateOnly(2025, 12, 5), new DateOnly(2026, 7, 13),
            TestContext.Current.CancellationToken);

        Assert.Single(overlaps);
    }

    [Fact]
    public async Task FindOverlappingRatesAsync_ReturnsEmpty_WhenNoOverlap()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("zelleri", vendorId, "seat", 40m, new DateOnly(2026, 7, 14)), TestContext.Current.CancellationToken);

        var overlaps = await _repository.FindOverlappingRatesAsync(
            "zelleri", vendorId, "seat", new DateOnly(2025, 12, 5), new DateOnly(2026, 7, 13),
            TestContext.Current.CancellationToken);

        Assert.Empty(overlaps);
    }

    [Fact]
    public async Task FindOverlappingRatesAsync_ReturnsEmpty_ForDifferentScope()
    {
        var vendorId = VendorCatalog.ChatGptEnterprise.Id;
        await _repository.AddAsync(
            Rate("other-tenant", vendorId, "seat", 40m, new DateOnly(2026, 1, 1)), TestContext.Current.CancellationToken);

        var overlaps = await _repository.FindOverlappingRatesAsync(
            "zelleri", vendorId, "seat", new DateOnly(2026, 1, 1), null, TestContext.Current.CancellationToken);

        Assert.Empty(overlaps);
    }

    private static VendorRateConfig Rate(
        string? tenantId, Guid vendorId, string modelOrSku, decimal rate, DateOnly effectiveFrom,
        DateOnly? effectiveTo = null) => new()
    {
        TenantId = tenantId,
        VendorId = vendorId,
        RateType = modelOrSku == "seat" ? "per-seat" : "credit-to-usd",
        ModelOrSku = modelOrSku,
        Rate = rate,
        EffectiveFrom = effectiveFrom,
        EffectiveTo = effectiveTo,
    };
}
