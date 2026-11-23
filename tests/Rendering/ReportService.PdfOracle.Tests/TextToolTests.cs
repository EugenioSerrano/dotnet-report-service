namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The two text tools are never allowed to decide when a read ends. A stopped read
/// is reported as an instrument error naming the tool and its limit — never as a
/// verdict, and never as a pass — while a caller that cancels gets its cancellation
/// back. The tools themselves exist only in the instrument's image, so the cases hold
/// the decision a stopped read reaches and the check that runs before a tool is
/// started; the memory records that the time limit's expiry, which needs a tool that
/// does not answer, is read and not exercised here.
/// </summary>
public sealed class TextToolTests
{
    [Fact]
    public async Task AReadTheCallerAlreadyCancelledStartsNoTool()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // No tool is started, so the case passes on a host without poppler: a started
        // tool would have failed with the instrument's message instead.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => PopplerText.ReadAsync("document.pdf", TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public void AReadThatOutlivedItsLimitIsAnInstrumentErrorNamingTheToolAndTheLimit()
    {
        var ended = PopplerText.Ended(CancellationToken.None, TimeSpan.FromSeconds(2), "The text tool");

        var exception = Assert.IsType<OracleInstrumentException>(ended);
        Assert.Contains("The text tool", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2 s", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadTheCallerCancelledEndsAsThatCancellationAndNotAsAnError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var ended = PopplerText.Ended(cancellation.Token, TimeSpan.FromSeconds(2), "The text tool");

        Assert.IsType<OperationCanceledException>(ended);
    }
}
