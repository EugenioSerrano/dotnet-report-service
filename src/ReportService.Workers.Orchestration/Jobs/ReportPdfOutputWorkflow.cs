using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// What one output's execution is asked to produce: the PDF output of one job. The
/// continuation members are optional and default to the start, so every payload
/// recorded before the multi-block path existed deserializes unchanged.
/// </summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the job's PDF output.</param>
/// <param name="NextBlockIndex">The next block a continued run schedules, from zero.</param>
/// <param name="CompletedBlocks">How many blocks a continued run's predecessor completed.</param>
public sealed record ReportPdfOutputInput(
    string ReportJobId,
    string OutputId,
    int NextBlockIndex = 0,
    int CompletedBlocks = 0);

/// <summary>
/// The one PDF output of a job, as its own execution: it starts the output, has its
/// blocks printed and verified on the PDF worker, assembles the verified parts in
/// manifest order on the merge worker, and publishes the assembled document as the
/// output's one artifact in the transaction that also marks the output succeeded. A
/// single-block output keeps the recorded path exactly: one render and the part
/// published as the artifact. A multi-block output is gated by its own patch, keeps
/// at most W renders outstanding, continues as a new run every 1 000 completed
/// blocks, and assembles only after every block committed. A failure that ends the
/// work ends the output with the code the failure carries, and the output is never
/// left running: the job that started it closes from the output's own state. It
/// holds no clock, no random value and no I/O of its own, so its history replays
/// exactly.
/// </summary>
[Workflow(WorkflowType)]
public sealed class ReportPdfOutputWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "ReportPdfOutput";

    /// <summary>The suffix of the child execution's identifier: one execution per output.</summary>
    public const string WorkflowIdSuffix = "/output/pdf";

    /// <summary>The code an output ends with when the failure carries no code of its own.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>
    /// The identifier of the patch that gates the multi-block path: the partition
    /// facts read, the bounded fan-out and the assembly. A run recorded before it
    /// carries no marker, so it replays with the patch inactive and keeps its old
    /// command sequence exactly.
    /// </summary>
    public const string MultiBlockRenderPatch = "multi-block-render-v1";

    private static int _window = PdfOutputConcurrencyOptions.DefaultWindow;

    /// <summary>Publishes the configured window to the workflow type, once, while the process plans its workers.</summary>
    /// <param name="window">The most renders one output may keep outstanding.</param>
    public static void Configure(int window)
    {
        _window = PdfOutputConcurrencyOptions.Resolve(window);
    }

    /// <summary>The window as this process published it.</summary>
    public static int CurrentWindow() => _window;

    /// <summary>
    /// The codes a failure of the data or the template carries, and the only ones an output
    /// keeps without reading the cause record: the same data or the same template reported
    /// again reaches the same verdict, so nothing about the cause would change the answer.
    /// </summary>
    private static readonly IReadOnlyList<string> DeterministicRefusals =
    [
        "PAGINATION_FAILED",
        "DATA_INVALID",
        "TEMPLATE_INVALID",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
        "OUTPUT_LIMIT_EXCEEDED",
        "ARTIFACT_INTEGRITY_FAILED",
    ];

    /// <summary>The identifier of the one execution that produces <paramref name="reportJobId"/>'s PDF output.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public static string WorkflowIdFor(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return ReportJobWorkflow.WorkflowIdFor(reportJobId) + WorkflowIdSuffix;
    }

    /// <summary>Produces one output of one job.</summary>
    /// <param name="input">The job and the output.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportPdfOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var metadata = ReportJobActivityOptions.Create();

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.TransitionOutputAsync(
                new TransitionOutputInput(input.OutputId)),
            metadata).ConfigureAwait(true);

        try
        {
            if (!Workflow.Patched(MultiBlockRenderPatch))
            {
                await RunSingleBlockAsync(input, metadata).ConfigureAwait(true);
                return;
            }

            var facts = await Workflow.ExecuteActivityAsync(
                (PdfOutputActivities activities) => activities.ReadPartitionFactsAsync(
                    new PdfOutputPartitionFactsInput(input.ReportJobId)),
                metadata).ConfigureAwait(true);

            if (facts.BlockCount <= 1)
            {
                await RunSingleBlockAsync(input, metadata).ConfigureAwait(true);
                return;
            }

            await RunFanOutAsync(input, facts.BlockCount).ConfigureAwait(true);

            var assembled = await Workflow.ExecuteActivityAsync(
                (IAssemblePdfStep step) => step.RunAsync(new AssemblePdfStepInput(input.ReportJobId, input.OutputId)),
                AssemblePdfStepOptions.Create()).ConfigureAwait(true);

            await Workflow.ExecuteActivityAsync(
                (PdfOutputActivities activities) => activities.PublishOutputAsync(new PublishOutputInput(
                    input.ReportJobId,
                    input.OutputId,
                    assembled.ObjectKey,
                    assembled.Sha256,
                    assembled.SizeBytes,
                    assembled.PageCount)),
                metadata).ConfigureAwait(true);
        }
        catch (BlockRenderFailure blockFailure)
        {
            var carried = ((blockFailure.InnerException as ActivityFailureException)?.InnerException as ApplicationFailureException)?.ErrorType;
            var (code, reason) = await ExhaustionAsync(
                carried,
                () => Workflow.ExecuteActivityAsync(
                    (AttemptCauseActivities activities) => activities.FindLastAttemptCauseAsync(
                        new FindLastAttemptCauseInput(input.OutputId, blockFailure.BlockIndex)),
                    AttemptCauseActivities.Options())).ConfigureAwait(true);

            await FailTheOutputAsync(
                input,
                code,
                reason,
                blockFailure.BlockIndex,
                AttemptsOf(carried),
                metadata).ConfigureAwait(true);
        }
        catch (ActivityFailureException failure)
            when (!TemporalException.IsCanceledException(failure))
        {
            // The render, the assembly or the publication could not finish: the output ends
            // with the code the failure carries, and one that carries none ends with the
            // generic code, so the job is never left waiting on an output that will not end.
            // A cancellation is not a failure: the run was cancelled under its parent, which
            // owns the output through the job's finalisation, so nothing is ended here.
            var carried = (failure.InnerException as ApplicationFailureException)?.ErrorType;
            var (code, reason) = await ExhaustionAsync(
                carried,
                () => Workflow.ExecuteActivityAsync(
                    (AttemptCauseActivities activities) => activities.FindLastAttemptCauseAsync(
                        new FindLastAttemptCauseInput(input.OutputId, 0)),
                    AttemptCauseActivities.Options())).ConfigureAwait(true);

            await FailTheOutputAsync(
                input,
                code,
                reason,
                blockIndex: null,
                AttemptsOf(carried),
                metadata).ConfigureAwait(true);
        }
    }

    /// <summary>The recorded single-block path: one render, and the verified part published as the artifact.</summary>
    private static async Task RunSingleBlockAsync(ReportPdfOutputInput input, ActivityOptions metadata)
    {
        var part = await Workflow.ExecuteActivityAsync(
            (IRenderPdfBlockStep step) => step.RunAsync(new RenderPdfBlockStepInput(
                input.ReportJobId,
                input.OutputId,
                0)),
            RenderPdfBlockStepOptions.Create()).ConfigureAwait(true);

        await Workflow.ExecuteActivityAsync(
            (PdfOutputActivities activities) => activities.PublishOutputAsync(new PublishOutputInput(
                input.ReportJobId,
                input.OutputId,
                part.PartKey,
                part.Sha256,
                part.SizeBytes,
                part.PageCount)),
            metadata).ConfigureAwait(true);
    }

    /// <summary>
    /// The bounded fan-out: at most W renders outstanding, every block index scheduled
    /// exactly once, completion in any order, and a run that completes its share
    /// continues as a new one carrying only the next index and the counters.
    /// </summary>
    private static async Task RunFanOutAsync(ReportPdfOutputInput input, int blockCount)
    {
        var window = PdfOutputConcurrencyOptions.Resolve(_window);
        var next = input.NextBlockIndex;
        var completed = input.CompletedBlocks;
        var outstanding = new SortedDictionary<int, Task<RenderPdfBlockStepResult>>();

        while (true)
        {
            var decision = PdfOutputWindow.Decide(blockCount, next, completed, outstanding.Count, window);
            switch (decision.Action)
            {
                case PdfOutputWindowAction.Schedule:
                    var index = decision.NextIndex!.Value;
                    outstanding[index] = Workflow.ExecuteActivityAsync(
                        (IRenderPdfBlockStep step) => step.RunAsync(new RenderPdfBlockStepInput(
                            input.ReportJobId,
                            input.OutputId,
                            index)),
                        RenderPdfBlockStepOptions.Create());
                    next = index + 1;
                    break;

                case PdfOutputWindowAction.Wait:
                    var finished = await Workflow.WhenAnyAsync(outstanding.Values).ConfigureAwait(true);
                    var finishedEntry = outstanding.First(entry => ReferenceEquals(entry.Value, finished));
                    outstanding.Remove(finishedEntry.Key);
                    try
                    {
                        _ = await finished.ConfigureAwait(true);
                    }
                    catch (Exception failure) when (failure is ActivityFailureException or ApplicationFailureException)
                    {
                        // The block's failure is the output's: the fan-out stops scheduling new
                        // blocks, and the child ends with the code the failure carries. Pending
                        // activities are cancelled when this execution closes.
                        throw new BlockRenderFailure(finishedEntry.Key, failure);
                    }

                    completed++;
                    break;

                case PdfOutputWindowAction.ContinueAsNew:
                    Workflow.Logger.LogInformation(
                        "Output {OutputId} continued as new after {Completed} blocks, next index {Next}",
                        input.OutputId,
                        completed,
                        next);
                    var continuation = input with
                    {
                        NextBlockIndex = next,
                        CompletedBlocks = 0,
                    };
                    throw Workflow.CreateContinueAsNewException(
                        (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(continuation));

                case PdfOutputWindowAction.Complete:
                    return;

                default:
                    throw new InvalidOperationException("the window decided an action the workflow does not know.");
            }
        }
    }

    /// <summary>
    /// How many attempts the engine gave the block before the output ended: one when the failure
    /// carries the code a non-retried step raises, and the policy's budget otherwise - a deadline
    /// or a lost worker is retried to the last allowed attempt.
    /// </summary>
    /// <param name="carried">The code the last failure carries, or <see langword="null"/>.</param>
    private static int AttemptsOf(string? carried) =>
        string.Equals(carried, ExecutionFailed, StringComparison.Ordinal)
            ? 1
            : RenderPdfBlockStepOptions.MaximumAttempts;

    /// <summary>The code a carried failure keeps when the attempt that raised it hit the block's deadline.</summary>
    private const string RenderTimeout = "RENDER_TIMEOUT";

    /// <summary>
    /// The code and the reason an output that has no attempt left ends with, from the code its
    /// last failure carries and the cause that failure's last attempt recorded. A refusal of
    /// the data or the template keeps its own code and reads nothing; every other failure reads
    /// the last recorded cause, so a deadline is reported as the timeout it was and a crash,
    /// which records nothing, as the generic code. The one read this map makes is the step's
    /// only call here, and the map never publishes anything.
    /// </summary>
    /// <param name="carried">The code the last failure carries, or <see langword="null"/> when it carries none.</param>
    /// <param name="read">The read of the block's last recorded cause.</param>
    /// <returns>The code and the reason the output ends with.</returns>
    public static async Task<(string Code, string? Reason)> ExhaustionAsync(
        string? carried,
        Func<Task<LastAttemptCause?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (IsDeterministicRefusal(carried))
        {
            return (carried!, null);
        }

        var last = await read().ConfigureAwait(true);

        var code = string.Equals(carried, RenderTimeout, StringComparison.Ordinal)
            || AttemptCauseVocabulary.IsDeadline(last?.Cause)
                ? RenderTimeout
                : ExecutionFailed;
        return (code, AttemptCauseVocabulary.ReasonOf(last?.Cause));
    }

    /// <summary>Whether a carried code is one of the deterministic refusals.</summary>
    /// <param name="carried">The carried error type, when the failure has one.</param>
    /// <returns>Whether the code keeps itself without reading a cause.</returns>
    private static bool IsDeterministicRefusal(string? carried) =>
        carried is { Length: > 0 } code && DeterministicRefusals.Contains(code, StringComparer.Ordinal);

    /// <summary>
    /// The code an output ends with for a failure: the failure's own code when it is one of
    /// the codes this path raises, and the generic code otherwise. A runtime exception reaches
    /// the workflow with its own type name where a code would be, and that type is not a
    /// consumer's answer.
    /// </summary>
    /// <param name="failure">The failure, as the engine reported it inside the step's failure.</param>
    /// <returns>The code to end the output with.</returns>
    public static string CodeOf(Exception? failure) =>
        failure is ApplicationFailureException application
        && application.ErrorType is { Length: > 0 } code
        && DomainCodes.Contains(code)
            ? code
            : ExecutionFailed;

    /// <summary>The codes the steps of a printed output raise, and the only ones an output keeps.</summary>
    private static readonly HashSet<string> DomainCodes = new(StringComparer.Ordinal)
    {
        "PAGINATION_FAILED",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
        "RENDER_TIMEOUT",
        "DATA_INVALID",
        "TEMPLATE_INVALID",
        "EXECUTION_FAILED",
        "OUTPUT_LIMIT_EXCEEDED",
        "ARTIFACT_INTEGRITY_FAILED",
    };

    private static async Task FailTheOutputAsync(
        ReportPdfOutputInput input,
        string code,
        string? reason,
        int? blockIndex,
        int attempts,
        ActivityOptions options)
    {
        if (reason is not null && blockIndex is { } block)
        {
            Workflow.Logger.LogWarning(
                "Block {BlockIndex} of output {OutputId} ended after {Attempts} attempts: {Code} ({Reason})",
                block,
                input.OutputId,
                attempts,
                code,
                reason);
        }

        Workflow.Logger.LogWarning(
            "Output {OutputId} ended with {ErrorCode}",
            input.OutputId,
            code);

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.FailOutputAsync(
                new FailOutputInput(input.OutputId, code, reason)),
            options).ConfigureAwait(true);
    }
}

/// <summary>A block's render failed: the block index is kept so the output's mapping reads the block's own cause.</summary>
internal sealed class BlockRenderFailure(int blockIndex, Exception innerException)
    : Exception("a block of the output failed", innerException)
{
    /// <summary>The block whose render failed.</summary>
    public int BlockIndex { get; } = blockIndex;
}
