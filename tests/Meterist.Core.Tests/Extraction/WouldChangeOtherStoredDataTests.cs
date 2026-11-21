using Meterist.Core.Extraction;
using Meterist.Core.Persistence;

namespace Meterist.Core.Tests.Extraction;

/// <summary>
/// The dry-run footer said "Nothing would change" over a Detail line reporting 1,252 new activity rows, because it looked only at
/// spend days. This is the question it now asks of each result.
/// </summary>
public class WouldChangeOtherStoredDataTests
{
    private static VendorExtractionResult Result(
        UserUsageUpsertResult? userUsage = null,
        BreakdownReplaceResult? breakdown = null,
        ActivitySummaryUpsertResult? summaries = null,
        UserActivityReplaceResult? activity = null) =>
        new(Guid.NewGuid(), "x", VendorExtractionStatus.Succeeded, 1, "d", null, userUsage, breakdown, summaries, activity);

    [Fact]
    public void NothingStoredBeyondSpend_IsFalse()
    {
        Assert.False(Result().WouldChangeOtherStoredData());
    }

    [Fact]
    public void ResultsThatAreAllUnchanged_AreFalse()
    {
        Assert.False(Result(
            new UserUsageUpsertResult(0, 0, 40),
            new BreakdownReplaceResult(0, 0, 12, 0),
            new ActivitySummaryUpsertResult(0, 0, 2614),
            new UserActivityReplaceResult(0, 0, 1252, 0)).WouldChangeOtherStoredData());
    }

    [Fact]
    public void NewPerPersonActivity_IsTrue()
    {
        Assert.True(Result(activity: new UserActivityReplaceResult(1252, 0, 0, 0)).WouldChangeOtherStoredData());
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void PerPersonActivityThatIsInsertedUpdatedOrRemoved_IsTrue(int inserted, int updated, int removed)
    {
        Assert.True(Result(activity: new UserActivityReplaceResult(inserted, updated, removed, 0) with { Removed = removed }).WouldChangeOtherStoredData());
    }

    [Fact]
    public void EachOtherKindOfRow_CountsOnItsOwn()
    {
        Assert.True(Result(userUsage: new UserUsageUpsertResult(3, 0, 0)).WouldChangeOtherStoredData());
        Assert.True(Result(userUsage: new UserUsageUpsertResult(0, 3, 0)).WouldChangeOtherStoredData());
        Assert.True(Result(breakdown: new BreakdownReplaceResult(0, 0, 0, 2)).WouldChangeOtherStoredData());   // a removal is a change
        Assert.True(Result(summaries: new ActivitySummaryUpsertResult(0, 4, 0)).WouldChangeOtherStoredData());
        Assert.True(Result(summaries: new ActivitySummaryUpsertResult(5, 0, 0)).WouldChangeOtherStoredData());
    }
}
