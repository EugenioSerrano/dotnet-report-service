using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// US-010 AC-7 over ADR-048 R0 and ADR-012 §3.10: a run that ends by a failure decides how it
/// ends from the job's durable state and nothing else. A job being cancelled is closed through
/// the non-cancellable finalisation — the one writer of the terminal <c>CANCELLED</c> — and the
/// failure it ended with is not written; a job that already ended is left untouched; every other
/// state is the failure the run always wrote. The oracles are the approved US-010 AC-7 and the
/// accepted ADRs, never the implementation (G06). The decision is a pure function, so the whole
/// table is exercised without a Temporal worker.
/// </summary>
public sealed class DecideEndAfterFailureTests
{
    /// <summary>
    /// The whole table, one row per state the enumeration declares. It is the test's own copy of
    /// the decision, so a state added to the enumeration without a decision here fails
    /// <see cref="EveryStateTheEnumerationDeclaresIsDecided"/> instead of silently taking the
    /// ordinary failure path.
    /// </summary>
    private static readonly (ReportJobStatus Status, JobEndDecision Expected)[] Table =
    [
        (ReportJobStatus.Cancelling, JobEndDecision.CloseAsCancelled),
        (ReportJobStatus.Cancelled, JobEndDecision.Stop),
        (ReportJobStatus.Succeeded, JobEndDecision.Stop),
        (ReportJobStatus.Failed, JobEndDecision.Stop),
        (ReportJobStatus.PartiallySucceeded, JobEndDecision.Stop),
        (ReportJobStatus.Queued, JobEndDecision.FailAsToday),
        (ReportJobStatus.Validating, JobEndDecision.FailAsToday),
        (ReportJobStatus.Partitioning, JobEndDecision.FailAsToday),
        (ReportJobStatus.Running, JobEndDecision.FailAsToday),
        (ReportJobStatus.Finalizing, JobEndDecision.FailAsToday),
    ];

    /// <summary>The table as the theory reads it.</summary>
    public static TheoryData<ReportJobStatus, JobEndDecision> TheTable { get; } =
        new(Table.Select(row => (row.Status, row.Expected)));

    /// <summary>
    /// AC-7's one production gap: the engine can report a lost attempt as a timeout failure rather
    /// than as a cancellation, the run takes its ordinary failure branch, and the job it was
    /// carrying is being cancelled. The run must close the job as <c>CANCELLED</c> — never leave it
    /// <c>CANCELLING</c> and never turn a cancellation into a failure (ADR-048 R0).
    /// </summary>
    [Fact]
    public void AJobBeingCancelledIsClosedAsCancelled()
    {
        Assert.Equal(JobEndDecision.CloseAsCancelled, ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Cancelling));
    }

    /// <summary>
    /// ADR-048 §3.4(1): a job that already ended is never moved, whatever ended it and however the
    /// interrupted run's failure arrived. Nothing is written for it.
    /// </summary>
    [Fact]
    public void AJobThatAlreadyEndedIsLeftUntouched()
    {
        JobEndDecision[] stopped =
        [
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Cancelled),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Succeeded),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Failed),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.PartiallySucceeded),
        ];

        Assert.All(stopped, decision => Assert.Equal(JobEndDecision.Stop, decision));
    }

    /// <summary>
    /// The ordinary path is unchanged: a failure that races nothing fails the job exactly as it
    /// always did, with the code, reason and message the failing step carried (AC-7's "a job that
    /// is not cancelled is unaffected").
    /// </summary>
    [Fact]
    public void AFailureOfAJobThatIsNeitherCancelledNorTerminalFailsItAsToday()
    {
        JobEndDecision[] asToday =
        [
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Queued),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Validating),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Partitioning),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Running),
            ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Finalizing),
        ];

        Assert.All(asToday, decision => Assert.Equal(JobEndDecision.FailAsToday, decision));
    }

    /// <summary>
    /// The failure modes the design names, as the state each of them leaves: a failure of the data
    /// step, of the template step or of a printed output is the same decision, because the guard
    /// reads the job and nothing of the step that failed. A job being cancelled closes as
    /// <c>CANCELLED</c> whatever failed; a job already <c>CANCELLED</c> writes nothing; a job that
    /// is <c>RUNNING</c> fails as today.
    /// </summary>
    [Fact]
    public void TheDecisionDependsOnTheJobAndNotOnWhichStepFailed()
    {
        Assert.Equal(JobEndDecision.CloseAsCancelled, ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Cancelling));
        Assert.Equal(JobEndDecision.Stop, ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Cancelled));
        Assert.Equal(JobEndDecision.FailAsToday, ReportJobWorkflow.DecideEndAfterFailure(ReportJobStatus.Running));
    }

    /// <summary>
    /// The table is the whole decision: every state the enumeration declares is in it, so a state
    /// added to the enumeration later fails here until it is decided, rather than taking the
    /// ordinary path by default.
    /// </summary>
    [Fact]
    public void EveryStateTheEnumerationDeclaresIsDecided()
    {
        var declared = Enum.GetValues<ReportJobStatus>();
        var covered = Table.Select(row => row.Status).ToArray();

        Assert.Equal(declared.OrderBy(status => status), covered.OrderBy(status => status));
    }

    /// <summary>
    /// Each row of the table is the decision the product answers.
    /// </summary>
    /// <param name="status">The state the job's record carries.</param>
    /// <param name="expected">How the run ends in that state.</param>
    [Theory]
    [MemberData(nameof(TheTable))]
    public void EachDeclaredStateIsDecidedAsTheTableSays(ReportJobStatus status, JobEndDecision expected)
    {
        Assert.Equal(expected, ReportJobWorkflow.DecideEndAfterFailure(status));
    }

    /// <summary>
    /// The design fails closed: a value the enumeration does not declare is refused, never
    /// silently failed. An engine or a record that hands the workflow such a value is a defect
    /// that must surface, not a job quietly written <c>FAILED</c>.
    /// </summary>
    [Fact]
    public void AValueTheEnumerationDoesNotDeclareIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportJobWorkflow.DecideEndAfterFailure((ReportJobStatus)int.MaxValue));
    }
}
