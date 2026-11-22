using System.Text;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class ReportDiffTests
{
    private const string Claude = "claude-enterprise";
    private const string ChatGpt = "chatgpt-enterprise";

    // Weeks run Wednesday to Tuesday. 2026-11-04 is a Wednesday.
    private static readonly DateOnly W1 = new(2026, 11, 4);
    private static readonly DateOnly W2 = new(2026, 11, 11);
    private static readonly DateOnly W3 = new(2026, 11, 18);

    private static ManifestWeek Week(DateOnly start, decimal seat, decimal usage, int days = 7) =>
        new(start, start.AddDays(6), seat, usage, seat + usage, days);

    private static ReportManifest Manifest(
        string periodStart, string periodEnd, params (string Tenant, string Vendor, ManifestWeek[] Weeks)[] entries) => new(
        ReportManifest.CurrentVersion,
        new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc),
        DateOnly.Parse(periodStart),
        DateOnly.Parse(periodEnd),
        entries.GroupBy(e => e.Tenant)
            .Select(t => new ManifestTenant(t.Key, t.Select(e => new ManifestVendor(e.Vendor, e.Weeks)).ToList()))
            .ToList());

    private static ReportManifest Closed(params ManifestWeek[] weeks) =>
        Manifest("2026-11-04", "2026-11-24", ("t1", Claude, weeks));

    [Fact]
    public void NoChange_ReportsNothingMoved()
    {
        var weeks = new[] { Week(W1, 100m, 50m), Week(W2, 100m, 60m), Week(W3, 100m, 70m) };

        var diff = ReportDiffer.Compare(Closed(weeks), Closed(weeks));

        Assert.True(diff.Compared);
        Assert.Empty(diff.Changes);
        Assert.Equal(3, diff.WeeksCompared);
        Assert.Equal(0, diff.WeeksOnlyInCurrent + diff.WeeksOnlyInPrevious + diff.WeeksCoveringDifferentDays);
    }

    [Fact]
    public void ASubToleranceDifference_IsRoundingNotAChange()
    {
        var diff = ReportDiffer.Compare(
            Closed(Week(W1, 100m, 50.000m)),
            Closed(Week(W1, 100m, 50.004m)));

        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void ADifferenceJustPastTheTolerance_IsAChange_AndOneAtItIsNot()
    {
        var atTolerance = ReportDiffer.Compare(Closed(Week(W1, 100m, 50m)), Closed(Week(W1, 100m, 50.50m)), tolerance: 0.50m);
        var past = ReportDiffer.Compare(Closed(Week(W1, 100m, 50m)), Closed(Week(W1, 100m, 50.51m)), tolerance: 0.50m);

        Assert.Empty(atTolerance.Changes);
        Assert.Equal(WeekChangeKind.Increase, Assert.Single(past.Changes).Kind);
    }

    [Fact]
    public void ADecrease_IsFlaggedAsADecrease_AndListedBeforeIncreases()
    {
        var diff = ReportDiffer.Compare(
            Closed(Week(W1, 100m, 50m), Week(W2, 100m, 60m)),
            Closed(Week(W1, 100m, 65m), Week(W2, 100m, 45m)));

        Assert.Equal(2, diff.Changes.Count);
        Assert.Equal(WeekChangeKind.Decrease, diff.Changes[0].Kind);
        Assert.Equal(W2, diff.Changes[0].WeekStart);
        Assert.Equal(60m + 100m, diff.Changes[0].Previous.GrossSpend);
        Assert.Equal(45m + 100m, diff.Changes[0].Current.GrossSpend);
        Assert.Equal(WeekChangeKind.Increase, diff.Changes[1].Kind);
        Assert.Equal(1, diff.Decreases);
        Assert.Equal(1, diff.Increases);
    }

    [Fact]
    public void AnIncrease_OnAWeekCoveringTheSameDays_IsFlagged()
    {
        var diff = ReportDiffer.Compare(Closed(Week(W1, 100m, 50m)), Closed(Week(W1, 100m, 80m)));

        var change = Assert.Single(diff.Changes);
        Assert.Equal(WeekChangeKind.Increase, change.Kind);
        Assert.Equal("t1", change.TenantId);
        Assert.Equal(Claude, change.Vendor);
    }

    [Fact]
    public void AFallInEitherSplitIsADecrease_EvenWhenTheTotalHoldsUp()
    {
        // The seat fee fell by 40 while usage rose by 40: the gross is unchanged but usage did not fall, the seat did.
        var diff = ReportDiffer.Compare(Closed(Week(W1, 100m, 50m)), Closed(Week(W1, 60m, 90m)));

        Assert.Equal(WeekChangeKind.Decrease, Assert.Single(diff.Changes).Kind);
    }

    [Fact]
    public void AWeekInOnlyOneReport_IsNotAChange_ButIsCounted()
    {
        var diff = ReportDiffer.Compare(
            Closed(Week(W1, 100m, 50m), Week(W2, 100m, 60m)),
            Closed(Week(W2, 100m, 60m), Week(W3, 100m, 70m)));

        Assert.Empty(diff.Changes);
        Assert.Equal(1, diff.WeeksCompared);
        Assert.Equal(1, diff.WeeksOnlyInPrevious);
        Assert.Equal(1, diff.WeeksOnlyInCurrent);
    }

    [Fact]
    public void AVendorInOnlyOneReport_CountsItsWeeksAsOnlyInThatReport()
    {
        var previous = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W1, 1m, 1m)]));
        var current = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W1, 1m, 1m)]), ("t1", ChatGpt, [Week(W1, 5m, 5m)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.Empty(diff.Changes);
        Assert.Equal(1, diff.WeeksOnlyInCurrent);
    }

    [Fact]
    public void ATrailingPartialWeekThatHasSinceCompleted_RisingIsExpected_NotAChange()
    {
        // Previous report ended on Friday the 20th, so the week of the 18th covered 3 days; the new one covers all 7.
        var previous = Manifest("2026-11-04", "2026-11-20", ("t1", Claude, [Week(W3, 30m, 10m, days: 3)]));
        var current = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W3, 100m, 70m)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.Empty(diff.Changes);
        Assert.Equal(1, diff.WeeksCompared);
        Assert.Equal(0, diff.WeeksCoveringDifferentDays);
    }

    [Fact]
    public void ATrailingPartialWeekThatHasSinceCompleted_FallingIsStillFlagged()
    {
        // More days counted can never explain a lower figure.
        var previous = Manifest("2026-11-04", "2026-11-20", ("t1", Claude, [Week(W3, 30m, 100m, days: 3)]));
        var current = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W3, 30m, 40m)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.Equal(WeekChangeKind.Decrease, Assert.Single(diff.Changes).Kind);
    }

    [Fact]
    public void AWeekCoveringFewerOrDifferentDays_IsNotComparable_SoNoFalseAlarm()
    {
        // The new report starts later, so its first week counts fewer days than the previous one did.
        var previous = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W1, 100m, 50m)]));
        var current = Manifest("2026-11-07", "2026-11-24", ("t1", Claude, [Week(W1, 60m, 20m, days: 4)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.Empty(diff.Changes);
        Assert.Equal(1, diff.WeeksCoveringDifferentDays);
        Assert.Equal(0, diff.WeeksCompared);
    }

    [Fact]
    public void ADifferentTenantSet_IsNotCompared_AndSaysWhy()
    {
        var previous = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W1, 1m, 1m)]));
        var current = Manifest("2026-11-04", "2026-11-24", ("t1", Claude, [Week(W1, 9m, 9m)]), ("t2", Claude, [Week(W1, 1m, 1m)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.False(diff.Compared);
        Assert.Contains("different set of tenants", diff.NotComparedReason);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void TheSameTenantsInADifferentCase_AreTheSameSet()
    {
        var previous = Manifest("2026-11-04", "2026-11-24", ("Zelleri", Claude, [Week(W1, 100m, 50m)]));
        var current = Manifest("2026-11-04", "2026-11-24", ("zelleri", Claude, [Week(W1, 100m, 10m)]));

        var diff = ReportDiffer.Compare(previous, current);

        Assert.True(diff.Compared);
        Assert.Equal(WeekChangeKind.Decrease, Assert.Single(diff.Changes).Kind);
    }

    [Fact]
    public void ADifferentManifestVersion_IsNotCompared()
    {
        var previous = Closed(Week(W1, 1m, 1m)) with { Version = 2 };

        var diff = ReportDiffer.Compare(previous, Closed(Week(W1, 1m, 1m)));

        Assert.False(diff.Compared);
        Assert.Contains("version 2", diff.NotComparedReason);
    }

    [Fact]
    public void ANegativeTolerance_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReportDiffer.Compare(Closed(Week(W1, 1m, 1m)), Closed(Week(W1, 1m, 1m)), tolerance: -1m));

    // ---- the manifest itself ----

    [Fact]
    public void AManifest_RoundTripsThroughJson()
    {
        var original = Closed(Week(W1, 100.25m, 50.5m), Week(W2, 100m, 60m));

        Assert.True(ReportManifest.TryParse(original.ToJson(), out var read, out var problem), problem);

        Assert.Equal(original.Version, read!.Version);
        Assert.Equal(original.PeriodStart, read.PeriodStart);
        Assert.Equal(original.Tenants[0].Vendors[0].Weeks, read.Tenants[0].Vendors[0].Weeks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "version": "one" }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "version": 1, "generatedAt": "2026-11-22T00:00:00Z", "periodStart": "2026-11-04", "periodEnd": "2026-11-24", "tenants": [ { "tenantId": "t1" } ] }""")]
    [InlineData("""{ "version": 1, "generatedAt": "2026-11-22T00:00:00Z", "periodStart": "2026-11-24", "periodEnd": "2026-11-04", "tenants": [] }""")]
    public void ACorruptOrIncompleteManifest_IsRefused_WithAReasonThatQuotesNothing(string json)
    {
        Assert.False(ReportManifest.TryParse(json, out var manifest, out var problem));

        Assert.Null(manifest);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.DoesNotContain("t1", problem);
    }

    [Fact]
    public void AManifestOfAnotherVersion_IsRefusedByName()
    {
        var json = Closed(Week(W1, 1m, 1m)).ToJson().Replace("\"version\": 1", "\"version\": 7", StringComparison.Ordinal);

        Assert.False(ReportManifest.TryParse(json, out _, out var problem));
        Assert.Contains("version 7", problem);
    }

    [Fact]
    public void From_KeepsTheWeeklyFigures_AndOrdersTenantsAndVendors()
    {
        var weekly = new WeeklyTotal(0, W1, W1.AddDays(6), 100m, 50m, 150m, 7, false);
        var data = new ReportData
        {
            Period = new DateRange(W1, W1.AddDays(6)),
            WeekAnchor = W1,
            GeneratedAt = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
            Tenants =
            [
                new TenantReportData { TenantId = "b", Vendors = [Vendor(VendorCatalog.ChatGptEnterprise, weekly), Vendor(VendorCatalog.ClaudeEnterprise, weekly)] },
                new TenantReportData { TenantId = "A", Vendors = [Vendor(VendorCatalog.ClaudeEnterprise, weekly)] },
            ],
        };

        var manifest = ReportManifest.From(data);

        Assert.Equal(["A", "b"], manifest.Tenants.Select(t => t.TenantId));
        Assert.Equal([ChatGpt, Claude], manifest.Tenants[1].Vendors.Select(v => v.Vendor));
        var week = Assert.Single(manifest.Tenants[0].Vendors[0].Weeks);
        Assert.Equal((100m, 50m, 150m, 7), (week.SeatFee, week.UsageOrOverage, week.GrossSpend, week.DaysPresent));
    }

    private static VendorReportData Vendor(VendorIdentity vendor, WeeklyTotal week) =>
        new() { Vendor = vendor, Weeks = [week], CurrentRates = [] };

    // ---- the store ----

    [Fact]
    public async Task TheStore_NeverOverwritesAnEarlierManifest_AndPicksTheNewestRegardlessOfTheReportName()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var current = Closed(Week(W1, 1m, 1m));
        Assert.Null(ReportManifestStore.ResolveBaseline(Path.Combine(dir.Path, "r.html"), current).Path);   // none yet

        var first = current with { GeneratedAt = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc) };
        var second = Closed(Week(W1, 1m, 2m)) with { GeneratedAt = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc) };
        // Reports are saved under dated names, so the two manifests have different stems.
        var firstPath = await ReportManifestStore.WriteAsync(first, ReportManifestStore.PathFor(Path.Combine(dir.Path, "report-2026-11-20.html"), first.GeneratedAt), ct);
        var secondPath = await ReportManifestStore.WriteAsync(second, ReportManifestStore.PathFor(Path.Combine(dir.Path, "report-2026-11-22.html"), second.GeneratedAt), ct);

        var baseline = ReportManifestStore.ResolveBaseline(Path.Combine(dir.Path, "report-2026-11-25.html"), current);

        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));   // the earlier baseline survives the later run
        Assert.Equal(secondPath, baseline.Path);
        Assert.Contains("20261122T080000Z", secondPath);
        Assert.Equal(2m, baseline.Manifest!.Tenants[0].Vendors[0].Weeks[0].UsageOrOverage);
    }

    [Fact]
    public async Task TheStore_PrefersTheNewestManifestForTheSameTenants_OverANewerOneForOtherTenants()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var mine = Closed(Week(W1, 1m, 1m)) with { GeneratedAt = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc) };
        var other = Manifest("2026-11-04", "2026-11-24", ("someone-else", Claude, [Week(W1, 9m, 9m)]))
            with { GeneratedAt = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc) };
        var minePath = await ReportManifestStore.WriteAsync(mine, ReportManifestStore.PathFor(Path.Combine(dir.Path, "a.html"), mine.GeneratedAt), ct);
        await ReportManifestStore.WriteAsync(other, ReportManifestStore.PathFor(Path.Combine(dir.Path, "b.html"), other.GeneratedAt), ct);

        var baseline = ReportManifestStore.ResolveBaseline(Path.Combine(dir.Path, "c.html"), mine);

        Assert.Equal(minePath, baseline.Path);
    }

    [Fact]
    public async Task TheStore_WithOnlyOtherTenantsManifests_ReturnsTheNewestSoTheReportCanSayItWasNotComparable()
    {
        using var dir = new TempDir();
        var other = Manifest("2026-11-04", "2026-11-24", ("someone-else", Claude, [Week(W1, 9m, 9m)]));
        var path = await ReportManifestStore.WriteAsync(other, ReportManifestStore.PathFor(Path.Combine(dir.Path, "b.html"), other.GeneratedAt), TestContext.Current.CancellationToken);

        var baseline = ReportManifestStore.ResolveBaseline(Path.Combine(dir.Path, "c.html"), Closed(Week(W1, 1m, 1m)));

        Assert.Equal(path, baseline.Path);
        Assert.False(ReportDiffer.Compare(baseline.Manifest!, Closed(Week(W1, 1m, 1m))).Compared);
    }

    [Fact]
    public async Task TheStore_ACorruptNewestManifest_IsReportedNotSilentlySkippedWhenNothingElseMatches()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var corrupt = Path.Combine(dir.Path, "r.manifest.20261122T080000Z.json");
        await File.WriteAllTextAsync(corrupt, "{ not json", TestContext.Current.CancellationToken);

        var baseline = ReportManifestStore.ResolveBaseline(Path.Combine(dir.Path, "r.html"), Closed(Week(W1, 1m, 1m)));

        Assert.Equal(corrupt, baseline.Path);
        Assert.Null(baseline.Manifest);
        Assert.Contains("not valid", baseline.Problem);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meterist-manifest-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [Fact]
    public void TheStore_AMissingOrUnreadableFile_IsRefusedWithAReason()
    {
        Assert.False(ReportManifestStore.TryLoad(Path.Combine(Path.GetTempPath(), "meterist-no-such-" + Guid.NewGuid().ToString("N") + ".json"), out var manifest, out var problem));

        Assert.Null(manifest);
        Assert.Equal("it could not be read", problem);
    }

    // ---- the section in the report ----

    [Fact]
    public void TheSection_IsAbsentWithoutAPreviousReport_AndSaysWhyWhenNotCompared()
    {
        var none = new StringBuilder();
        ReportDiffRenderer.Render(none, null);
        Assert.Equal(string.Empty, none.ToString());

        var notCompared = new StringBuilder();
        ReportDiffRenderer.Render(notCompared, ReportDiff.NotCompared("it was <for> other tenants", 0.5m));
        Assert.Contains("Not compared", notCompared.ToString());
        Assert.Contains("&lt;for&gt;", notCompared.ToString());   // encoded
    }

    [Fact]
    public void TheSection_SaysNothingMovedWhenNothingDid_AndListsDecreasesWhenTheyDid()
    {
        var clean = new StringBuilder();
        ReportDiffRenderer.Render(clean, ReportDiffer.Compare(Closed(Week(W1, 1m, 1m)), Closed(Week(W1, 1m, 1m))));
        Assert.Contains("No weekly figure moved", clean.ToString());

        var moved = new StringBuilder();
        ReportDiffRenderer.Render(moved, ReportDiffer.Compare(Closed(Week(W1, 100m, 80m)), Closed(Week(W1, 100m, 30m))));
        var html = moved.ToString();
        Assert.Contains("<b>Decrease</b>", html);
        Assert.Contains("Claude Enterprise", html);   // the display name, not the short name
        Assert.Contains("$180.00", html);
        Assert.Contains("$130.00", html);
    }
}
