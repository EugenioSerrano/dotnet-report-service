namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>The job whose template version's per-page code declaration is read.</summary>
/// <param name="ReportJobId">The job.</param>
public sealed record PageCodeDeclarationInput(string ReportJobId);

/// <summary>Whether the job's taken template version declares a per-page code.</summary>
/// <param name="DeclaresPageCode">Whether it does.</param>
public sealed record PageCodeDeclarationResult(bool DeclaresPageCode);
