using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource.Resolution;

namespace ReportService.TemplateSource.Tests.Resolution;

/// <summary>
/// The candidate set of one resolution: the union of the listed names and the
/// taken versions, the exclusions with their reasons, and the total order the
/// walk uses — precedence first, then the taken copy, then the higher full
/// version string in ordinal order.
/// </summary>
public sealed class TemplateVersionCandidatesTests
{
    private const string Consumer = "billing-system";

    [Fact]
    public void AVersionBothListedAndTakenIsOneCandidateWithItsRecord()
    {
        var taken = TemplateVersionRecords.Sample(version: "1.1.0", owner: Consumer);

        var candidates = TemplateVersionCandidates.Build(["1.0.0", "1.1.0"], [taken], Consumer);

        Assert.Equal(["1.1.0", "1.0.0"], candidates.Considered.Select(candidate => candidate.Version));
        Assert.Same(taken, candidates.Considered[0].Taken);
        Assert.Empty(candidates.Skipped);
    }

    [Fact]
    public void ANameThatIsNotASemanticVersionIsExcludedWithItsReason()
    {
        var candidates = TemplateVersionCandidates.Build(["v1.3.0", "1.0.0"], [], Consumer);

        Assert.Equal(["1.0.0"], candidates.Considered.Select(candidate => candidate.Version));
        var skip = Assert.Single(candidates.Skipped);
        Assert.Equal("v1.3.0", skip.Version);
        Assert.Equal(CandidateSkipReasons.NotSemVer, skip.Reason);
        Assert.Null(skip.Code);
        Assert.Null(skip.RefusalReason);
    }

    [Fact]
    public void APreReleaseIsExcludedWithItsReason()
    {
        var candidates = TemplateVersionCandidates.Build(["1.0.0", "2.0.0-beta.1"], [], Consumer);

        Assert.Equal(["1.0.0"], candidates.Considered.Select(candidate => candidate.Version));
        var skip = Assert.Single(candidates.Skipped);
        Assert.Equal("2.0.0-beta.1", skip.Version);
        Assert.Equal(CandidateSkipReasons.PreRelease, skip.Reason);
    }

    [Fact]
    public void ATakenVersionOfAnotherConsumerIsExcludedAsNotVisible()
    {
        var foreign = TemplateVersionRecords.Sample(version: "1.1.0", owner: "reporting-system", shared: false);

        var candidates = TemplateVersionCandidates.Build([], [foreign], Consumer);

        Assert.Empty(candidates.Considered);
        var skip = Assert.Single(candidates.Skipped);
        Assert.Equal("1.1.0", skip.Version);
        Assert.Equal(CandidateSkipReasons.NotVisible, skip.Reason);
        Assert.Null(skip.Code);
        Assert.Null(skip.RefusalReason);
    }

    [Fact]
    public void TheOrderIsPrecedenceNotText()
    {
        var candidates = TemplateVersionCandidates.Build(["1.0.0", "1.10.0", "1.2.0", "1.1.0"], [], Consumer);

        Assert.Equal(["1.10.0", "1.2.0", "1.1.0", "1.0.0"], candidates.Considered.Select(candidate => candidate.Version));
    }

    [Fact]
    public void EqualPrecedenceWithNeitherTakenTakesTheHigherFullString()
    {
        var candidates = TemplateVersionCandidates.Build(["1.1.0+build.1", "1.1.0+build.2"], [], Consumer);

        Assert.Equal(["1.1.0+build.2", "1.1.0+build.1"], candidates.Considered.Select(candidate => candidate.Version));
    }

    [Fact]
    public void EqualPrecedenceWithOneTakenPutsTheTakenOneFirst()
    {
        var taken = TemplateVersionRecords.Sample(version: "1.1.0+build.1", owner: Consumer);

        var candidates = TemplateVersionCandidates.Build(["1.1.0+build.1", "1.1.0+build.2"], [taken], Consumer);

        Assert.Equal(["1.1.0+build.1", "1.1.0+build.2"], candidates.Considered.Select(candidate => candidate.Version));
    }

    [Fact]
    public void EqualPrecedenceWithBothTakenTakesTheHigherFullString()
    {
        var first = TemplateVersionRecords.Sample(version: "1.1.0+build.1", owner: Consumer);
        var second = TemplateVersionRecords.Sample(version: "1.1.0+build.2", owner: Consumer);

        var candidates = TemplateVersionCandidates.Build([], [first, second], Consumer);

        Assert.Equal(["1.1.0+build.2", "1.1.0+build.1"], candidates.Considered.Select(candidate => candidate.Version));
    }

    [Fact]
    public void TheSameStateAlwaysGivesTheSameOrder()
    {
        string[] names = ["1.1.0+build.2", "1.0.0", "1.1.0+build.1", "2.0.0-beta.1", "v1.3.0"];

        var first = TemplateVersionCandidates.Build(names, [], Consumer);
        var second = TemplateVersionCandidates.Build([.. names], [], Consumer);

        Assert.Equal(
            first.Considered.Select(candidate => candidate.Version),
            second.Considered.Select(candidate => candidate.Version));
        Assert.Equal(
            first.Skipped.Select(skip => (skip.Version, skip.Reason)),
            second.Skipped.Select(skip => (skip.Version, skip.Reason)));
    }
}
