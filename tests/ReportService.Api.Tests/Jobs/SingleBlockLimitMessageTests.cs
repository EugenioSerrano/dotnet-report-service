using ReportService.Api.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The sentence a producer reads when its report was refused because its data does not fit
/// in one block: the record the pass stopped at, the limit this version applied, the
/// dimension that was crossed and what to do next, with plain digits and never a value of
/// the report or a number the operator configured. A stored reason this build does not
/// recognise renders no message and is reported once, so a consumer is never handed a
/// sentence the service cannot stand behind.
/// </summary>
public sealed class SingleBlockLimitMessageTests
{
    private const string QuotaExceeded = "QUOTA_EXCEEDED";

    private const string RecordsSentence =
        "The PDF report was not printed: record 1001 exceeds this version's limit of 1000 records per report (one block). Send a smaller report.";

    private const string HardMaximumSentence =
        "The PDF report was not printed: record 2001 exceeds this version's limit of 2000 records per report (one block). Send a smaller report.";

    private const string BytesSentence =
        "The PDF report was not printed: record 573 exceeds this version's limit of 1048576 bytes of record data per report (one block). Send a smaller report.";

    [Fact]
    public void TheRecordsRefusalNamesTheAppliedLimitAndTheRecordThatCrossedIt()
    {
        Assert.Equal(
            RecordsSentence,
            Format(Sentence("dimension=records applied=1000", ordinal: 1001, offset: 48213)));
    }

    [Fact]
    public void AConfiguredLimitAboveTheMaximumIsReportedAsTheMaximumItActuallyApplied()
    {
        Assert.Equal(
            HardMaximumSentence,
            Format(Sentence("dimension=records applied=2000", ordinal: 2001, offset: 96214)));
    }

    [Fact]
    public void TheByteRefusalNamesTheByteLimitAndTheRecordThatCrossedIt()
    {
        Assert.Equal(
            BytesSentence,
            Format(Sentence("dimension=bytes applied=1048576", ordinal: 573, offset: 1049000)));
    }

    [Fact]
    public void AConfiguredValueTheReasonCarriesNeverReachesTheConsumer()
    {
        // The operator's own diagnostic may name the configured value beside the applied one;
        // the sentence a consumer reads names only what was applied to its data.
        var message = Format(Sentence("dimension=records applied=1000 configured=5000", ordinal: 1001, offset: 48213));

        Assert.Equal(RecordsSentence, message);
        Assert.DoesNotContain("5000", message!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoRenderingsOfOneReasonAreIdentical()
    {
        var reason = Sentence("dimension=bytes applied=1048576", ordinal: 573, offset: 1049000);

        Assert.Equal(Format(reason), Format(reason));
    }

    [Theory]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED limit=block ordinal=1001 offset=5 reason=dimension=records applied=1000")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=many limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=-1 limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1001 limit=block offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=pages applied=3 limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1048576 limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=bytes applied=1048577 limit=block ordinal=573 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1000 limit=block ordinal=1001")]
    [InlineData("the document cannot be used: code=DATA_INVALID reason=dimension=records applied=1000 limit=block ordinal=1001 offset=5")]
    [InlineData("the document cannot be used: code=QUOTA_EXCEEDED reason=dimension=records applied=1000 limit=block ordinal=1001 offset=5 trailing")]
    [InlineData("")]
    public void AReasonThisBuildCannotReadRendersNoMessageAndIsReportedOnce(string reason)
    {
        var warnings = new LogCapture();

        Assert.Null(ErrorMessages.Format(QuotaExceeded, reason, warnings.CreateLogger("api")));
        Assert.Single(warnings.Lines);
        Assert.StartsWith("Warning:", warnings.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AJobThatCarriesNoReasonAndACodeThisBuildDoesNotSpeakForRenderNothing()
    {
        var warnings = new LogCapture();
        var logger = warnings.CreateLogger("api");

        Assert.Null(ErrorMessages.Format(QuotaExceeded, null, logger));
        Assert.Null(ErrorMessages.Format(null, null, logger));
        Assert.Null(ErrorMessages.Format("PAGINATION_FAILED", null, logger));
        Assert.Null(ErrorMessages.Format("PAGINATION_FAILED", "anchors-missing", logger));
        Assert.Empty(warnings.Lines);
    }

    private static string? Format(string reason)
    {
        var warnings = new LogCapture();
        var message = ErrorMessages.Format(QuotaExceeded, reason, warnings.CreateLogger("api"));

        Assert.Empty(warnings.Lines);
        return message;
    }

    // The whole stored sentence, as the data step's refusal leaves it: the code, the reason
    // the pass names (dimension and applied value), the limit's token and the position.
    private static string Sentence(string reasonPayload, int ordinal, long offset) =>
        "the document cannot be used: code=QUOTA_EXCEEDED reason=" + reasonPayload
        + " limit=block ordinal=" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
