using Microsoft.Extensions.Logging;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Resolution;

namespace ReportService.TemplateSource.Tests.Resolution;

/// <summary>
/// The by-name resolution at service level, against the in-memory source, the
/// in-memory store and the take seam's double: the acceptance criteria of the
/// change, each with the state its Given describes, plus the boundary cases the
/// rule implies — one commit, a removed folder, two versions of equal
/// precedence, the deadline and the source that cannot be checked.
/// </summary>
public sealed class TemplateVersionResolverTests : IDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = "invoice-energy";
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(240);

    private readonly InMemoryTemplateSourceReader _source = new();
    private readonly InMemoryTemplateVersionStore _store = new();
    private readonly InMemoryTemplateTake _take = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly LogCapture _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly TemplateVersionConflict _conflict;

    public TemplateVersionResolverTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(_logs));
        _conflict = new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory));
    }

    /// <inheritdoc />
    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task ANewerValidVersionIsTakenAndUsed()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        var record = Record("1.1.0", commitSha: _source.CommitSha);
        _take.SeedTaken(Template, "1.1.0", record);

        var (outcome, trace) = await Resolve();

        var resolved = AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Equal(_source.CommitSha, resolved.Record.CommitSha);
        Assert.Equal(["1.1.0", "1.0.0"], trace.Considered);
        var call = Assert.Single(_take.Calls);
        Assert.Equal("1.1.0", call.Version);
        Assert.Equal(Consumer, call.RequestingConsumerId);
        Assert.Equal("job-0001", call.ReportJobId);
        Assert.Equal(0, _store.InsertCalls);
        Assert.True(LoggedInformation("template.by_name", "outcome resolved"));
    }

    [Fact]
    public async Task ANewerInvalidVersionIsLoggedSkippedAndTheTakenOlderOneUsed()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        _take.SeedRefused(Template, "1.1.0", TemplateCheckCodes.TemplateInvalid, "Add the missing schema file.", retryable: false);

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.0.0", fromTakenCopy: false);
        var skip = Assert.Single(trace.Skipped);
        Assert.Equal("1.1.0", skip.Version);
        Assert.Equal(CandidateSkipReasons.Refused, skip.Reason);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, skip.Code);
        Assert.Equal("Add the missing schema file.", skip.RefusalReason);
        Assert.True(LoggedWarning("template.by_name.skipped", "1.1.0", "Add the missing schema file."));
        Assert.DoesNotContain(_source.RequestLog, request => request.Version == "1.0.0");
        Assert.DoesNotContain(_take.Calls, call => call.Version == "1.0.0");
    }

    [Fact]
    public async Task NothingValidRefusesTheTemplateNamingTheNewestRefusal()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        _take.SeedRefused(Template, "1.0.0", TemplateCheckCodes.TemplateInvalid, "Fix the row template.", retryable: false);
        _take.SeedRefused(Template, "1.1.0", TemplateCheckCodes.TemplateInvalid, "Fix the manifest version.", retryable: false);

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateInvalid, retryable: false);
        Assert.Contains("1.1.0", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Fix the manifest version.", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("1.0.0", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _store.InsertCalls);
    }

    [Fact]
    public async Task AnEmptyTemplateFolderHasNoReleaseVersion()
    {
        _source.SeedTemplateFolder(Template);

        var (outcome, _) = await Resolve();

        // The class read answers an empty list both for a template that does not
        // exist and for one that holds no version: the delivered not-found answer.
        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(TemplateVersionResolver.NotFoundReason, refused.Reason);
        Assert.Empty(_take.Calls);
    }

    [Fact]
    public async Task AHigherPreReleaseIsNeverChosenByName()
    {
        SeedSource(Template, "1.0.0", "1.1.0", "2.0.0-beta.1");
        Taken("1.0.0");
        _take.SeedTaken(Template, "1.1.0", Record("1.1.0"));

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Contains(trace.Skipped, skip => skip.Version == "2.0.0-beta.1" && skip.Reason == CandidateSkipReasons.PreRelease);
        Assert.DoesNotContain(_take.Calls, call => call.Version == "2.0.0-beta.1");
    }

    [Fact]
    public async Task ATakenPreReleaseIsNeverAByNameCandidate()
    {
        SeedSource(Template, "1.1.0");
        _store.Seed(Record("2.0.0-beta.1", owner: Consumer, commitSha: _source.CommitSha));
        _take.SeedTaken(Template, "1.1.0", Record("1.1.0"));

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Contains(trace.Skipped, skip => skip.Version == "2.0.0-beta.1" && skip.Reason == CandidateSkipReasons.PreRelease);
    }

    [Fact]
    public async Task OnlyPreReleasesHaveNoReleaseVersionAndTheNewestIsNamed()
    {
        SeedSource(Template, "2.0.0-beta.1", "2.0.0-rc.1");

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateInvalid, retryable: false);
        Assert.Contains("has no release version", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("2.0.0-rc.1", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_take.Calls);
    }

    [Fact]
    public async Task ATakenPreReleaseWhoseFolderWasRemovedIsStillNotARelease()
    {
        SeedSource(Template, "1.0.0");
        _store.Seed(Record("2.0.0-rc.1", owner: Consumer, commitSha: _source.CommitSha));
        _source.Remove(Template, "1.0.0");

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateInvalid, retryable: false);
        Assert.Contains("has no release version", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", refused.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnreachableSourceUsesTheNewestTakenRelease()
    {
        SeedSource(Template, "1.0.0", "1.1.0", "1.2.0");
        Taken("1.0.0");
        Taken("1.1.0");
        _source.SetUnreachable(true);

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: true);
        Assert.True(trace.SourceUnreachable);
        Assert.True(LoggedWarning("template.by_name.unchecked", "could not be checked", "unreachable", "1.1.0"));
    }

    [Fact]
    public async Task AnUnreachableSourceWithNothingTakenIsRetryableSourceUnavailable()
    {
        SeedSource(Template, "1.0.0");
        _source.SetUnreachable(true);

        var (outcome, trace) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateSourceUnavailable, retryable: true);
        Assert.Equal(TemplateVersionResolver.SourceUnavailableReason, refused.Reason);
        Assert.True(trace.SourceUnreachable);
    }

    [Fact]
    public async Task ASourceThatFailsDuringTheWalkUsesTheTakenRelease()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        _take.SeedRefused(Template, "1.1.0", TemplateResolutionCodes.TemplateSourceUnavailable, TemplateVersionResolver.SourceUnavailableReason, retryable: true);

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.0.0", fromTakenCopy: true);
        Assert.True(trace.SourceUnreachable);
        Assert.True(LoggedWarning("template.by_name.unchecked", "could not be checked", "1.0.0"));
    }

    [Fact]
    public async Task EqualPrecedenceWithNeitherTakenTakesTheHigherFullString()
    {
        const string Equal = "invoice-energy-equal";
        SeedSource(Equal, "1.1.0+build.1", "1.1.0+build.2");
        _take.SeedTaken(Equal, "1.1.0+build.2", Record("1.1.0+build.2", templateId: Equal));

        var (first, _) = await Resolve(Equal);
        var (second, _) = await Resolve(Equal);

        AssertResolved(first, "1.1.0+build.2", fromTakenCopy: false);
        AssertResolved(second, "1.1.0+build.2", fromTakenCopy: false);
        Assert.DoesNotContain(_take.Calls, call => call.Version == "1.1.0+build.1");
    }

    [Fact]
    public async Task EqualPrecedenceWithOneTakenUsesTheTakenOneWithoutASourceReadForEither()
    {
        const string Equal = "invoice-energy-equal";
        SeedSource(Equal, "1.1.0+build.1", "1.1.0+build.2");
        Taken("1.1.0+build.1", templateId: Equal);

        var (outcome, _) = await Resolve(Equal);

        AssertResolved(outcome, "1.1.0+build.1", fromTakenCopy: false);
        Assert.Empty(_take.Calls);
        Assert.DoesNotContain(_source.RequestLog, request => request.Version is "1.1.0+build.1" or "1.1.0+build.2");
    }

    [Fact]
    public async Task EqualPrecedenceWithBothTakenTakesTheHigherFullString()
    {
        const string Equal = "invoice-energy-equal";
        SeedSource(Equal, "1.1.0+build.1", "1.1.0+build.2");
        Taken("1.1.0+build.1", templateId: Equal);
        Taken("1.1.0+build.2", templateId: Equal);

        var (outcome, _) = await Resolve(Equal);

        AssertResolved(outcome, "1.1.0+build.2", fromTakenCopy: false);
        Assert.Empty(_take.Calls);
    }

    [Fact]
    public async Task AVersionPublishedAfterTheListingIsNotInTheJobSet()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        _take.SeedTaken(Template, "1.1.0", Record("1.1.0"));
        var published = false;
        _take.BeforeTake = _ =>
        {
            if (!published)
            {
                published = true;
                _source.Publish(Template, "1.2.0");
            }
        };

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Equal(["1.1.0", "1.0.0"], trace.Considered);
        Assert.Equal("1.1.0", Assert.Single(_take.Calls).Version);
    }

    [Fact]
    public async Task ASecondResolutionSeesAVersionPublishedAfterTheFirstListing()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        _take.SeedTaken(Template, "1.1.0", Record("1.1.0"));
        await Resolve();

        _source.Publish(Template, "1.2.0");
        _take.SeedTaken(Template, "1.2.0", Record("1.2.0", commitSha: _source.CommitSha));
        _take.ClearCalls();
        var (outcome, _) = await Resolve();

        AssertResolved(outcome, "1.2.0", fromTakenCopy: false);
        Assert.Equal("1.2.0", Assert.Single(_take.Calls).Version);
    }

    [Fact]
    public async Task ATakenVersionRemovedFromTheBranchStaysAvailable()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        Taken("1.1.0");
        _source.Remove(Template, "1.1.0");

        var (outcome, _) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Empty(_take.Calls);
        Assert.DoesNotContain(_source.RequestLog, request => request.Version == "1.1.0");
    }

    [Fact]
    public async Task AForeignNonSharedVersionIsExcludedAndTheSharedOneUsed()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        _store.Seed(Record("1.1.0", owner: "reporting-system", shared: false));
        _store.Seed(Record("1.0.0", owner: "reporting-system", shared: true));

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.0.0", fromTakenCopy: false);
        var skip = Assert.Single(trace.Skipped);
        Assert.Equal("1.1.0", skip.Version);
        Assert.Equal(CandidateSkipReasons.NotVisible, skip.Reason);
        Assert.Null(skip.Code);
        Assert.Null(skip.RefusalReason);
    }

    [Fact]
    public async Task ATemplateWhoseVersionsAreAllForeignIsNotFoundNamingNothing()
    {
        SeedSource(Template, "1.1.0");
        _store.Seed(Record("1.1.0", owner: "reporting-system", shared: false));

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(TemplateVersionResolver.NotFoundReason, refused.Reason);
        Assert.DoesNotContain("1.1.0", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForeignReleaseBesideANonVersionFolderIsNotFound()
    {
        SeedSource(Template, "1.1.0", "v1.3.0");
        _store.Seed(Record("1.1.0", owner: "reporting-system", shared: false));

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(TemplateVersionResolver.NotFoundReason, refused.Reason);
    }

    [Fact]
    public async Task AForeignTakenPreReleaseBesideAForeignReleaseIsNotFound()
    {
        SeedSource(Template, "1.1.0");
        _store.Seed(Record("1.1.0", owner: "reporting-system", shared: false));
        _store.Seed(Record("2.0.0-rc.1", owner: "reporting-system", shared: false));

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(TemplateVersionResolver.NotFoundReason, refused.Reason);
    }

    [Fact]
    public async Task AnUnsupportedVersionIsSkippedLikeAnInvalidOne()
    {
        SeedSource(Template, "1.0.0", "1.1.0", "1.4.0");
        Taken("1.0.0");
        _take.SeedRefused(Template, "1.4.0", TemplateCheckCodes.UnsupportedCapability, "The version declares the Razor engine, which this deployment does not run.", retryable: false);
        _take.SeedTaken(Template, "1.1.0", Record("1.1.0"));

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Contains(trace.Skipped, skip => skip.Version == "1.4.0" && skip.Code == TemplateCheckCodes.UnsupportedCapability);
        Assert.True(LoggedWarning("template.by_name.skipped", "1.4.0", TemplateCheckCodes.UnsupportedCapability));
    }

    [Fact]
    public async Task ATemplateWhoseVersionsAreAllUnsupportedIsInvalidWithTheNewestRefusal()
    {
        SeedSource(Template, "1.4.0", "1.5.0");
        _take.SeedRefused(Template, "1.4.0", TemplateCheckCodes.UnsupportedCapability, "The version declares the Razor engine.", retryable: false);
        _take.SeedRefused(Template, "1.5.0", TemplateCheckCodes.UnsupportedCapability, "The version declares a feature this deployment does not run.", retryable: false);

        var (outcome, _) = await Resolve();

        var refused = AssertRefused(outcome, TemplateResolutionCodes.TemplateInvalid, retryable: false);
        Assert.Contains("1.5.0", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("The version declares a feature this deployment does not run.", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateCheckCodes.UnsupportedCapability, refused.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeadlineUsesTheTakenReleaseAndNeverReportsInvalidity()
    {
        SeedSource(Template, "1.0.0", "1.1.0", "1.2.0");
        Taken("1.0.0");
        _take.SeedRefused(Template, "1.2.0", TemplateCheckCodes.TemplateInvalid, "Fix the row template.", retryable: false);
        var deadline = TimeSpan.FromSeconds(10);
        _take.BeforeTake = _ => _time.Advance(deadline);

        var (outcome, trace) = await Resolve(deadline: deadline);

        AssertResolved(outcome, "1.0.0", fromTakenCopy: true);
        Assert.True(trace.DeadlineReached);
        Assert.Equal(1, trace.Examined);
        Assert.True(LoggedWarning("template.by_name.unchecked", "deadline"));
    }

    [Fact]
    public async Task TheDeadlineWithNothingTakenIsRetryableSourceUnavailable()
    {
        SeedSource(Template, "1.1.0", "1.2.0");
        _take.SeedRefused(Template, "1.2.0", TemplateCheckCodes.TemplateInvalid, "Fix the row template.", retryable: false);
        var deadline = TimeSpan.FromSeconds(10);
        _take.BeforeTake = _ => _time.Advance(deadline);

        var (outcome, trace) = await Resolve(deadline: deadline);

        AssertRefused(outcome, TemplateResolutionCodes.TemplateSourceUnavailable, retryable: true);
        Assert.True(trace.DeadlineReached);
        Assert.DoesNotContain(TemplateResolutionCodes.TemplateInvalid, outcome is RefusedOutcome refused ? refused.Code : string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadlineInsideATakeUsesTheTakenRelease()
    {
        SeedSource(Template, "1.0.0", "1.1.0");
        Taken("1.0.0");
        _take.WaitForCancellation = true;
        var deadline = TimeSpan.FromSeconds(10);
        var resolving = Resolver(deadline).ResolveAsync(Template, Consumer, "job-0001", TestContext.Current.CancellationToken);

        _time.Advance(deadline);
        var (outcome, trace) = await resolving;

        AssertResolved(outcome, "1.0.0", fromTakenCopy: true);
        Assert.True(trace.DeadlineReached);
    }

    [Fact]
    public async Task AnInvalidReferenceCostsNoStoreAndNoSourceCall()
    {
        var (outcome, trace) = await Resolve(templateId: "Invoice-Energy");

        AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(0, _source.RequestCount);
        Assert.Equal(0, _store.ListCalls);
        Assert.Empty(_take.Calls);
        Assert.Empty(trace.Considered);
        Assert.Empty(ConflictLines());
    }

    [Fact]
    public async Task ATakenVersionWhoseListedPublicationChangedIsObservedOnceAndTheCopyStillAnswers()
    {
        SeedSource(Template, "1.1.0");
        var listed = InMemoryTemplateSourceReader.DescriptorHash(Template, "1.1.0");
        var taken = TemplateVersionRecords.StorageSample(Template, "1.1.0");
        Assert.NotEqual(taken.SourceDescriptorSha256, listed, StringComparer.Ordinal);
        _store.Seed(taken);

        var (outcome, trace) = await Resolve();

        // The taken copy answers, no job is refused and nothing is taken again.
        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Empty(_take.Calls);
        Assert.Empty(trace.Skipped);

        // One warning, naming both publications and the reason.
        var line = Assert.Single(ConflictLines());
        Assert.Contains(taken.SourceDescriptorSha256!, line, StringComparison.Ordinal);
        Assert.Contains(listed, line, StringComparison.Ordinal);
        Assert.Contains($"Observation {TemplateVersionConflict.Observations.PublicationChanged}", line, StringComparison.Ordinal);
        Assert.Contains("job-0001", line, StringComparison.Ordinal);

        // The comparison is two identities: the listing is the only source call,
        // and no file of the package is opened.
        Assert.DoesNotContain(_source.RequestLog, request => request.Step == "file");
        Assert.All(_source.RequestLog, request => Assert.NotEqual("1.1.0", request.Version));
    }

    [Fact]
    public async Task AListedPublicationEqualToTheOneTheVersionWasTakenWithCarriesNoWarning()
    {
        SeedSource(Template, "1.1.0");
        var listed = InMemoryTemplateSourceReader.DescriptorHash(Template, "1.1.0");
        _store.Seed(TemplateVersionRecords.StorageSample(Template, "1.1.0") with
        {
            SourceDescriptorSha256 = listed,
            SourceDescriptorKey = $"template-source/{Template}/published/1.1.0/{listed}.json",
        });

        var (outcome, _) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Empty(ConflictLines());
    }

    [Fact]
    public async Task AVersionTakenBeforeTheObjectStoreIsNeverCompared()
    {
        SeedSource(Template, "1.1.0");
        Taken("1.1.0");

        var (outcome, _) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Empty(ConflictLines());
    }

    [Fact]
    public async Task ADuplicatePublicationIsTheWalksSkipAndCarriesNoObservation()
    {
        SeedSource(Template, "1.1.0");
        _source.SeedDuplicatePublication(Template, "1.1.0");
        _store.Seed(TemplateVersionRecords.StorageSample(Template, "1.1.0"));

        var (outcome, trace) = await Resolve();

        // Two records under one version text are not "the record the version was
        // taken with differs": the observation compares only a single one.
        AssertResolved(outcome, "1.1.0", fromTakenCopy: false);
        Assert.Empty(ConflictLines());
        Assert.Empty(trace.Skipped);
    }

    [Fact]
    public async Task ATwicePublishedUntakenVersionIsSkippedAndTheTakenOlderOneAnswers()
    {
        // The version is published twice and no copy of it is taken: the listing
        // reports both records, the take refuses the version as published more than
        // once, and the walk skips it like any other invalid one.
        SeedSource(Template, "1.0.0", "1.1.0");
        _source.SeedDuplicatePublication(Template, "1.1.0");
        _store.Seed(TemplateVersionRecords.StorageSample(Template, "1.0.0"));
        _take.SeedRefused(
            Template,
            "1.1.0",
            TemplateCheckCodes.TemplateInvalid,
            "The version was published more than once; publish it under a new version.",
            retryable: false);

        var (outcome, trace) = await Resolve();

        // The older taken release answers, from its record and not from the source.
        var resolved = AssertResolved(outcome, "1.0.0", fromTakenCopy: false);
        Assert.Equal(TemplateVersionRecords.DescriptorSha256, resolved.Record.SourceDescriptorSha256);

        // The duplicate is the walk's skip: its code and the text to fix are kept,
        // and nothing is written for it.
        var skip = Assert.Single(trace.Skipped);
        Assert.Equal("1.1.0", skip.Version);
        Assert.Equal(CandidateSkipReasons.Refused, skip.Reason);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, skip.Code);
        Assert.Equal(
            "The version was published more than once; publish it under a new version.",
            skip.RefusalReason);
        Assert.True(LoggedWarning(
            "template.by_name.skipped",
            "version 1.1.0",
            "reason refused",
            "published more than once"));
        Assert.Equal(0, _store.InsertCalls);

        // The take ran for the duplicate only: the taken release cost no take and no
        // source read of its own.
        Assert.Equal("1.1.0", Assert.Single(_take.Calls).Version);
        Assert.DoesNotContain(_source.RequestLog, request => request.Version == "1.0.0");
    }

    [Fact]
    public async Task AnUnreachableSourceNeverReachesTheObservation()
    {
        SeedSource(Template, "1.1.0");
        _store.Seed(TemplateVersionRecords.StorageSample(Template, "1.1.0"));
        _source.SetUnreachable(true);

        var (outcome, trace) = await Resolve();

        AssertResolved(outcome, "1.1.0", fromTakenCopy: true);
        Assert.True(trace.SourceUnreachable);
        Assert.Empty(ConflictLines());
    }

    [Fact]
    public async Task AnEmptyConsumerCostsNoStoreAndNoSourceCall()
    {
        var (outcome, _) = await Resolve(consumer: string.Empty);

        AssertRefused(outcome, TemplateResolutionCodes.TemplateNotFound, retryable: false);
        Assert.Equal(0, _source.RequestCount);
        Assert.Equal(0, _store.ListCalls);
    }

    private TemplateVersionResolver Resolver(TimeSpan? deadline = null) => new(
        _source,
        _store,
        _take,
        _conflict,
        new TemplateByNameMetrics(),
        _time,
        _loggerFactory.CreateLogger("ReportService.TemplateSource.Resolution"),
        deadline ?? DefaultDeadline);

    /// <summary>Every line of the conflict warning the case captured, whichever reason it carries.</summary>
    private IReadOnlyList<string> ConflictLines() =>
        [.. _logs.Lines.Where(line => line.Contains(TemplateVersionConflict.Code, StringComparison.Ordinal))];

    private Task<(TemplateResolutionOutcome Outcome, TemplateResolutionTrace Trace)> Resolve(
        string templateId = Template,
        string consumer = Consumer,
        TimeSpan? deadline = null) =>
        Resolver(deadline).ResolveAsync(templateId, consumer, "job-0001", TestContext.Current.CancellationToken);

    private void SeedSource(string templateId, params string[] versions)
    {
        foreach (var version in versions)
        {
            _source.SeedVersionFolder(templateId, version);
        }
    }

    private void Taken(string version, string templateId = Template, string owner = Consumer, bool shared = false) =>
        _store.Seed(Record(version, templateId, owner, shared));

    private TemplateVersionRecord Record(
        string version,
        string templateId = Template,
        string owner = Consumer,
        bool shared = false,
        string? commitSha = null) =>
        TemplateVersionRecords.Sample(
            templateId: templateId,
            version: version,
            owner: owner,
            shared: shared,
            commitSha: commitSha ?? _source.CommitSha);

    private bool LoggedInformation(params string[] fragments) => Logged(LogLevel.Information, fragments);

    private bool LoggedWarning(params string[] fragments) => Logged(LogLevel.Warning, fragments);

    private bool Logged(LogLevel level, params string[] fragments) =>
        _logs.Lines.Any(line => line.StartsWith($"{level}: ", StringComparison.Ordinal)
            && fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));

    private static ResolvedOutcome AssertResolved(TemplateResolutionOutcome outcome, string version, bool fromTakenCopy)
    {
        var resolved = Assert.IsType<ResolvedOutcome>(outcome);
        Assert.Equal(version, resolved.Record.Version);
        Assert.Equal(fromTakenCopy, resolved.FromTakenCopy);
        return resolved;
    }

    private static RefusedOutcome AssertRefused(TemplateResolutionOutcome outcome, string code, bool retryable)
    {
        var refused = Assert.IsType<RefusedOutcome>(outcome);
        Assert.Equal(code, refused.Code);
        Assert.Equal(retryable, refused.Retryable);
        return refused;
    }
}
