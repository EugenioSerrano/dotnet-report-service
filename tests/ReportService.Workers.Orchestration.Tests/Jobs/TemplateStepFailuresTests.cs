using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// What a job records when its template step fails: a refusal keeps the code the step
/// raised, the constant reason that code stands for and the text the step brought -
/// cut at the length a job may hold - while a failure with no code of its own ends the
/// job with the step's own code. The decision is one function, so it is asserted without
/// an engine, and the workflow applies it unchanged.
/// </summary>
public sealed class TemplateStepFailuresTests
{
    [Theory]
    [InlineData("TEMPLATE_NOT_FOUND", "template-not-found")]
    [InlineData("TEMPLATE_INVALID", "template-invalid")]
    [InlineData("TEMPLATE_SOURCE_UNAVAILABLE", "template-source-unavailable")]
    [InlineData("UNSUPPORTED_CAPABILITY", "unsupported-capability")]
    public void ARefusalKeepsItsCodeAndItsConstantReason(string code, string reason)
    {
        var mapped = Map(code, "what the consumer has to fix");

        Assert.Equal(code, mapped.Code);
        Assert.Equal(reason, mapped.Reason);
        Assert.Equal("what the consumer has to fix", mapped.Message);
    }

    [Fact]
    public void TheAbsentTemplateRecordKeepsItsOwnReason()
    {
        var mapped = Map("EXECUTION_FAILED", TemplateFailureCodes.RecordMissingMessage);

        Assert.Equal("EXECUTION_FAILED", mapped.Code);
        Assert.Equal("template-record-missing", mapped.Reason);
        Assert.Equal(TemplateFailureCodes.RecordMissingMessage, mapped.Message);
    }

    [Fact]
    public void AnExecutionFailureOfTheStepItselfKeepsTheStepsReason()
    {
        var mapped = Map("EXECUTION_FAILED", "the version is not the one the job named");

        Assert.Equal("template-step-failed", mapped.Reason);
    }

    [Fact]
    public void AMessageOfTheLongestLengthIsKeptWhole()
    {
        var message = new string('m', TemplateFailureCodes.MessageLimit);

        var mapped = Map("TEMPLATE_INVALID", message);

        Assert.Equal(message, mapped.Message);
        Assert.False(mapped.WasCut(message));
    }

    [Fact]
    public void AMessageOneCharacterTooLongIsCutAndTheCutIsRecorded()
    {
        var message = new string('m', TemplateFailureCodes.MessageLimit + 1);

        var mapped = Map("TEMPLATE_INVALID", message);

        Assert.Equal(TemplateFailureCodes.MessageLimit, mapped.Message.Length);
        Assert.EndsWith("...", mapped.Message, StringComparison.Ordinal);
        Assert.True(mapped.WasCut(message));
    }

    [Fact]
    public void AFailureWithoutACodeOfItsOwnIsTheStepsFailure()
    {
        Assert.False(TemplateStepFailures.TryMap(new InvalidOperationException("no attempt left"), out var mapped));

        Assert.Equal("EXECUTION_FAILED", mapped.Code);
        Assert.Equal("template-step-failed", mapped.Reason);
        Assert.Equal(TemplateFailureCodes.StepFailedMessage, mapped.Message);
    }

    [Fact]
    public void AFailureWithNoCauseAtAllIsTheStepsFailure()
    {
        Assert.False(TemplateStepFailures.TryMap(null, out var mapped));

        Assert.Equal("EXECUTION_FAILED", mapped.Code);
    }

    [Fact]
    public void OnlyTheUnreachableSourceIsTheOneAByNameReferenceAnswersWithItsBaseline()
    {
        Assert.True(TemplateStepFailures.IsSourceUnavailable(Application("TEMPLATE_SOURCE_UNAVAILABLE")));
        Assert.False(TemplateStepFailures.IsSourceUnavailable(Application("TEMPLATE_NOT_FOUND")));
        Assert.False(TemplateStepFailures.IsSourceUnavailable(Application("TEMPLATE_INVALID")));
        Assert.False(TemplateStepFailures.IsSourceUnavailable(new InvalidOperationException("no attempt left")));
        Assert.False(TemplateStepFailures.IsSourceUnavailable(null));
    }

    private static TemplateStepFailures.Mapped Map(string code, string message)
    {
        Assert.True(TemplateStepFailures.TryMap(Application(code, message), out var mapped));
        return mapped;
    }

    private static ApplicationFailureException Application(string code, string message = "a message") =>
        new(message, errorType: code, nonRetryable: true);
}
