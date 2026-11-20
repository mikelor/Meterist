using System.Text.Json;
using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Meterist.Vendors.Tests.ChatGptEnterprise;
using Meterist.Vendors.ClaudeEnterprise;

namespace Meterist.Vendors.Tests.ClaudeEnterprise;

public class ClaudeEnterpriseUserUsageNormalizerTests
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly Day = new(2026, 11, 3);

    [Fact]
    public void Normalize_SumsAnActorsProductsIntoOneRow_WithVendorReportedDollarsAndTheIdentity()
    {
        var normalizer = Create(out _);

        var record = Assert.Single(normalizer.Normalize(
            Raw(
                Day,
                Row("u1", "chat", amount: 1.25m, requests: 10, email: "a@example.test", name: "Alex Example"),
                Row("u1", "claude_code", amount: 2.50m, requests: 30, email: "a@example.test", name: "Alex Example")),
            []));

        Assert.Equal("u1", record.UserId);
        Assert.Equal("a@example.test", record.UserEmail);
        Assert.Equal("Alex Example", record.UserName);
        Assert.Equal(3.75m, record.Amount);
        Assert.Equal(40L, record.Requests);
        Assert.Null(record.Credits);
        Assert.False(record.IsEstimated);   // the API reports dollars per actor
        Assert.Equal(UsageAttribution.Person, record.Attribution);
        Assert.Equal(Day, record.Date);
        Assert.Equal("t1", record.TenantId);
    }

    [Fact]
    public void Normalize_KeepsAZeroCostActorWhoMadeRequests_AsAnActiveUser()
    {
        // A seat-based plan: cost is usage credits only, so a busy user can cost nothing.
        var normalizer = Create(out _);

        var record = Assert.Single(normalizer.Normalize(Raw(Day, Row("u1", "chat", amount: 0m, requests: 250)), []));

        Assert.Equal(0m, record.Amount);
        Assert.Equal(250L, record.Requests);
    }

    [Fact]
    public void Normalize_ReturnsOneRowPerActorPerDay()
    {
        var normalizer = Create(out _);
        var data = new RawVendorSpendData
        {
            VendorId = Vendor,
            TenantId = "t1",
            Period = new DateRange(Day, Day.AddDays(1)),
            RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                [Day] = [Row("u1", "chat", 1m), Row("u2", "chat", 2m)],
                [Day.AddDays(1)] = [Row("u1", "chat", 4m)],
            },
        };

        var records = normalizer.Normalize(data, []);

        Assert.Equal(3, records.Count);
        Assert.Equal(4m, records.Single(r => r.UserId == "u1" && r.Date == Day.AddDays(1)).Amount);
    }

    [Theory]
    [InlineData("user_actor", UsageAttribution.Person)]
    [InlineData("USER_ACTOR", UsageAttribution.Person)]
    [InlineData("service_account_actor", UsageAttribution.ApiKey)]
    [InlineData("api_actor", UsageAttribution.ApiKey)]
    [InlineData("federated_actor", UsageAttribution.Unrecognized)]
    [InlineData("something_new", UsageAttribution.Unrecognized)]
    [InlineData(null, UsageAttribution.Unrecognized)]
    public void Normalize_ClassifiesTheActorType(string? actorType, UsageAttribution expected)
    {
        var normalizer = Create(out _);

        var record = Assert.Single(normalizer.Normalize(Raw(Day, Row("u1", "chat", 1m, actorType: actorType)), []));

        Assert.Equal(expected, record.Attribution);
    }

    [Fact]
    public void Normalize_UnrecognizedActors_AreStoredAndNamedByTypeInOneWarning_NeverByPerson()
    {
        var normalizer = Create(out var logger);

        var records = normalizer.Normalize(
            Raw(
                Day,
                Row("u1", "chat", 1m, actorType: "user_actor", email: "a@example.test"),
                Row("f1", "chat", 1m, actorType: "federated_actor", email: "fed@example.test"),
                Row("f2", "chat", 1m, actorType: "federated_actor"),
                Row("x1", "chat", 1m, actorType: null)),
            []);

        Assert.Equal(4, records.Count);   // stored, so per-user figures still add up to the total
        var warning = Assert.Single(logger.Warnings, w => w.Contains("unrecognized"));
        Assert.Contains("federated_actor x2", warning);
        Assert.Contains("(none) x1", warning);
        Assert.DoesNotContain("example.test", warning);
    }

    [Fact]
    public void Normalize_CostWithNoActor_GoesInOneLabelledBucket_SoTheTotalStillAddsUp()
    {
        var normalizer = Create(out var logger);

        var records = normalizer.Normalize(
            Raw(Day, Row("u1", "chat", 1m), Row(null, "chat", 2m), Row(null, "cowork", 3m)), []);

        var bucket = Assert.Single(records, r => r.UserId == ClaudeEnterpriseUserUsageNormalizer.UnattributedUserId);
        Assert.Equal(5m, bucket.Amount);
        Assert.Equal(UsageAttribution.Unrecognized, bucket.Attribution);   // never a person
        Assert.Equal(6m, records.Sum(r => r.Amount));
        Assert.Contains(logger.Warnings, w => w.Contains("(no actor)"));
    }

    [Fact]
    public void Normalize_RowsStoredBeforeTheActorTypeWasKept_AreTreatedAsPeople()
    {
        // The legacy per-user report has no ActorType key at all (not even a null).
        var normalizer = Create(out var logger);
        var legacy = new Dictionary<string, object?>
        {
            [ClaudeAnalyticsCostRowFields.RecordDate] = Day,
            [ClaudeAnalyticsCostRowFields.AmountUsd] = 2m,
            [ClaudeAnalyticsCostRowFields.UserId] = "u1",
            [ClaudeAnalyticsCostRowFields.UserEmail] = "a@example.test",
            [ClaudeAnalyticsCostRowFields.UserName] = "Alex Example",
        };

        var record = Assert.Single(normalizer.Normalize(Raw(Day, legacy), []));

        Assert.Equal(UsageAttribution.Person, record.Attribution);
        Assert.Null(record.Requests);   // the legacy report carries no request count
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Normalize_DaysWithNoRows_ProduceNothing()
    {
        Assert.Empty(Create(out _).Normalize(Raw(Day), []));
    }

    [Fact]
    public async Task Backfill_RebuildsPerUserRowsFromStoredLegacyRawJson_WithoutTheApi()
    {
        // Raw rows exactly as the extractors stored them: serialized with System.Text.Json (a DateOnly
        // becomes a string, a decimal a number). They are read back, normalized and stored.
        var legacyRows = new object[]
        {
            new Dictionary<string, object?>
            {
                [ClaudeAnalyticsCostRowFields.RecordDate] = Day,
                [ClaudeAnalyticsCostRowFields.AmountUsd] = 1.5m,
                [ClaudeAnalyticsCostRowFields.UserId] = "u1",
                [ClaudeAnalyticsCostRowFields.UserEmail] = "a@example.test",
                [ClaudeAnalyticsCostRowFields.UserName] = "Alex Example",
            },
            new Dictionary<string, object?>
            {
                [ClaudeAnalyticsCostRowFields.RecordDate] = Day,
                [ClaudeAnalyticsCostRowFields.AmountUsd] = 0.5m,
                [ClaudeAnalyticsCostRowFields.UserId] = "u1",
                [ClaudeAnalyticsCostRowFields.UserEmail] = "a@example.test",
                [ClaudeAnalyticsCostRowFields.UserName] = "Alex Example",
            },
        };
        var raw = new StoredRaw(new Dictionary<DateOnly, string>
        {
            [Day] = JsonSerializer.Serialize(legacyRows),
            [Day.AddDays(1)] = "[]",
        });
        var users = new CapturingUserRepository();
        var backfill = new UserUsageBackfill(
            raw, [Create(out _)], new NoRates(), users);

        var period = new DateRange(Day, Day.AddDays(1));
        var result = await backfill.RunAsync("t1", Vendor, period, dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.DaysRead);
        Assert.Equal(1, result.RowsNormalized);
        var stored = Assert.Single(users.Stored);
        Assert.Equal("u1", stored.UserId);
        Assert.Equal(2.0m, stored.Amount);
        Assert.Equal(UsageAttribution.Person, stored.Attribution);
        Assert.Equal("Alex Example", stored.UserName);
    }

    [Fact]
    public async Task Backfill_RebuildsRowsFromTheNewEndpointsRawJson_IncludingRequestsAndActorType()
    {
        var rows = new object[]
        {
            Row("u1", "chat", 1m, requests: 12, actorType: "user_actor"),
            Row("s1", "claude_code", 4m, requests: 3, actorType: "service_account_actor"),
        };
        var raw = new StoredRaw(new Dictionary<DateOnly, string> { [Day] = JsonSerializer.Serialize(rows) });
        var users = new CapturingUserRepository();
        var backfill = new UserUsageBackfill(raw, [Create(out _)], new NoRates(), users);

        await backfill.RunAsync("t1", Vendor, new DateRange(Day, Day), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(12L, users.Stored.Single(r => r.UserId == "u1").Requests);
        Assert.Equal(UsageAttribution.ApiKey, users.Stored.Single(r => r.UserId == "s1").Attribution);
    }

    [Fact]
    public async Task Backfill_DryRun_PlansAndWritesNothing()
    {
        var raw = new StoredRaw(new Dictionary<DateOnly, string>
        {
            [Day] = JsonSerializer.Serialize(new object[] { Row("u1", "chat", 1m) }),
        });
        var users = new CapturingUserRepository();
        var backfill = new UserUsageBackfill(raw, [Create(out _)], new NoRates(), users);

        var result = await backfill.RunAsync("t1", Vendor, new DateRange(Day, Day), dryRun: true, TestContext.Current.CancellationToken);

        Assert.Empty(users.Stored);
        Assert.Equal(1, users.Planned);
        Assert.Equal(1, result.Upsert.Inserted);
    }

    [Fact]
    public async Task Backfill_ForAVendorWithNoUserNormalizer_SaysSo()
    {
        var backfill = new UserUsageBackfill(
            new StoredRaw(new Dictionary<DateOnly, string>()), [Create(out _)], new NoRates(), new CapturingUserRepository());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.RunAsync(
            "t1", VendorCatalog.GeminiEnterprise.Id, new DateRange(Day, Day), false, TestContext.Current.CancellationToken));

        Assert.Contains("Gemini Enterprise", ex.Message);
        Assert.Contains("nothing to backfill", ex.Message);
    }

    [Fact]
    public void ParseRows_ReadsNumbersAsDecimal_StringsBooleansAndNulls_AndKeepsNestedJson()
    {
        var rows = UserUsageBackfill.ParseRows(
            """[ { "a": 1.25, "b": "text", "c": true, "d": null, "e": 7, "f": [1,2] } ]""");

        var row = Assert.Single(rows);
        Assert.Equal(1.25m, row["a"]);
        Assert.Equal("text", row["b"]);
        Assert.Equal(true, row["c"]);
        Assert.Null(row["d"]);
        Assert.Equal(7m, row["e"]);
        Assert.IsType<JsonElement>(row["f"]);
    }

    private static ClaudeEnterpriseUserUsageNormalizer Create(out CapturingLogger<ClaudeEnterpriseUserUsageNormalizer> logger)
    {
        logger = new CapturingLogger<ClaudeEnterpriseUserUsageNormalizer>();
        return new ClaudeEnterpriseUserUsageNormalizer(logger);
    }

    private static RawVendorSpendData Raw(DateOnly date, params IReadOnlyDictionary<string, object?>[] rows) => new()
    {
        VendorId = Vendor,
        TenantId = "t1",
        Period = new DateRange(date, date),
        RecordsByDate = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyDictionary<string, object?>>> { [date] = rows },
    };

    private static Dictionary<string, object?> Row(
        string? userId, string product, decimal amount, long? requests = null, string? actorType = "user_actor",
        string? email = null, string? name = null) => new()
    {
        [ClaudeAnalyticsCostRowFields.RecordDate] = Day,
        [ClaudeAnalyticsCostRowFields.AmountUsd] = amount,
        [ClaudeAnalyticsCostRowFields.UserId] = userId,
        [ClaudeAnalyticsCostRowFields.UserEmail] = email,
        [ClaudeAnalyticsCostRowFields.UserName] = name,
        [ClaudeAnalyticsCostRowFields.Product] = product,
        [ClaudeAnalyticsCostRowFields.Requests] = requests,
        [ClaudeAnalyticsCostRowFields.ActorType] = actorType,
        [ClaudeAnalyticsCostRowFields.ActorDeleted] = false,
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

    private sealed class NoRates : IVendorRateConfigRepository
    {
        public Task AddAsync(VendorRateConfig rate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CloseOpenEndedRateAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly newEffectiveFrom,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<VendorRateConfig>> GetApplicableRatesAsync(
            string tenantId, Guid vendorId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VendorRateConfig>>([]);

        public Task<DateOnly?> FindNextEffectiveFromAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly afterEffectiveFrom,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<VendorRateConfig>> FindOverlappingRatesAsync(
            string? tenantId, Guid vendorId, string? modelOrSku, DateOnly effectiveFrom, DateOnly? effectiveTo,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CapturingUserRepository : IUserDailyUsageRepository
    {
        public List<UserDailyUsageRecord> Stored { get; } = [];

        public int Planned { get; private set; }

        public Task<UserUsageUpsertResult> UpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Stored.AddRange(list);
            return Task.FromResult(new UserUsageUpsertResult(list.Count, 0, 0));
        }

        public Task<UserUsageUpsertResult> PlanUpsertAsync(
            IEnumerable<UserDailyUsageRecord> records, CancellationToken cancellationToken = default)
        {
            var count = records.Count();
            Planned += count;
            return Task.FromResult(new UserUsageUpsertResult(count, 0, 0));
        }

        public Task<IReadOnlyList<UserDailyUsageRecord>> GetAsync(
            string tenantId, Guid vendorId, DateRange range, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserDailyUsageRecord>>([]);
    }
}
