using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The output's own execution as the workflow type declares it: the name and the identifier
/// it is started under, and the code an output ends with for each kind of failure. The steps
/// it schedules are recorded against a real engine and replayed by the histories of the job
/// workflow; what is asserted here is the mapping a history cannot show.
/// </summary>
public sealed class ReportPdfOutputWorkflowTests
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    [Fact]
    public void TheWorkflowIsRegisteredUnderTheNameAndIdentifierTheParentStarts()
    {
        Assert.Equal("ReportPdfOutput", ReportPdfOutputWorkflow.WorkflowType);
        Assert.Equal(
            "report-job/job-0123456789abcdef0123456789abcdef/output/pdf",
            ReportPdfOutputWorkflow.WorkflowIdFor(ReportJobId));
        Assert.StartsWith(ReportJobWorkflow.WorkflowIdFor(ReportJobId), ReportPdfOutputWorkflow.WorkflowIdFor(ReportJobId), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PAGINATION_FAILED")]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    [InlineData("QUOTA_EXCEEDED")]
    [InlineData("RENDER_TIMEOUT")]
    [InlineData("EXECUTION_FAILED")]
    public void ADomainFailureKeepsTheCodeItCarries(string code)
    {
        var failure = new ApplicationFailureException("the step refused", errorType: code, nonRetryable: true);

        Assert.Equal(code, ReportPdfOutputWorkflow.CodeOf(failure));
    }

    [Fact]
    public void AFailureThatCarriesNoCodeEndsTheOutputWithTheGenericCode()
    {
        Assert.Equal(
            ReportPdfOutputWorkflow.ExecutionFailed,
            ReportPdfOutputWorkflow.CodeOf(new InvalidOperationException("the engine lost the step")));
        Assert.Equal(
            ReportPdfOutputWorkflow.ExecutionFailed,
            ReportPdfOutputWorkflow.CodeOf(null));
        Assert.Equal(
            ReportPdfOutputWorkflow.ExecutionFailed,
            ReportPdfOutputWorkflow.CodeOf(new ApplicationFailureException("no code", errorType: null, nonRetryable: false)));
    }

    [Fact]
    public void ARuntimeExceptionTypeIsNotAConsumersCode()
    {
        // The engine reports a .NET exception with its own type name where a code would be;
        // that name is not what a consumer is told the job failed with.
        var failure = new ApplicationFailureException(
            "the step threw", errorType: "InvalidOperationException", nonRetryable: false);

        Assert.Equal(ReportPdfOutputWorkflow.ExecutionFailed, ReportPdfOutputWorkflow.CodeOf(failure));
    }

    [Fact]
    public void AJobMessageIsTheProductsOwnTextAndNothingElse()
    {
        var refusal = new ApplicationFailureException(
            "PAGINATION_FAILED reason=text-dropped page=2 record=7",
            errorType: "PAGINATION_FAILED",
            nonRetryable: true);

        Assert.Equal(
            "PAGINATION_FAILED reason=text-dropped page=2 record=7",
            ReportJobWorkflow.MessageOf(refusal));
        Assert.Null(ReportJobWorkflow.MessageOf(new TimeoutException("the transport gave up")));
        Assert.Null(ReportJobWorkflow.MessageOf(null));
    }

    [Fact]
    public void TheOutputOfAJobIsTheOneOfTheJobsOwnExecution()
    {
        // The input carries identifiers only: the workflow reads neither a record nor a document.
        var input = new ReportPdfOutputInput(ReportJobId, OutputId);

        Assert.Equal(ReportJobId, input.ReportJobId);
        Assert.Equal(OutputId, input.OutputId);
    }
}
