using Meterist.Core.Extraction;
using Meterist.Core.Models;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meterist.Data.Tests;

/// <summary>
/// Issue #64: in August a re-extraction that covered a day never stored before reported success, yet the newest day was
/// missing afterwards, and it was never root-caused. These run the real extraction service over the real EF
/// repositories against a throwaway database <i>file</i> (never the live one) and read the result back through a separate
/// connection, as the CLI's next process would. They show that, with the current code, new trailing days persist, so
/// the failure was not in the upsert paths; the post-write check in the service makes sure a recurrence would be loud.
/// </summary>
public sealed class ExtractionPersistenceIntegrationTests : IDisposable
{
    private static readonly Guid VendorA = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly Guid VendorB = VendorCatalog.ChatGptEnterprise.Id;
    private static readonly DateOnly Day1 = new(2026, 11, 1);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meterist-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public ExtractionPersistenceIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "throwaway.db");

        using var context = NewContext();
        context.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed right now is not a test failure.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReExtractingAWiderWindow_PersistsTheNewTrailingDays_InBothTables(bool monotonic)
    {
        var ct = TestContext.Current.CancellationToken;

        await RunAsync([Fake(VendorA, monotonic)], new DateRange(Day1, Day1.AddDays(4)), ct);
        await RunAsync([Fake(VendorA, monotonic)], new DateRange(Day1, Day1.AddDays(7)), ct);   // three brand-new trailing days

        var days = DailyDates("t1", VendorA);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => Day1.AddDays(i)), days);
        Assert.Equal(days, RawDates("t1", VendorA));
    }

    [Fact]
    public async Task ExtractingOneNewDayAtATime_AsTheDailyScheduledRunDoes_PersistsEachOne()
    {
        var ct = TestContext.Current.CancellationToken;

        // The same fixed start every time and a --to that moves forward by a day, as ExtractAllThroughToday does.
        for (var last = 3; last <= 9; last++)
        {
            var results = await RunAsync([Fake(VendorA, monotonic: true)], new DateRange(Day1, Day1.AddDays(last - 1)), ct);
            Assert.Equal(VendorExtractionStatus.Succeeded, Assert.Single(results).Status);

            Assert.Equal(Day1.AddDays(last - 1), DailyDates("t1", VendorA).Max());   // the newest day is there right now
            Assert.Equal(Day1.AddDays(last - 1), RawDates("t1", VendorA).Max());
        }
    }

    [Fact]
    public async Task SeveralVendorsInOneRunOnOneContext_AllPersistTheirNewestDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync([Fake(VendorA, false), Fake(VendorB, true)], new DateRange(Day1, Day1.AddDays(2)), ct);

        var results = await RunAsync([Fake(VendorA, false), Fake(VendorB, true)], new DateRange(Day1, Day1.AddDays(5)), ct);

        Assert.All(results, r => Assert.Equal(VendorExtractionStatus.Succeeded, r.Status));
        Assert.Equal(Day1.AddDays(5), DailyDates("t1", VendorA).Max());
        Assert.Equal(Day1.AddDays(5), DailyDates("t1", VendorB).Max());
        Assert.Equal(Day1.AddDays(5), RawDates("t1", VendorB).Max());
    }

    [Fact]
    public async Task TwoTenantsOnTheSameVendor_DoNotAffectEachOthersDays()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunAsync([Fake(VendorA, false)], new DateRange(Day1, Day1.AddDays(2)), ct, tenant: "t1");
        await RunAsync([Fake(VendorA, false)], new DateRange(Day1, Day1.AddDays(5)), ct, tenant: "t2");

        Assert.Equal(3, DailyDates("t1", VendorA).Count);
        Assert.Equal(6, DailyDates("t2", VendorA).Count);
    }

    [Fact]
    public async Task AnExtractionThatRunsWhileAnotherProcessHoldsTheFileOpen_StillPersists()
    {
        var ct = TestContext.Current.CancellationToken;

        // A reader holding an open connection (a sqlite3 shell, a dashboard) must not make a write vanish.
        await using var reader = new SqliteConnection($"Data Source={_path}");
        await reader.OpenAsync(ct);

        var results = await RunAsync([Fake(VendorA, true)], new DateRange(Day1, Day1.AddDays(3)), ct);

        Assert.Equal(VendorExtractionStatus.Succeeded, Assert.Single(results).Status);
        Assert.Equal(4, DailyDates("t1", VendorA).Count);
    }

    private async Task<IReadOnlyList<VendorExtractionResult>> RunAsync(
        IVendorSpendExtractor[] extractors, DateRange period, CancellationToken ct, string tenant = "t1")
    {
        // A fresh context per run, like one CLI invocation, shared by every vendor in that run.
        await using var context = NewContext();
        var service = new SpendExtractionService(
            extractors,
            extractors.Select(e => (IVendorSpendNormalizer)new DayNormalizer(e.VendorId)).ToList(),
            new EfRawExtractionRepository(context),
            new EfDailySpendRepository(context),
            new EfVendorRateConfigRepository(context),
            NullLogger<SpendExtractionService>.Instance);

        return await service.ExtractAsync(tenant, period, cancellationToken: ct);
    }

    private MeteristDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite($"Data Source={_path}").Options);

    // Read back with raw SQL on a brand-new connection: nothing from the writer's context or connection is involved.
    private List<DateOnly> DailyDates(string tenant, Guid vendor) => Query("DailySpendRecords", tenant, vendor);

    private List<DateOnly> RawDates(string tenant, Guid vendor) => Query("RawDailyExtractionRecords", tenant, vendor);

    private List<DateOnly> Query(string table, string tenant, Guid vendor)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"Date\" FROM \"{table}\" WHERE \"TenantId\" = $t AND \"VendorId\" = $v ORDER BY \"Date\"";
        command.Parameters.AddWithValue("$t", tenant);
        command.Parameters.AddWithValue("$v", vendor.ToString().ToUpperInvariant());

        var dates = new List<DateOnly>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"));
        }

        return dates;
    }

    private static IVendorSpendExtractor Fake(Guid vendor, bool monotonic) => new DayExtractor(vendor, monotonic);

    private sealed class DayExtractor(Guid vendorId, bool monotonic) : IVendorSpendExtractor
    {
        public Guid VendorId => vendorId;

        public bool SupportsOverage => true;

        public bool SupportsPerUserBreakdown => false;

        public bool RequiresMonotonicUsage => monotonic;

        public Task<RawVendorSpendData> ExtractAsync(string tenantId, DateRange period, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RawVendorSpendData
            {
                VendorId = vendorId,
                TenantId = tenantId,
                Period = period,
                RecordsByDate = period.EnumerateDays().ToDictionary(
                    d => d,
                    d => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)
                        [new Dictionary<string, object?> { ["Day"] = d.ToString("yyyy-MM-dd") }]),
            });
    }

    private sealed class DayNormalizer(Guid vendorId) : IVendorSpendNormalizer
    {
        public Guid VendorId => vendorId;

        public IReadOnlyList<DailySpendRecord> Normalize(RawVendorSpendData rawData, IReadOnlyList<VendorRateConfig> applicableRates) =>
            rawData.RecordsByDate.Keys.Order().Select(d => new DailySpendRecord
            {
                TenantId = rawData.TenantId,
                VendorId = vendorId,
                Date = d,
                SeatFee = 10m,
                UsageOrOverage = d.Day,
                GrossSpend = 10m + d.Day,
                NetSpend = 10m + d.Day,
            }).ToList();
    }
}
