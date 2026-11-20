using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Meterist.Vendors.ChatGptEnterprise;
using Microsoft.Extensions.Logging;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

public class ChatGptEnterpriseUserUsageNormalizerTests
{
    private static readonly Guid Vendor = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly DateOnly Day = new(2026, 11, 3);
    private static readonly TimeProvider Now = new FixedTime(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Normalize_PricesCreditsAtTheConfiguredRate_AndKeepsTheIdentityAndCounts()
    {
        var normalizer = Create(out _);

        var records = normalizer.Normalize(
            Raw(Day, UserRow("user-1", "a@example.test", credits: 100m, estimated: 4.5m, messages: 7)),
            [CreditRate(0.04m)]);

        var record = Assert.Single(records);
        Assert.Equal("user-1", record.UserId);
        Assert.Equal("a@example.test", record.UserEmail);
        Assert.Null(record.UserName);
        Assert.Equal(4.0m, record.Amount);          // 100 credits x 0.04, not the vendor's 4.5
        // Dollars derived from credits are never a billed per-user figure, even at a configured rate.
        Assert.True(record.IsEstimated);
        Assert.Equal(100m, record.Credits);
        Assert.Equal(7, record.Requests);
        Assert.Equal(UsageAttribution.Person, record.Attribution);
        Assert.Equal(Day, record.Date);
        Assert.Equal("t1", record.TenantId);
    }

    [Fact]
    public void Normalize_WithNoConfiguredRate_FallsBackToTheVendorEstimate_AndMarksItEstimated()
    {
        var normalizer = Create(out var logger);

        var record = Assert.Single(normalizer.Normalize(
            Raw(Day, UserRow("user-1", "a@example.test", credits: 100m, estimated: 4.5m)), []));

        Assert.Equal(4.5m, record.Amount);
        Assert.True(record.IsEstimated);
        Assert.Contains(logger.Warnings, w => w.Contains("estimate"));
    }

    [Fact]
    public void Normalize_AddsNativeUsdUsageWithoutRunningItThroughTheCreditRate()
    {
        var normalizer = Create(out _);

        var record = Assert.Single(normalizer.Normalize(
            Raw(Day, UserRow("user-1", null, credits: 100m, estimated: 4m, costUsd: 2m)), [CreditRate(0.04m)]));

        Assert.Equal(6.0m, record.Amount);
        Assert.True(record.IsEstimated);   // part of it is converted credits
    }

    [Fact]
    public void Normalize_NativeUsdOnly_IsVendorReportedAndNotEstimated()
    {
        var normalizer = Create(out var logger);

        var record = Assert.Single(normalizer.Normalize(
            Raw(Day, UserRow("user-1", null, credits: 0m, estimated: null, costUsd: 3m)), [CreditRate(0.04m)]));

        Assert.Equal(3m, record.Amount);
        Assert.False(record.IsEstimated);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_OnlyAccountUserIsAPerson_OtherActorTypesAreStoredAsUnrecognized_AndReported()
    {
        var normalizer = Create(out var logger);

        var records = normalizer.Normalize(
            Raw(
                Day,
                UserRow("u1", "a@example.test", credits: 1m, actorType: "ACCOUNT_USER"),
                UserRow("u2", "b@example.test", credits: 1m, actorType: "account_user"),
                UserRow("u3", "ci@example.test", credits: 1m, actorType: "SERVICE_ACCOUNT"),
                UserRow("u4", "d@example.test", credits: 1m, actorType: "SERVICE_ACCOUNT"),
                UserRow("u5", "e@example.test", credits: 1m, actorType: null)),
            [CreditRate(0.04m)]);

        Assert.Equal(5, records.Count);   // stored, so totals still reconcile
        Assert.Equal(
            [UsageAttribution.Person, UsageAttribution.Person],
            records.Where(r => r.UserId is "u1" or "u2").Select(r => r.Attribution));
        Assert.All(
            records.Where(r => r.UserId is "u3" or "u4" or "u5"),
            r => Assert.Equal(UsageAttribution.Unrecognized, r.Attribution));

        var warning = Assert.Single(logger.Warnings, w => w.Contains("unrecognized"));
        Assert.Contains("SERVICE_ACCOUNT x2", warning);
        Assert.Contains("(none) x1", warning);
        Assert.Contains("3 per-user", warning);
        Assert.DoesNotContain("@example.test", warning);
    }

    [Fact]
    public void Normalize_OnlyPeople_RaisesNoActorTypeWarning()
    {
        var normalizer = Create(out var logger);

        normalizer.Normalize(Raw(Day, UserRow("u1", null, credits: 1m)), [CreditRate(0.04m)]);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("unrecognized"));
    }

    [Fact]
    public void Normalize_IgnoresWorkspaceAndCostsRows_AndDaysWithNoUserRows()
    {
        var normalizer = Create(out _);
        var data = new RawVendorSpendData
        {
            VendorId = Vendor,
            TenantId = "t1",
            Period = new DateRange(Day, Day.AddDays(1)),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [WorkspaceRow(100m), CostsRow()],
                [Day.AddDays(1)] = [],
            },
        };

        Assert.Empty(normalizer.Normalize(data, []));
    }

    [Fact]
    public void Normalize_SkipsARowWithNoUserId_AndSaysHowMany()
    {
        var normalizer = Create(out var logger);

        var records = normalizer.Normalize(
            Raw(Day, UserRow(null, "ghost@example.test", credits: 1m), UserRow("user-1", null, credits: 1m)), []);

        Assert.Equal(["user-1"], records.Select(r => r.UserId));
        var warning = Assert.Single(logger.Warnings, w => w.Contains("no user id"));
        Assert.Contains("1 per-user", warning);
        Assert.DoesNotContain("ghost@example.test", warning);
    }

    [Fact]
    public void Normalize_UsersThatAddUpToTheWorkspace_RaiseNoWarning()
    {
        var normalizer = Create(out var logger);

        normalizer.Normalize(
            Raw(Day, WorkspaceRow(300m), UserRow("u1", null, credits: 100m), UserRow("u2", null, credits: 200m)),
            [CreditRate(0.04m)]);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_UsersThatDoNotAddUp_WarnWithDatesAndFigures_ButNeverNamePeople()
    {
        var normalizer = Create(out var logger);

        var records = normalizer.Normalize(
            Raw(Day, WorkspaceRow(1000m), UserRow("u1", "secret@example.test", credits: 400m)),
            [CreditRate(0.04m)]);

        Assert.Single(records);   // the shortfall is reported, not "fixed"
        var warning = Assert.Single(logger.Warnings, w => w.Contains("do not add up"));
        Assert.Contains("2026-11-03", warning);
        Assert.Contains("1000.0", warning);
        Assert.Contains("400.0", warning);
        Assert.DoesNotContain("secret@example.test", warning);
        Assert.DoesNotContain("u1", warning);
    }

    [Fact]
    public void Normalize_DoesNotReconcileTheDayStillInProgress()
    {
        var normalizer = Create(out var logger);
        var today = new DateOnly(2026, 11, 20);

        normalizer.Normalize(Raw(today, WorkspaceRow(1000m), UserRow("u1", null, credits: 1m)), [CreditRate(0.04m)]);

        Assert.Empty(logger.Warnings);
    }

    private static ChatGptEnterpriseUserUsageNormalizer Create(out CapturingLogger<ChatGptEnterpriseUserUsageNormalizer> logger)
    {
        logger = new CapturingLogger<ChatGptEnterpriseUserUsageNormalizer>();
        return new ChatGptEnterpriseUserUsageNormalizer(logger, Now);
    }

    private static VendorRateConfig CreditRate(decimal rate) => new()
    {
        TenantId = "t1",
        VendorId = Vendor,
        RateType = ChatGptRateKeys.CreditToUsdRateType,
        ModelOrSku = ChatGptRateKeys.CreditUsdModelOrSku,
        Rate = rate,
        EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private static RawVendorSpendData Raw(DateOnly date, params IReadOnlyDictionary<string, object?>[] rows) => new()
    {
        VendorId = Vendor,
        TenantId = "t1",
        Period = new DateRange(date, date),
        RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> { [date] = rows },
    };

    private static IReadOnlyDictionary<string, object?> UserRow(
        string? userId, string? email, decimal credits, decimal? estimated = null, decimal? costUsd = null, int messages = 1,
        string? actorType = "ACCOUNT_USER") =>
        new Dictionary<string, object?>
        {
            [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.DailyUsageUser,
            [ChatGptDailyUsageRowFields.UserId] = userId,
            [ChatGptDailyUsageRowFields.UserEmail] = email,
            [ChatGptDailyUsageRowFields.ActorType] = actorType,
            [ChatGptDailyUsageRowFields.Credits] = credits,
            [ChatGptDailyUsageRowFields.CostUsd] = costUsd,
            [ChatGptDailyUsageRowFields.EstimatedCostUsd] = estimated,
            [ChatGptDailyUsageRowFields.Messages] = messages,
        };

    private static IReadOnlyDictionary<string, object?> WorkspaceRow(decimal credits) => new Dictionary<string, object?>
    {
        [ChatGptDailyUsageRowFields.Source] = ChatGptRowSources.DailyUsage,
        [ChatGptDailyUsageRowFields.Credits] = credits,
        [ChatGptDailyUsageRowFields.Messages] = 0,
    };

    private static IReadOnlyDictionary<string, object?> CostsRow() => new Dictionary<string, object?>
    {
        [ChatGptCostRowFields.CostValue] = 5m,
        [ChatGptCostRowFields.CostUnit] = "CREDITS",
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
