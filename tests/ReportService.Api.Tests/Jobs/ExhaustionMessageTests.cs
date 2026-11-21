using ReportService.Api.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The sentence a consumer reads when a PDF output exhausted the attempts its policy allowed:
/// the fixed sentence of its code, joined by the token the failed output stores - never built
/// from an exception, never naming a path, a key, a host, an attempt or a value. A reason this
/// build does not recognise renders no message and is reported once, so a consumer is never
/// handed a sentence the service cannot stand behind.
/// </summary>
public sealed class ExhaustionMessageTests
{
    private const string ExecutionFailedSentence =
        "The service could not complete this report. Your data and template are not the cause; the request can be submitted again later.";

    private const string RenderTimeoutSentence =
        "This report took too long to print. Your data and template are not the cause; the request can be submitted again later.";

    [Theory]
    [InlineData("EXECUTION_FAILED", "exhausted cause=none", ExecutionFailedSentence)]
    [InlineData("EXECUTION_FAILED", "exhausted cause=watchdog-deadline", ExecutionFailedSentence)]
    [InlineData("EXECUTION_FAILED", "exhausted cause=guard-deadline", ExecutionFailedSentence)]
    [InlineData("RENDER_TIMEOUT", "exhausted cause=guard-deadline", RenderTimeoutSentence)]
    [InlineData("RENDER_TIMEOUT", "exhausted cause=watchdog-deadline", RenderTimeoutSentence)]
    [InlineData("RENDER_TIMEOUT", "exhausted cause=none", RenderTimeoutSentence)]
    public void AnExhaustedFailureIsTheFixedSentenceOfItsCode(string code, string reason, string expected)
    {
        var warnings = new LogCapture();

        var message = ErrorMessages.Format(code, reason, warnings.CreateLogger("api"));

        Assert.Equal(expected, message);
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheSameFailureRendersTheSameTextTwice()
    {
        var first = Format("RENDER_TIMEOUT", "exhausted cause=guard-deadline");
        var second = Format("RENDER_TIMEOUT", "exhausted cause=guard-deadline");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("EXECUTION_FAILED", "exhausted cause=render-timeout")]
    [InlineData("EXECUTION_FAILED", "exhausted cause=")]
    [InlineData("EXECUTION_FAILED", "the render passed its deadline")]
    [InlineData("RENDER_TIMEOUT", "C:\\scratch\\report-service-render\\block-0.pdf")]
    [InlineData("RENDER_TIMEOUT", "outputs/job-1/output-1/3f2a")]
    [InlineData("RENDER_TIMEOUT", "System.TimeoutException: the request timed out")]
    [InlineData("RENDER_TIMEOUT", "exhausted cause=none path=C:\\work")]
    [InlineData("EXECUTION_FAILED", "")]
    public void AReasonThisBuildCannotReadRendersNoMessageAndIsReportedOnce(string code, string reason)
    {
        var warnings = new LogCapture();

        Assert.Null(ErrorMessages.Format(code, reason, warnings.CreateLogger("api")));
        Assert.Single(warnings.Lines);
        Assert.StartsWith("Warning:", warnings.Lines[0], StringComparison.Ordinal);

        // The report names the code and never the text a consumer could not use anyway.
        Assert.Contains(code, warnings.Lines[0], StringComparison.Ordinal);
        if (reason.Length > 0)
        {
            Assert.DoesNotContain(reason, warnings.Lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnotherCodeOrNoReasonRendersNothingWithoutAWarning()
    {
        var warnings = new LogCapture();
        var logger = warnings.CreateLogger("api");

        Assert.Null(ErrorMessages.Format(null, null, logger));
        Assert.Null(ErrorMessages.Format("PAGINATION_FAILED", "exhausted cause=none", logger));
        Assert.Null(ErrorMessages.Format("RENDER_TIMEOUT", null, logger));
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheSentenceCarriesNoInternalDetail()
    {
        foreach (var code in new[] { "EXECUTION_FAILED", "RENDER_TIMEOUT" })
        {
            foreach (var reason in new[] { "exhausted cause=watchdog-deadline", "exhausted cause=guard-deadline", "exhausted cause=none" })
            {
                var sentence = Format(code, reason);

                Assert.NotNull(sentence);
                Assert.DoesNotContain("\\", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("/", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("outputs", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("C:", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("Exception", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("watchdog", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("guard", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("cause=", sentence, StringComparison.Ordinal);
            }
        }
    }

    private static string? Format(string code, string reason)
    {
        var warnings = new LogCapture();
        var message = ErrorMessages.Format(code, reason, warnings.CreateLogger("api"));
        Assert.Empty(warnings.Lines);
        return message;
    }
}
