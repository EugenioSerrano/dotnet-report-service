using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The sentence a producer reads when its PDF output was refused by the row bound: the record,
/// the field's path, its size and the bound, in plain digits and never a value of the report.
/// The stored reason is the data step's own diagnostic, parsed in its delivered key order; a
/// reason this build cannot read renders no message and is reported once, and every other code
/// keeps the rendering it has. The projection renders the same sentence on the job and on its
/// output, because the job's stored reason is the failed output's.
/// </summary>
public sealed class RowBoundMessageTests
{
    private const string Code = "ROW_BOUND_EXCEEDED";

    private const string Sentence =
        "Record 1500 has a text field at $.description of 262145 bytes of UTF-8; the PDF output accepts at most 262144 bytes per field. Shorten that field.";

    [Fact]
    public void TheRefusalNamesTheRecordTheFieldItsSizeAndTheBound()
    {
        Assert.Equal(
            Sentence,
            Format(StoredSentence(ordinal: 1_500, size: 262_145, bound: 262_144, path: "$.description")));
    }

    [Fact]
    public void ADeclaredNameWithASpaceIsReadWholeAsTheRestOfTheLine()
    {
        Assert.Equal(
            "Record 7 has a text field at $['unit price'] of 300000 bytes of UTF-8; the PDF output accepts at most 262144 bytes per field. Shorten that field.",
            Format(StoredSentence(ordinal: 7, size: 300_000, bound: 262_144, path: "$['unit price']")));
    }

    [Fact]
    public void ANameTheSchemaDoesNotDeclareIsTheStarPath()
    {
        Assert.Equal(
            "Record 2 has a text field at $.* of 262145 bytes of UTF-8; the PDF output accepts at most 262144 bytes per field. Shorten that field.",
            Format(StoredSentence(ordinal: 2, size: 262_145, bound: 262_144, path: "$.*")));
    }

    [Fact]
    public void TheTwoRenderingsOfOneReasonAreIdentical()
    {
        var reason = StoredSentence(ordinal: 1_500, size: 262_145, bound: 262_144, path: "$.description");

        Assert.Equal(Format(reason), Format(reason));
    }

    [Theory]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=-1 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262144 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=131072 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=0 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=-1 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=block ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large applied=3 size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 keyword=type path=$.description")]
    [InlineData("the document cannot be used: code=ROW_BOUND_EXCEEDED reason=another-word size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("the document cannot be used: code=DATA_INVALID reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description")]
    [InlineData("")]
    public void AReasonThisBuildCannotReadRendersNoMessageAndIsReportedOnce(string reason)
    {
        var warnings = new LogCapture();

        Assert.Null(ErrorMessages.Format(Code, reason, warnings.CreateLogger("api")));
        Assert.Single(warnings.Lines);
        Assert.StartsWith("Warning:", warnings.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherCodeAndAJobWithNoReasonRenderNothingWithoutAWarning()
    {
        var warnings = new LogCapture();
        var logger = warnings.CreateLogger("api");

        Assert.Null(ErrorMessages.Format(null, null, logger));
        Assert.Null(ErrorMessages.Format("PAGINATION_FAILED", "anchors-missing", logger));
        Assert.Null(ErrorMessages.Format(Code, null, logger));
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheRefusedJobsStatusRendersTheSameSentenceOnTheJobAndOnItsOutput()
    {
        var warnings = new LogCapture();
        var view = RefusedView(Code, StoredSentence(ordinal: 1_500, size: 262_145, bound: 262_144, path: "$.description"));

        var answer = ReportJobStatusResponse.From(view, warnings.CreateLogger("api"));

        Assert.Equal(Sentence, answer.ErrorMessage);
        Assert.Equal(Sentence, Assert.Single(answer.Outputs).ErrorMessage);
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void AJobFailedAtTheContractKeepsNoMessageOnTheJobOrOnItsOutput()
    {
        var warnings = new LogCapture();
        var view = RefusedView("DATA_INVALID", "schema-violation");

        var answer = ReportJobStatusResponse.From(view, warnings.CreateLogger("api"));

        Assert.Null(answer.ErrorMessage);
        Assert.Null(Assert.Single(answer.Outputs).ErrorMessage);
        Assert.Empty(warnings.Lines);
    }

    private static string? Format(string reason)
    {
        var warnings = new LogCapture();
        var message = ErrorMessages.Format(Code, reason, warnings.CreateLogger("api"));

        Assert.Empty(warnings.Lines);
        return message;
    }

    // The whole stored sentence, as the data step's refusal leaves it: the code, the reason
    // word with the field's size and the bound, the limit's token, the record's position and
    // the field's path.
    private static string StoredSentence(int ordinal, long size, long bound, string path) =>
        "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size="
        + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " bound=" + bound.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " limit=row-bound ordinal=" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " offset=48213 path=" + path;

    // A job whose only output ended failed with the same code and the same stored reason.
    private static ReportJobStatusView RefusedView(string code, string reason)
    {
        var now = new DateTimeOffset(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        return new ReportJobStatusView(
            "job-0123456789abcdef0123456789abcdef",
            ReportJobStatus.Failed,
            null,
            1_500,
            1_500,
            0,
            1,
            0,
            code,
            reason,
            now,
            now,
            now,
            [
                new ReportOutputStatusView(OutputFormat.Pdf, OutputStatus.Failed, code, reason, null),
            ]);
    }
}
