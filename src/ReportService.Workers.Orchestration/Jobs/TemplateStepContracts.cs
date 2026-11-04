using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The orchestration worker's view of the two activities the PDF worker serves for a
/// job's template: the exact take and the by-name resolution. They are scheduled by
/// name on <c>report-pdf-rendering</c>, so this process never loads the PDF worker's
/// dependencies, and the records below mirror the delivered ones member for member -
/// same member names, same types - so the answer the PDF worker writes is read here
/// exactly as it was written. The integration suite compares both sides; a drift is a
/// stop, never a silent retry.
/// </summary>
public static class TemplateStepContracts
{
    /// <summary>The name of the exact take, as the PDF worker registers it.</summary>
    public const string TakeTemplateVersion = "TakeTemplateVersion";

    /// <summary>The name of the by-name resolution, as the PDF worker registers it.</summary>
    public const string ResolveTemplateVersion = "ResolveTemplateVersion";

    /// <summary>The three ways a take succeeds, as the PDF worker reports them.</summary>
    public static class TakeOutcomes
    {
        /// <summary>The take read the version and kept it.</summary>
        public const string Taken = "TAKEN";

        /// <summary>The version was already taken and this consumer may use it.</summary>
        public const string AlreadyTaken = "ALREADY_TAKEN";

        /// <summary>The version was taken concurrently with different bytes; the taken copy stays.</summary>
        public const string ConflictLogged = "CONFLICT_LOGGED";
    }

    /// <summary>The two ways a by-name resolution succeeds, as the PDF worker reports them.</summary>
    public static class ResolutionOutcomes
    {
        /// <summary>The resolution took a version it had not taken before.</summary>
        public const string Resolved = "RESOLVED";

        /// <summary>The source could not be checked, and the newest release already taken was used.</summary>
        public const string ResolvedFromTakenCopy = "RESOLVED_FROM_TAKEN_COPY";
    }
}

/// <summary>What one take is asked for, as the PDF worker reads it.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The exact version to take.</param>
/// <param name="RequestingConsumerId">The consumer the version is taken for.</param>
/// <param name="ReportJobId">The job whose step this is.</param>
public sealed record TakeTemplateVersionStepInput(
    string TemplateId,
    string Version,
    string RequestingConsumerId,
    string? ReportJobId = null);

/// <summary>What the caller needs from a take, as the PDF worker answers it.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version.</param>
/// <param name="OwnerConsumerId">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use it.</param>
/// <param name="CommitSha">The commit the version was read at.</param>
/// <param name="PackageSha256">The SHA-256 of the package's bytes.</param>
/// <param name="PackageSize">The size of the package's bytes.</param>
/// <param name="Engine">The engine that renders it, as the record stores it.</param>
/// <param name="EngineVersion">The engine version that will render it.</param>
/// <param name="SchemaRef">The package-relative path of the schema.</param>
/// <param name="Outcome">One of <see cref="TemplateStepContracts.TakeOutcomes"/>.</param>
public sealed record TakeTemplateVersionStepResult(
    string TemplateId,
    string Version,
    string OwnerConsumerId,
    bool Shared,
    string CommitSha,
    string PackageSha256,
    long PackageSize,
    string Engine,
    string EngineVersion,
    string SchemaRef,
    string Outcome);

/// <summary>What one by-name resolution is asked for, as the PDF worker reads it.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="RequestingConsumerId">The consumer the version is chosen for.</param>
/// <param name="ReportJobId">The job whose step this is.</param>
public sealed record ResolveTemplateVersionStepInput(
    string TemplateId,
    string RequestingConsumerId,
    string? ReportJobId = null);

/// <summary>What the caller needs from a by-name resolution, as the PDF worker answers it.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version the resolution chose.</param>
/// <param name="OwnerConsumerId">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use it.</param>
/// <param name="CommitSha">The commit the version was read at.</param>
/// <param name="PackageSha256">The SHA-256 of the package's bytes.</param>
/// <param name="PackageSize">The size of the package's bytes.</param>
/// <param name="Engine">The engine that renders it, as the record stores it.</param>
/// <param name="EngineVersion">The engine version that will render it.</param>
/// <param name="SchemaRef">The package-relative path of the schema.</param>
/// <param name="Outcome">One of <see cref="TemplateStepContracts.ResolutionOutcomes"/>.</param>
public sealed record ResolveTemplateVersionStepResult(
    string TemplateId,
    string Version,
    string OwnerConsumerId,
    bool Shared,
    string CommitSha,
    string PackageSha256,
    long PackageSize,
    string Engine,
    string EngineVersion,
    string SchemaRef,
    string Outcome);

/// <summary>The exact take, as this worker schedules it on the PDF worker's queue.</summary>
public interface ITakeTemplateVersionStep
{
    /// <summary>Takes one template version on behalf of one consumer.</summary>
    /// <param name="input">The version to take and who asks for it.</param>
    [Activity(TemplateStepContracts.TakeTemplateVersion)]
    Task<TakeTemplateVersionStepResult> RunAsync(TakeTemplateVersionStepInput input);
}

/// <summary>The by-name resolution, as this worker schedules it on the PDF worker's queue.</summary>
public interface IResolveTemplateVersionStep
{
    /// <summary>Resolves the version a by-name reference uses.</summary>
    /// <param name="input">The template and the consumer it is resolved for.</param>
    [Activity(TemplateStepContracts.ResolveTemplateVersion)]
    Task<ResolveTemplateVersionStepResult> RunAsync(ResolveTemplateVersionStepInput input);
}
