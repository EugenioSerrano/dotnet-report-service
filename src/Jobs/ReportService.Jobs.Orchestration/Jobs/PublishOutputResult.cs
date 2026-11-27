namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the publication did, with the artifact that now exists.</summary>
/// <param name="Outcome">Either <c>PUBLISHED</c> or <c>ALREADY_PUBLISHED</c>.</param>
/// <param name="ArtifactId">The identifier of the artifact that exists.</param>
public sealed record PublishOutputResult(string Outcome, string ArtifactId)
{
    /// <summary>The outcome of a publication this call wrote.</summary>
    public const string Published = "PUBLISHED";

    /// <summary>The outcome of a publication an earlier call wrote; nothing was written.</summary>
    public const string AlreadyPublished = "ALREADY_PUBLISHED";
}
