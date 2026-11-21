using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource.Checks;
using ReportService.Testing;
using ReportService.Testing.TemplateChecks;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The take's at-bound check over the in-memory renderer: a passing verdict whose
/// request carries the snapshot, the refusals' exact sentences - the part, the
/// position's path, the verification's reason and never a value - the anchor, blank
/// page and engine cases, the no-record control taken with its contract-synthesised
/// record, the retryable render raised and never refused, the observation's fields,
/// and a version with no PDF output admitted with its scope recorded.
/// </summary>
public sealed class AtBoundSampleCheckTests
{
    private const int Bound = PdfRowBound.Bytes;

    [Fact]
    public async Task APassingRenderTakesTheVersionAndReceivesTheSnapshot()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysRendered();

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
        var request = Assert.Single(renderer.Requests);
        Assert.False(request.EmptySample);
        var snapshot = JsonNode.Parse(Encoding.UTF8.GetString(request.SampleJson.Span))!.AsObject();
        var items = snapshot["items"]!.AsArray();
        Assert.Single(items);
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(items[0]!["description"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("text-dropped", null, "html/row.html.sbn", "html/row.html.sbn: the record at the row bound is not printed completely at $.description (text-dropped). Print the field as it is, with every character, across as many pages as the row needs.")]
    [InlineData("text-altered", null, "html/row.html.sbn", "html/row.html.sbn: the record at the row bound is not printed completely at $.description (text-altered). Print the field as it is, with every character, across as many pages as the row needs.")]
    [InlineData("blank-page", null, "html/row.html.sbn", "html/row.html.sbn: the page that prints the record at the row bound carries no content at $.description (blank-page). Correct the page settings or the parts so that every page carries its content.")]
    [InlineData("anchor-mismatch", "$.description", "template.yaml", "template.yaml: the field declared as an anchor is not printed as it is at $.description (anchor-mismatch). Print the field as it is, or remove it from the anchor list.")]
    public async Task AVerificationFailureRefusesWithThePartThePathAndTheReason(
        string reason,
        string? anchor,
        string expectedFile,
        string expected)
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysFailed(new SampleFailure(
            SampleFailureKind.Verification,
            expectedFile,
            AnchorPath: anchor,
            Reason: reason));

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Equal(expected, result.Reason);
        Assert.True(Encoding.UTF8.GetByteCount(result.Reason!) <= TemplateRefusal.MaximumBytes);
        Assert.DoesNotContain("Consulting", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineFailureRefusesNamingThePartAndTheClass()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysFailed(new SampleFailure(
            SampleFailureKind.EngineError,
            "html/row.html.sbn"));

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(
            "html/row.html.sbn: the part could not render the record at the row bound at $.description (engine-error). Correct the template so that it renders the record at the row bound.",
            result.Reason);
    }

    [Fact]
    public async Task AVErificationReasonOutsideTheNamedClassesUsesTheGenericSentence()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysFailed(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            Reason: "row-without-text"));

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(
            "html/row.html.sbn: the record at the row bound does not pass the printed page's check at $.description (row-without-text). Correct the part the page prints from.",
            result.Reason);
    }

    [Fact]
    public async Task ARefusalOfTheDerivationsOwnMakingIsALimitation()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysFailed(new SampleFailure(
            SampleFailureKind.Unreadable,
            "sample.json",
            Reason: "format"));

        var raised = await Assert.ThrowsAsync<AtBoundDerivationException>(() =>
            Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal("render", raised.Code);
    }

    [Fact]
    public async Task ARetryableRenderIsRaisedAndNeverRefused()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysUnavailable(SampleUnavailableCause.RenderTimeout);

        var raised = await Assert.ThrowsAsync<SampleRenderUnavailableException>(() =>
            Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal(SampleUnavailableCause.RenderTimeout, raised.Cause);
    }

    [Fact]
    public async Task ASampleWithNoRecordIsTakenWithItsContractSynthesisedRecord()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package(records: []));
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysRendered();

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
        var request = Assert.Single(renderer.Requests);
        var snapshot = JsonNode.Parse(Encoding.UTF8.GetString(request.SampleJson.Span))!.AsObject();
        var item = Assert.Single(snapshot["items"]!.AsArray())!;
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(item["description"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AVersionWithNoPdfOutputIsAdmittedWithItsScopeRecorded()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package(withPdf: false));
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        using var capture = new LogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        var result = await new AtBoundSampleCheck(new TemplateCheckSession(TimeProvider.System), renderer, factory.CreateLogger<AtBoundSampleCheck>())
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
        Assert.Empty(renderer.Requests);
        Assert.Contains(capture.Lines, line => line.Contains("checked=at-bound-sample outcome=not-applicable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheObservationNamesThePositionTheDerivationAndTheRecordSize()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        using var capture = new LogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        var result = await new AtBoundSampleCheck(new TemplateCheckSession(TimeProvider.System), renderer, factory.CreateLogger<AtBoundSampleCheck>())
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
        Assert.Contains(
            capture.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=$.description", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADerivationLimitationIsRaisedAndNeverRefused()
    {
        const string Record = """{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9]{4}$" } } }""";
        using var package = Packages.WritePackage(AtBoundFixtures.Package(Record));
        var candidate = Packages.ReadCandidate(package.Directory);

        var raised = await Assert.ThrowsAsync<AtBoundDerivationException>(() =>
            Check(new InMemorySampleRenderer().AlwaysRendered()).CheckAsync(candidate, TestContext.Current.CancellationToken));

        Assert.Equal("undecided", raised.Code);
    }

    [Fact]
    public async Task TheRefusalCarriesNoPartOfTheAtBoundValue()
    {
        using var package = Packages.WritePackage(AtBoundFixtures.Package());
        var candidate = Packages.ReadCandidate(package.Directory);
        var renderer = new InMemorySampleRenderer().AlwaysFailed(new SampleFailure(
            SampleFailureKind.Verification,
            "html/row.html.sbn",
            Reason: "text-dropped"));

        var result = await Check(renderer).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(new string('a', 16), result.Reason!, StringComparison.Ordinal);
        Assert.Contains("$.description", result.Reason, StringComparison.Ordinal);
    }

    private static AtBoundSampleCheck Check(ISampleRenderer renderer) =>
        new(new TemplateCheckSession(TimeProvider.System), renderer);
}
