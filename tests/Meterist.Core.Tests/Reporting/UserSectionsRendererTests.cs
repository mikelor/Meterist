using Meterist.Core.Auditing;
using Meterist.Core.Models;
using Meterist.Core.Reporting;
using Meterist.Core.Vendors;

namespace Meterist.Core.Tests.Reporting;

public class UserSectionsRendererTests
{
    private static readonly DateOnly Anchor = new(2026, 11, 4);   // a Wednesday
    private static readonly DateRange Period = new(Anchor, new DateOnly(2026, 11, 17));
    private static readonly VendorIdentity ChatGpt = VendorCatalog.ChatGptEnterprise;
    private static readonly VendorIdentity ClaudeEnt = VendorCatalog.ClaudeEnterprise;
    private static readonly VendorIdentity ClaudeApi = VendorCatalog.ClaudeApiPlatform;

    // ---- the people sections are left out when nothing is stored

    [Fact]
    public void ADatasetWithNoPerUserSeatOrActivityData_HasNoPeopleSections()
    {
        var html = Render(Tenant("t1"));

        Assert.DoesNotContain("Usage by person", html);
        Assert.DoesNotContain("Top consumers by spend", html);
        Assert.DoesNotContain("Idle seats", html);
        Assert.DoesNotContain("Active users and seat utilization", html);
    }

    // ---- usage by person

    [Fact]
    public void UsageByPerson_NamesEachPersonWithEmail_AndShowsSpendCreditsRequestsAndDays()
    {
        var html = Render(Tenant("t1", Vendor(ChatGpt, Usage(ChatGpt,
            [
                U("a", "Alex Example", "alex@example.test", amount: 120.5m, credits: 3000m, requests: 4000, days: 12, estimated: true),
                U("b", "Blake Sample", "blake@example.test", amount: 10m, credits: 250m, requests: 80, days: 3, estimated: true),
            ], aggregateUsage: 130.5m))));

        var section = Section(html, "Usage by person");
        Assert.Contains("Alex Example", section);
        Assert.Contains("alex@example.test", section);
        Assert.Contains("$120.50", section);
        Assert.Contains("3,000", section);   // credits column present
        Assert.Contains("4,000", section);   // requests column present
        Assert.Contains("Credits", section);
        Assert.Contains("Requests", section);
        Assert.Contains("Days active", section);
        Assert.True(section.IndexOf("Alex Example", StringComparison.Ordinal) < section.IndexOf("Blake Sample", StringComparison.Ordinal));
    }

    [Fact]
    public void UsageByPerson_HtmlEncodesNamesAndEmails()
    {
        var html = Render(Tenant("t1", Vendor(ClaudeEnt, Usage(ClaudeEnt,
            [U("x", "<script>alert(1)</script>", "a&b@example.test", amount: 1m)], aggregateUsage: 1m))));

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a&amp;b@example.test", html);
    }

    [Fact]
    public void UsageByPerson_LabelsEstimatedKeyLevelAndUnattributedRows()
    {
        var html = Render(Tenant("t1", Vendor(ClaudeApi, Usage(ClaudeApi,
            [
                U("p", "Pat Person", "pat@example.test", amount: 5m, attribution: UsageAttribution.Person),
                U("k", "ci-key", null, amount: 7m, attribution: UsageAttribution.ApiKey),
                U("unattributed", null, null, amount: 2m, attribution: UsageAttribution.Unrecognized),
                U("m", "Model Share", null, amount: 1m, attribution: UsageAttribution.Allocated, estimated: true),
            ], aggregateUsage: 15m))));

        var section = Section(html, "Usage by person");
        Assert.Contains("API key (key-level)", section);
        Assert.Contains("Unattributed", section);
        Assert.Contains("Allocated (est.)", section);
        Assert.Contains("(est.)", section);
    }

    [Fact]
    public void UsageByPerson_StatesThatSeatFeesAreNotAttributed_AndShowsTheAggregateBesideTheTotal()
    {
        var html = Render(Tenant("t1", Vendor(ClaudeEnt, Usage(ClaudeEnt,
            [U("a", "A", "a@example.test", amount: 40m)], aggregateUsage: 40m, seatFee: 1234.56m))));

        var section = Section(html, "Usage by person");
        Assert.Contains("Seat fees ($1,234.56) are not attributed to people", section);
        Assert.Contains("Reconciles", section);
        Assert.Contains("aggregate usage $40.00", section);
    }

    [Fact]
    public void UsageByPerson_FlagsAFigureThatDoesNotReconcile_WithTheDifference()
    {
        var usage = Usage(ClaudeEnt, [U("a", "A", "a@example.test", amount: 40m)], aggregateUsage: 90m, reconciles: false);

        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, usage))), "Usage by person");

        Assert.Contains("Does not reconcile", section);
        Assert.Contains("differs", section);
        Assert.Contains("$50.00", section);   // |40 - 90|
    }

    [Fact]
    public void UsageByPerson_SaysHowManyDaysWithUsageHaveNoPerUserRows()
    {
        var usage = Usage(ClaudeEnt, [U("a", "A", null, amount: 5m)], aggregateUsage: 5m, daysWithoutRows: 3);

        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, usage))), "Usage by person");

        Assert.Contains("3 day(s) with usage have no per-user rows", section);
    }

    [Fact]
    public void UsageByPerson_ShowsTheFirstTwentyFiveRows_AndSumsTheRestInOneLine_WhileTheTotalCoversEveryone()
    {
        var users = Enumerable.Range(1, 30).Select(i => U($"u{i}", $"Person {i:00}", null, amount: 31 - i)).ToList();
        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, Usage(ClaudeEnt, users, aggregateUsage: users.Sum(u => u.Amount))))), "Usage by person");

        Assert.Contains("Person 25", section);
        Assert.DoesNotContain("Person 26", section);
        Assert.Contains("5 other(s)", section);
        Assert.Contains($"{users.Sum(u => u.Amount):C2}".Replace("$", "$"), section);   // the total row includes all 30
    }

    [Fact]
    public void UsageByPerson_OmitsTheCreditsAndRequestsColumns_WhenTheVendorReportsNeither()
    {
        var section = Section(Render(Tenant("t1", Vendor(ClaudeApi, Usage(ClaudeApi,
            [U("a", "A", null, amount: 4m)], aggregateUsage: 4m)))), "Usage by person");

        Assert.DoesNotContain(">Credits<", section);
        Assert.DoesNotContain(">Requests<", section);
    }

    [Fact]
    public void UsageByPerson_NamesTheSourceOfEachVendorsFigures()
    {
        var html = Render(Tenant(
            "t1",
            Vendor(ChatGpt, Usage(ChatGpt, [U("a", "A", null, amount: 1m)], aggregateUsage: 1m)),
            Vendor(ClaudeEnt, Usage(ClaudeEnt, [U("b", "B", null, amount: 1m)], aggregateUsage: 1m))));

        var section = Section(html, "Usage by person");
        Assert.Contains("priced from credits at the configured rate (estimated)", section);
        Assert.Contains("vendor-reported cost, requests count seat-covered use too", section);
    }

    // ---- top consumers

    [Fact]
    public void TopConsumers_SumsAPersonAcrossVendorsByEmail_AndRanksByTotal()
    {
        var html = Render(Tenant(
            "t1",
            Vendor(ChatGpt, Usage(ChatGpt,
            [
                U("c1", "Casey Mixed", "CASEY@example.test", amount: 30m, estimated: true),
                U("d1", "Drew Solo", "drew@example.test", amount: 50m, estimated: true),
            ], aggregateUsage: 80m)),
            Vendor(ClaudeEnt, Usage(ClaudeEnt,
            [
                U("c2", "Casey Mixed", "casey@example.test", amount: 40m),
            ], aggregateUsage: 40m))));

        var section = Section(html, "Top consumers by spend");
        // Casey is $70 across two vendors (matched by email ignoring case) and outranks Drew at $50.
        Assert.True(section.IndexOf("Casey Mixed", StringComparison.Ordinal) < section.IndexOf("Drew Solo", StringComparison.Ordinal));
        Assert.Contains("$70.00", section);
        Assert.Contains("ChatGPT Enterprise $30.00", section);
        Assert.Contains("Claude Enterprise $40.00", section);
        Assert.Equal(1, CountOf(section, "Casey Mixed"));   // one row, not two
    }

    [Fact]
    public void TopConsumers_LeavesOutKeysAndUnattributedUsage_AndPeopleWithNoSpend()
    {
        var html = Render(Tenant("t1", Vendor(ClaudeApi, Usage(ClaudeApi,
            [
                U("p", "Real Person", "p@example.test", amount: 5m),
                U("k", "build-key", null, amount: 500m, attribution: UsageAttribution.ApiKey),
                U("unattributed", null, null, amount: 300m, attribution: UsageAttribution.Unrecognized),
                U("z", "No Spend", "z@example.test", amount: 0m, requests: 900),
            ], aggregateUsage: 805m))));

        var section = Section(html, "Top consumers by spend");
        Assert.Contains("Real Person", section);
        Assert.DoesNotContain("build-key", section);
        Assert.DoesNotContain("No Spend", section);
    }

    [Fact]
    public void TopConsumers_ShowsTenTopPeopleAtMost()
    {
        var users = Enumerable.Range(1, 14).Select(i => U($"u{i}", $"Person {i:00}", $"p{i}@example.test", amount: 100 - i)).ToList();
        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, Usage(ClaudeEnt, users, aggregateUsage: 1m)))), "Top consumers by spend");

        Assert.Contains("Person 10", section);
        Assert.DoesNotContain("Person 11", section);
    }

    [Fact]
    public void TopConsumers_MarksAnEstimatedTotal()
    {
        var section = Section(Render(Tenant("t1", Vendor(ChatGpt, Usage(ChatGpt,
            [U("a", "A", "a@example.test", amount: 9m, estimated: true)], aggregateUsage: 9m)))), "Top consumers by spend");

        Assert.Contains("(est.)", section);
    }

    // ---- active users and seats

    [Fact]
    public void ActiveUsersAndSeats_ShowsBilledReportedRosterActiveIdleAndUtilization()
    {
        var seats = Seats(ClaudeEnt, billed: 10, assigned: 13, roster: 13, active: 9, idleCount: 4);
        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, seats: seats))), "Active users and seat utilization");

        Assert.Contains(">10<", section);                 // billed
        Assert.Contains("13 <span class=\"muted-dash\">(assigned seats)", section);
        Assert.Contains(">9<", section);                  // active
        Assert.Contains(">4<", section);                  // idle
        Assert.Contains("90%", section);                  // 9 of 10 billed
        Assert.Contains("69%", section);                  // 9 of 13 on the roster
    }

    [Fact]
    public void ActiveUsersAndSeats_FlagsDrift_AndExplainsUnusedBilledSeatsWithoutCallingThemDrift()
    {
        var drift = Section(
            Render(Tenant("t1", Vendor(ClaudeEnt, seats: Seats(ClaudeEnt, billed: 10, assigned: 13, roster: 13, active: 9, idleCount: 4)))),
            "Active users and seat utilization");
        Assert.Contains("Seat drift", drift);
        Assert.Contains("10 seat(s) are billed", drift);

        var spare = Section(
            Render(Tenant("t1", Vendor(ChatGpt, seats: Seats(ChatGpt, billed: 50, assigned: null, roster: 29, active: 11, idleCount: 18)))),
            "Active users and seat utilization");
        Assert.DoesNotContain("Seat drift", spare);
        Assert.Contains("21 of the 50 billed seat(s) have no member on the roster", spare);
        Assert.Contains("Not treated as drift", spare);
    }

    [Fact]
    public void ActiveUsersAndSeats_SaysWhenIdleSeatsCouldNotBeAssessed_NeverShowingZeroIdle()
    {
        var seats = Seats(ChatGpt, billed: 50, assigned: null, roster: 29, active: 0, idleCount: 0, assessable: false, reason: "No per-user usage is stored for this vendor.");
        var section = Section(Render(Tenant("t1", Vendor(ChatGpt, seats: seats))), "Active users and seat utilization");

        Assert.Contains("idle seats not assessed", section);
        Assert.Contains("No per-user usage is stored", section);
        Assert.DoesNotContain("0%", section);
    }

    [Fact]
    public void ActiveUsersAndSeats_CarriesTheCaveatWhenStoredUsageEndsEarly()
    {
        var seats = Seats(ClaudeEnt, billed: 10, assigned: 10, roster: 10, active: 5, idleCount: 5, reason: "Stored per-user activity ends on 2026-11-01, 16 days before the audit date.");
        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, seats: seats))), "Active users and seat utilization");

        Assert.Contains("caveat", section);
        Assert.Contains("ends on 2026-11-01", section);
    }

    [Fact]
    public void ActiveUsersAndSeats_ShowsTheVendorsOwnActiveUserCounts()
    {
        var activity = new ActivityOverview(new DateOnly(2026, 11, 16), 3, 6, 9, 10, 90m, 3.5m, 14);
        var section = Section(Render(Tenant("t1", Vendor(ClaudeEnt, activity: activity))), "Active users and seat utilization");

        Assert.Contains("3 daily · 6 weekly · 9 monthly", section);
        Assert.Contains("Nov 16, 2026", section);
    }

    // ---- idle seats

    [Fact]
    public void IdleSeats_ListsNamedMembers_LongestIdleFirst_WithLastActiveOrNoneStored()
    {
        var idle = new[]
        {
            Idle("never", "Never Active", "never@example.test", null),
            Idle("was", "Was Active", "was@example.test", new DateOnly(2026, 8, 1)),
        };
        var seats = Seats(ChatGpt, billed: 5, assigned: null, roster: 5, active: 3, idleList: idle);
        var section = Section(Render(Tenant("t1", Vendor(ChatGpt, seats: seats))), "Idle seats");

        Assert.Contains("Never Active", section);
        Assert.Contains("never@example.test", section);
        Assert.Contains("none stored", section);
        Assert.Contains("Aug 1, 2026", section);
        Assert.Contains("2 idle of 5 on the roster", section);
        Assert.True(section.IndexOf("Never Active", StringComparison.Ordinal) < section.IndexOf("Was Active", StringComparison.Ordinal));
    }

    [Fact]
    public void IdleSeats_IsOmitted_WhenNoSeatIsIdleOrTheAssessmentIsUnavailable()
    {
        var none = Render(Tenant("t1", Vendor(ChatGpt, seats: Seats(ChatGpt, billed: 5, assigned: null, roster: 5, active: 5, idleCount: 0))));
        var unavailable = Render(Tenant("t1", Vendor(ChatGpt, seats: Seats(ChatGpt, billed: 5, assigned: null, roster: 5, active: 0, idleCount: 0, assessable: false, reason: "x"))));

        Assert.DoesNotContain("<h2>Idle seats</h2>", none);
        Assert.DoesNotContain("<h2>Idle seats</h2>", unavailable);
    }

    [Fact]
    public void IdleSeats_ShowsFortyAtMost_AndCountsTheRest()
    {
        var idle = Enumerable.Range(1, 45).Select(i => Idle($"u{i}", $"Idle {i:00}", null, null)).ToArray();
        var section = Section(Render(Tenant("t1", Vendor(ChatGpt, seats: Seats(ChatGpt, billed: 50, assigned: null, roster: 50, active: 5, idleList: idle)))), "Idle seats");

        Assert.Contains("Idle 40", section);
        Assert.DoesNotContain("Idle 41", section);
        Assert.Contains("5 more", section);
    }

    // ---- footnote and sections together

    [Fact]
    public void TheFootnote_SaysPeopleAreNamed_SoTheReportIsPersonalData()
    {
        var html = Render(Tenant("t1"));

        Assert.Contains("People are named", html);
        Assert.Contains("git-ignored", html);
    }

    [Fact]
    public void ThePeopleSections_SitBetweenTheSpendSectionsAndTheProjections()
    {
        var html = Render(Tenant("t1", Vendor(ClaudeEnt,
            usage: Usage(ClaudeEnt, [U("a", "A", "a@example.test", amount: 5m)], aggregateUsage: 5m),
            seats: Seats(ClaudeEnt, billed: 10, assigned: 10, roster: 10, active: 5, idleCount: 0))));

        var weekly = html.IndexOf("Weekly spend", StringComparison.Ordinal);
        var people = html.IndexOf("Usage by person", StringComparison.Ordinal);
        var projections = html.IndexOf("Annualized", StringComparison.Ordinal);
        Assert.True(weekly >= 0 && people > weekly && projections > people, $"{weekly} {people} {projections}");
    }

    // ---- builders

    private static string Render(TenantReportData tenant) =>
        ClientBenchmarkReportRenderer.Render(new ReportData
        {
            Period = Period,
            WeekAnchor = Anchor,
            Tenants = [tenant],
            GeneratedAt = new DateTime(2026, 11, 20, 12, 0, 0),
        });

    // Every catalog vendor for every tenant (the renderer looks each up), with the given ones carrying people data.
    private static TenantReportData Tenant(string tenantId, params VendorReportData[] vendors) => new()
    {
        TenantId = tenantId,
        Vendors = VendorCatalog.All.Select(v => vendors.FirstOrDefault(x => x.Vendor.Id == v.Id) ?? Vendor(v)).ToList(),
    };

    private static VendorReportData Vendor(
        VendorIdentity vendor, VendorUserUsage? usage = null, VendorSeatAudit? seats = null, ActivityOverview? activity = null) => new()
    {
        Vendor = vendor,
        Weeks = [],
        CurrentRates = [],
        UserUsage = usage,
        Seats = seats,
        Activity = activity,
    };

    private static UserUsageSummary U(
        string id, string? name, string? email, decimal amount, decimal? credits = null, long? requests = null, int days = 1,
        bool estimated = false, UsageAttribution attribution = UsageAttribution.Person) =>
        new(id, email, name, attribution, amount, credits, requests, days, estimated);

    private static VendorUserUsage Usage(
        VendorIdentity vendor, IReadOnlyList<UserUsageSummary> users, decimal aggregateUsage, decimal seatFee = 0m,
        bool reconciles = true, int daysWithoutRows = 0) =>
        new(vendor, users, seatFee, aggregateUsage,
            new UserUsageReconciliation(14, users.Sum(u => u.Amount), aggregateUsage, daysWithoutRows, reconciles));

    private static IdleSeat Idle(string id, string name, string? email, DateOnly? last) =>
        new() { MemberId = id, Name = name, Email = email, Role = "member", SeatType = "chatgpt", LastActiveBeforeWindow = last };

    private static VendorSeatAudit Seats(
        VendorIdentity vendor, int? billed, int? assigned, int? roster, int active, int idleCount = 0,
        IReadOnlyList<IdleSeat>? idleList = null, bool assessable = true, string? reason = null)
    {
        var idle = idleList ?? Enumerable.Range(0, idleCount).Select(i => Idle($"i{i}", $"Idle {i}", null, null)).ToList();
        var window = new DateRange(new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 17));
        return new VendorSeatAudit(
            vendor, new DateOnly(2026, 11, 17), billed,
            roster is null ? null : new DateOnly(2026, 11, 17), roster, new Dictionary<string, int>(),
            assigned, assigned is null ? null : new DateOnly(2026, 11, 16),
            new IdleSeatAssessment(window, assessable, reason, active, 0, 0, assessable ? idle : []), []);
    }

    private static string Section(string html, string heading)
    {
        var h = html.IndexOf($"<h2>{heading}</h2>", StringComparison.Ordinal);
        Assert.True(h >= 0, $"The report has no '{heading}' section.");
        var end = html.IndexOf("</section>", h, StringComparison.Ordinal);
        return html[h..end];
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
