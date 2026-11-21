using Meterist.Core.Models;
using Meterist.Core.Persistence;
using Meterist.Core.Vendors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data.Tests;

public class EfMemberRosterRepositoryTests : IDisposable
{
    private static readonly Guid Vendor = VendorCatalog.ClaudeEnterprise.Id;
    private static readonly DateOnly D1 = new(2026, 11, 18);
    private static readonly DateOnly D2 = new(2026, 11, 20);

    private readonly SqliteConnection _connection;
    private readonly MeteristDbContext _context;
    private readonly EfMemberRosterRepository _repository;

    public EfMemberRosterRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options;
        _context = new MeteristDbContext(options);
        _context.Database.Migrate();
        _repository = new EfMemberRosterRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Replace_StoresEveryFieldAndReadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var added = new DateTimeOffset(2026, 3, 30, 0, 4, 14, TimeSpan.Zero);

        var result = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [Member("m1", D1, email: "a@example.test", name: "Alex Example", role: "owner", seat: "Premium", added: added, scim: true)], ct);

        Assert.Equal(new RosterSnapshotResult(1, 0, 0, 0), result);
        var snapshot = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        Assert.NotNull(snapshot);
        Assert.Equal(D1, snapshot.Date);
        var stored = Assert.Single(snapshot.Members);
        Assert.Equal("m1", stored.MemberId);
        Assert.Equal("a@example.test", stored.Email);
        Assert.Equal("Alex Example", stored.Name);
        Assert.Equal("owner", stored.Role);
        Assert.Equal("Premium", stored.SeatType);
        Assert.True(stored.IsScimManaged);
        Assert.Equal(added, stored.AddedAt);
    }

    [Fact]
    public async Task Replace_NullableFieldsStayNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1)], ct);

        var stored = Assert.Single((await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members);

        Assert.Null(stored.Email);
        Assert.Null(stored.Name);
        Assert.Null(stored.Role);
        Assert.Null(stored.SeatType);
        Assert.Null(stored.IsScimManaged);   // "the vendor did not say" is not "false"
        Assert.Null(stored.AddedAt);
    }

    [Fact]
    public async Task Replace_RetakingTheSameDay_ReportsAddedChangedUnchangedAndRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [Member("keep", D1, role: "user"), Member("change", D1, role: "user"), Member("leave", D1, role: "user")], ct);

        var result = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
            [Member("keep", D1, role: "user"), Member("change", D1, role: "owner"), Member("join", D1, role: "user")], ct);

        Assert.Equal(new RosterSnapshotResult(Inserted: 1, Updated: 1, Unchanged: 1, Removed: 1), result);
        var members = (await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members;
        Assert.Equal(["change", "join", "keep"], members.Select(m => m.MemberId).Order());
        Assert.Equal("owner", members.Single(m => m.MemberId == "change").Role);
    }

    [Fact]
    public async Task Replace_ChangeInOnlyIsScimManaged_IsAnUpdate_NotUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "member", scim: false)], ct);

        var same = await _repository.PlanReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "member", scim: false)], ct);
        var flipped = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "member", scim: true)], ct);
        var cleared = await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "member", scim: null)], ct);

        Assert.Equal(new RosterSnapshotResult(0, 0, 1, 0), same);
        Assert.Equal(new RosterSnapshotResult(0, 1, 0, 0), flipped);
        Assert.Equal(new RosterSnapshotResult(0, 1, 0, 0), cleared);
        Assert.Null(Assert.Single((await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members).IsScimManaged);
    }

    [Fact]
    public async Task Snapshots_OfDifferentDays_AreKeptSeparately_AndTheLatestWins()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1), Member("m2", D1)], ct);
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D2, [Member("m1", D2)], ct);

        var latest = await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct);
        var earlier = await _repository.GetLatestSnapshotAsync("t1", Vendor, D1, ct);
        var between = await _repository.GetLatestSnapshotAsync("t1", Vendor, new DateOnly(2026, 11, 19), ct);
        var before = await _repository.GetLatestSnapshotAsync("t1", Vendor, new DateOnly(2026, 11, 1), ct);

        Assert.Equal(D2, latest!.Date);
        Assert.Single(latest.Members);
        Assert.Equal(D1, earlier!.Date);
        Assert.Equal(2, earlier.Members.Count);
        Assert.Equal(D1, between!.Date);   // on or before: the snapshot of the 18th
        Assert.Null(before);
        Assert.Equal([D1, D2], await _repository.GetSnapshotDatesAsync("t1", Vendor, ct));
    }

    [Fact]
    public async Task Replace_NeverTouchesOtherDays_Tenants_OrVendors()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1)], ct);
        await _repository.ReplaceSnapshotAsync("t2", Vendor, D2, [Member("m1", D2, tenant: "t2")], ct);
        await _repository.ReplaceSnapshotAsync("t1", VendorCatalog.ChatGptEnterprise.Id, D2,
            [Member("m1", D2, vendor: VendorCatalog.ChatGptEnterprise.Id)], ct);

        // Retake t1/Claude on D2 with a different member: D1 and the other tenant and vendor stay as they were.
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D2, [Member("m9", D2)], ct);

        Assert.Equal(["m1"], (await _repository.GetLatestSnapshotAsync("t1", Vendor, D1, ct))!.Members.Select(m => m.MemberId));
        Assert.Equal(["m9"], (await _repository.GetLatestSnapshotAsync("t1", Vendor, D2, ct))!.Members.Select(m => m.MemberId));
        Assert.Equal(["m1"], (await _repository.GetLatestSnapshotAsync("t2", Vendor, null, ct))!.Members.Select(m => m.MemberId));
        Assert.Single((await _repository.GetLatestSnapshotAsync("t1", VendorCatalog.ChatGptEnterprise.Id, null, ct))!.Members);
    }

    [Fact]
    public async Task Plan_PredictsTheSameResult_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("a", D1, role: "user"), Member("b", D1)], ct);
        var batch = new[] { Member("a", D1, role: "owner"), Member("c", D1) };

        var plan = await _repository.PlanReplaceSnapshotAsync("t1", Vendor, D1, batch, ct);

        Assert.Equal(new RosterSnapshotResult(1, 1, 0, 1), plan);
        var untouched = (await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members;
        Assert.Equal(["a", "b"], untouched.Select(m => m.MemberId));
        Assert.Equal("user", untouched.Single(m => m.MemberId == "a").Role);

        Assert.Equal(plan, await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, batch, ct));
    }

    [Fact]
    public async Task Replace_DuplicateMemberIdInOneBatch_IsRefused_WithoutNamingThem_AndStagesNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.ReplaceSnapshotAsync(
            "t1", Vendor, D1,
            [Member("early", D1, email: "early@example.test"), Member("dup", D1, email: "secret@example.test"), Member("dup", D1)], ct));

        Assert.DoesNotContain("secret@example.test", ex.Message);
        Assert.DoesNotContain("dup", ex.Message);

        // The earlier valid row must not have been staged for a later save on the same context.
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D2, [Member("later", D2)], ct);
        Assert.Equal(["later"], (await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members.Select(m => m.MemberId));
        Assert.Null(await _repository.GetLatestSnapshotAsync("t1", Vendor, D1, ct));
    }

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("other-vendor")]
    [InlineData("other-date")]
    [InlineData("blank-id")]
    public async Task Replace_RejectsAMalformedBatch_BeforeChangingAnything(string problem)
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "user")], ct);

        var bad = problem switch
        {
            "other-tenant" => Member("x", D1, tenant: "t2"),
            "other-vendor" => Member("x", D1, vendor: VendorCatalog.ChatGptEnterprise.Id),
            "other-date" => Member("x", D2),
            _ => Member(" ", D1),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("m1", D1, role: "owner"), bad], ct));

        var stored = Assert.Single((await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members);
        Assert.Equal("user", stored.Role);
    }

    [Fact]
    public async Task AFailedSave_LeavesNothingStagedForTheNextSnapshotsSaveOnTheSameContext()
    {
        // A scheduled run shares one context across vendors. If a failed snapshot''s rows stayed tracked, the next
        // vendor''s save would persist them: rows from a snapshot that was reported as failed.
        var ct = TestContext.Current.CancellationToken;
        var failOnce = new FailOnceInterceptor();
        var options = new DbContextOptionsBuilder<MeteristDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(failOnce)
            .Options;
        await using var context = new MeteristDbContext(options);
        var repository = new EfMemberRosterRepository(context);
        var other = VendorCatalog.ChatGptEnterprise.Id;

        // Existing data to be replaced, so a failed save would also leave a Modified and a Deleted entry behind.
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1, [Member("keep", D1, role: "user"), Member("gone", D1)], ct);

        failOnce.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceSnapshotAsync(
            "t1", Vendor, D1, [Member("keep", D1, role: "owner"), Member("new", D1)], ct));

        // The next vendor''s snapshot goes through the same context and must store only its own rows.
        await repository.ReplaceSnapshotAsync("t1", other, D1, [Member("other-1", D1, vendor: other)], ct);

        Assert.DoesNotContain(context.ChangeTracker.Entries<MemberRosterRecord>(), e => e.State != EntityState.Unchanged);

        await using var verify = new MeteristDbContext(new DbContextOptionsBuilder<MeteristDbContext>().UseSqlite(_connection).Options);
        var stored = await new EfMemberRosterRepository(verify).GetLatestSnapshotAsync("t1", Vendor, null, ct);
        var storedOther = await new EfMemberRosterRepository(verify).GetLatestSnapshotAsync("t1", other, null, ct);

        // The failed replace changed nothing: original members and role intact, no "new", "gone" still there.
        Assert.Equal(["gone", "keep"], stored!.Members.Select(m => m.MemberId).Order());
        Assert.Equal("user", stored.Members.Single(m => m.MemberId == "keep").Role);
        Assert.Equal(["other-1"], storedOther!.Members.Select(m => m.MemberId));
    }

    private sealed class FailOnceInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new InvalidOperationException("Simulated save failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task GetLatestSnapshot_OrdersMembersByNameThenEmail_FallingBackToId()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.ReplaceSnapshotAsync("t1", Vendor, D1,
        [
            Member("m-zed", D1, name: "Zed"),
            Member("m-bea", D1, name: "bea"),
            Member("m-noname", D1, email: "carol@example.test"),
            Member("m-anon", D1),
        ], ct);

        var order = (await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct))!.Members.Select(m => m.MemberId);

        // Sorted by name, else email, else id, ignoring case: "bea", "carol@...", "m-anon", "Zed".
        Assert.Equal(["m-bea", "m-noname", "m-anon", "m-zed"], order);
    }

    [Fact]
    public async Task GetLatestSnapshot_WhenNoneIsStored_IsNull_AndNoDates()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await _repository.GetLatestSnapshotAsync("t1", Vendor, null, ct));
        Assert.Empty(await _repository.GetSnapshotDatesAsync("t1", Vendor, ct));
    }

    [Fact]
    public void ToString_DoesNotRevealThePerson()
    {
        var text = Member("m1", D1, email: "a@example.test", name: "Alex Example").ToString();

        Assert.DoesNotContain("a@example.test", text);
        Assert.DoesNotContain("Alex", text);
    }

    private static MemberRosterRecord Member(
        string id, DateOnly date, string? email = null, string? name = null, string? role = null,
        string? seat = null, DateTimeOffset? added = null, string tenant = "t1", Guid? vendor = null, bool? scim = null) => new()
    {
        TenantId = tenant,
        VendorId = vendor ?? Vendor,
        SnapshotDate = date,
        MemberId = id,
        Email = email,
        Name = name,
        Role = role,
        SeatType = seat,
        IsScimManaged = scim,
        AddedAt = added,
    };
}
