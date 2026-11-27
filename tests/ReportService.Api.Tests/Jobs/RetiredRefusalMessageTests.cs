using System.Globalization;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The retirement of the single-block refusal's consumer sentence: a stored
/// <c>QUOTA_EXCEEDED</c> reason of the retired block shape renders no message, on the job
/// and on its output, while the surviving sentences - the row bound, an exhausted
/// execution and a render timeout - still render theirs. The code stays in the vocabulary
/// for its other causes, and the formatter's no-message answer for a failure it cannot word
/// is unchanged.
/// </summary>
public sealed class RetiredRefusalMessageTests
{
    private const string QuotaExceeded = "QUOTA_EXCEEDED";

    private const string RowBoundCode = "ROW_BOUND_EXCEEDED";

    private const string RowBoundSentence =
        "Record 1500 has a text field at $.description of 262145 bytes of UTF-8; the PDF output accepts at most 262144 bytes per field. Shorten that field.";

    private const string ExecutionFailedSentence =
        "The service could not complete this report. Your data and template are not the cause; the request can be submitted again later.";

    private const string RenderTimeoutSentence =
        "This report took too long to print. Your data and template are not the cause; the request can be submitted again later.";

    // The stored sentence the retired data step left: the code, the crossed dimension with
    // the applied value, the block token and the position the pass stopped at.
    private const string RetiredRecordsReason =
        "the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1000 limit=block ordinal=1001 offset=48213";

    private const string RetiredBytesReason =
        "the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=bytes applied=1048576 limit=block ordinal=573 offset=1049000";

    [Theory]
    [InlineData(RetiredRecordsReason)]
    [InlineData(RetiredBytesReason)]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1000 configured=5000 limit=block ordinal=1001 offset=48213")]
    public void ARetiredBlockRefusalRendersNoMessageAndChangesNothingElse(string reason)
    {
        var warnings = new LogCapture();

        Assert.Null(ErrorMessages.Format(QuotaExceeded, reason, warnings.CreateLogger("api")));
        Assert.Empty(warnings.Lines);

        // The status read keeps the code, the status and the non-retryability it had; only
        // the sentence the retired cause no longer has is absent.
        var answer = ReportJobStatusResponse.From(
            RefusedView(QuotaExceeded, reason),
            warnings.CreateLogger("api"));

        Assert.Equal("failed", answer.Status);
        Assert.Equal(QuotaExceeded, answer.ErrorCode);
        Assert.Null(answer.ErrorMessage);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal(QuotaExceeded, output.ErrorCode);
        Assert.Null(output.ErrorMessage);
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheSurvivingSentencesStillRender()
    {
        var warnings = new LogCapture();
        var logger = warnings.CreateLogger("api");

        Assert.Equal(
            RowBoundSentence,
            ErrorMessages.Format(RowBoundCode, StoredRowBoundSentence(), logger));
        Assert.Equal(
            ExecutionFailedSentence,
            ErrorMessages.Format("EXECUTION_FAILED", "exhausted cause=none", logger));
        Assert.Equal(
            RenderTimeoutSentence,
            ErrorMessages.Format("RENDER_TIMEOUT", "exhausted cause=guard-deadline", logger));
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheNoMessageAnswerIsUnchanged()
    {
        var warnings = new LogCapture();
        var logger = warnings.CreateLogger("api");

        Assert.Null(ErrorMessages.Format(null, null, logger));
        Assert.Null(ErrorMessages.Format("PAGINATION_FAILED", "anchors-missing", logger));
        Assert.Null(ErrorMessages.Format(RowBoundCode, null, logger));
        Assert.Null(ErrorMessages.Format(QuotaExceeded, null, logger));
        Assert.Empty(warnings.Lines);
    }

    private static string StoredRowBoundSentence() =>
        "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description";

    // A job whose only output ended failed with the same code and the same stored reason.
    private static ReportJobStatusView RefusedView(string code, string reason)
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        return new ReportJobStatusView(
            "job-0123456789abcdef0123456789abcdef",
            ReportJobStatus.Failed,
            null,
            1_001,
            1_001,
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
