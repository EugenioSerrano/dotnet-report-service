namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the bounded window decided to do next.</summary>
public enum PdfOutputWindowAction
{
    /// <summary>Schedule the block <see cref="PdfOutputWindowDecision.NextIndex"/>.</summary>
    Schedule,

    /// <summary>Wait for one outstanding block to finish.</summary>
    Wait,

    /// <summary>The run has completed its share of blocks and continues as a new run.</summary>
    ContinueAsNew,

    /// <summary>Every block is committed; the output may assemble.</summary>
    Complete,
}

/// <summary>The pure decision of the bounded window.</summary>
/// <param name="Action">What to do next.</param>
/// <param name="NextIndex">The block to schedule, when the action is <see cref="PdfOutputWindowAction.Schedule"/>.</param>
public sealed record PdfOutputWindowDecision(PdfOutputWindowAction Action, int? NextIndex);
