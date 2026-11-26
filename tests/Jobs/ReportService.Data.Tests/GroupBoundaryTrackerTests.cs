using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The group tracking a page-boundary cut prefers: a group is a contiguous run of
/// records with an equal group key (the field the manifest names in
/// partitioning.groupKey, a string or an integer, compared by type and exact value —
/// ADR-022 §3.2 item 5). A boundary is where that key changes; a key that reappears
/// after a different one starts a new group (contiguity is a producer obligation).
/// </summary>
public sealed class GroupBoundaryTrackerTests
{
    [Fact]
    public void TheFirstRecordAlwaysStartsAGroup()
    {
        var tracker = new GroupBoundaryTracker();

        Assert.True(tracker.Observe(GroupKey.Text("a")));
    }

    [Fact]
    public void AnEqualKeyContinuesTheSameGroup()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Text("a"));

        Assert.False(tracker.Observe(GroupKey.Text("a")));
    }

    [Fact]
    public void AChangedKeyStartsANewGroup()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Text("a"));

        Assert.True(tracker.Observe(GroupKey.Text("b")));
    }

    [Fact]
    public void AKeyThatReappearsAfterAnotherStartsANewGroup()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Text("a"));
        tracker.Observe(GroupKey.Text("b"));

        // "a" is contiguous no longer: its reappearance is a new group, not the first one resumed.
        Assert.True(tracker.Observe(GroupKey.Text("a")));
    }

    [Fact]
    public void AStringKeyAndAnIntegerKeyOfTheSameTextAreDifferentGroups()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Text("5"));

        Assert.True(tracker.Observe(GroupKey.Integer(5)));
    }

    [Fact]
    public void EqualIntegerKeysContinueTheSameGroup()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Integer(5));

        Assert.False(tracker.Observe(GroupKey.Integer(5)));
    }

    [Fact]
    public void SameGroupComparesTypeAndExactValue()
    {
        Assert.True(GroupKey.Text("a").SameGroup(GroupKey.Text("a")));
        Assert.False(GroupKey.Text("a").SameGroup(GroupKey.Text("b")));
        Assert.True(GroupKey.Integer(7).SameGroup(GroupKey.Integer(7)));
        Assert.False(GroupKey.Integer(7).SameGroup(GroupKey.Integer(8)));
        Assert.False(GroupKey.Text("5").SameGroup(GroupKey.Integer(5)));
    }

    [Fact]
    public void TheCurrentKeyIsTheOneLastObserved()
    {
        var tracker = new GroupBoundaryTracker();

        tracker.Observe(GroupKey.Text("a"));
        tracker.Observe(GroupKey.Text("b"));

        Assert.True(tracker.Current.SameGroup(GroupKey.Text("b")));
    }
}
