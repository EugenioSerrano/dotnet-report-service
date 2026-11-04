using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Resolution;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>What one by-name resolution is asked for: a template, the consumer it is resolved for, and the job it runs for.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="RequestingConsumerId">The consumer the version is chosen for.</param>
/// <param name="ReportJobId">The job whose step this is, when there is one.</param>
public sealed record ResolveTemplateVersionInput(
    string TemplateId,
    string RequestingConsumerId,
    string? ReportJobId = null);

/// <summary>
/// What the caller needs from a by-name resolution: the version it may use, the
/// values it is pinned to, and how the resolution arrived at it — never the
/// package or its manifest.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version the resolution chose.</param>
/// <param name="OwnerConsumerId">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use it.</param>
/// <param name="CommitSha">The commit the version was read at.</param>
/// <param name="PackageSha256">The SHA-256 of the package's bytes, lower-case hexadecimal.</param>
/// <param name="PackageSize">The size of the package's bytes.</param>
/// <param name="Engine">The engine that renders it, as the record stores it.</param>
/// <param name="EngineVersion">The engine version that will render it.</param>
/// <param name="SchemaRef">The package-relative path of the schema.</param>
/// <param name="Outcome">One of <see cref="ResolveTemplateVersionOutcomes"/>.</param>
public sealed record ResolveTemplateVersionResult(
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

/// <summary>The two ways a by-name resolution succeeds, as the caller is told them.</summary>
public static class ResolveTemplateVersionOutcomes
{
    /// <summary>The resolution took a version it had not taken before.</summary>
    public const string Resolved = "RESOLVED";

    /// <summary>The source could not be checked, and the newest release already taken was used.</summary>
    public const string ResolvedFromTakenCopy = "RESOLVED_FROM_TAKEN_COPY";
}

/// <summary>
/// The by-name resolution as the queue serves it: one activity, on the PDF
/// rendering queue, which chooses the version a template referenced without a
/// version uses and answers the values a job is pinned to. A refusal ends the
/// activity with the resolution's own code as the failure's type and its own
/// retryable flag, so the engine retries a source that is down and never retries
/// a template its author has to fix. The activity beats while it runs; it never
/// carries the package, its manifest or a package path.
/// </summary>
public sealed class ResolveTemplateVersionActivity(TemplateVersionResolver resolver)
{
    /// <summary>The name the activity is registered and scheduled under.</summary>
    public const string Name = "ResolveTemplateVersion";

    /// <summary>The most of a refusal's reason a job is ever shown, in characters.</summary>
    public const int ReasonLimit = 1024;

    /// <summary>Resolves the version a by-name reference uses.</summary>
    /// <param name="input">The template and the consumer it is resolved for.</param>
    [Activity(Name)]
    public async Task<ResolveTemplateVersionResult> RunAsync(ResolveTemplateVersionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        using var beatEnds = new CancellationTokenSource();
        var beat = BeatAsync(beatEnds.Token);
        TemplateResolutionOutcome outcome;
        try
        {
            (outcome, _) = await resolver
                .ResolveAsync(input.TemplateId, input.RequestingConsumerId, input.ReportJobId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await beatEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }

        if (outcome is RefusedOutcome refusal)
        {
            // The refusal is the job's failure: its code is the failure's type, and
            // only a resolution that may succeed later is retried.
            throw new ApplicationFailureException(Capped(refusal.Reason), errorType: refusal.Code, nonRetryable: !refusal.Retryable);
        }

        var resolved = outcome as ResolvedOutcome
            ?? throw new InvalidOperationException("the resolution answered a state this activity does not know");
        var record = resolved.Record;
        return new ResolveTemplateVersionResult(
            record.TemplateId,
            record.Version,
            record.OwnerConsumerId,
            record.Shared,
            record.CommitSha,
            record.PackageSha256,
            record.PackageSize,
            TemplateEngineNames.ToStoredText(record.Engine),
            record.EngineVersion,
            record.SchemaRef,
            resolved.FromTakenCopy
                ? ResolveTemplateVersionOutcomes.ResolvedFromTakenCopy
                : ResolveTemplateVersionOutcomes.Resolved);
    }

    /// <summary>The reason as a job may hold it: the text, cut at the limit and marked.</summary>
    private static string Capped(string reason) => reason.Length <= ReasonLimit
        ? reason
        : string.Concat(reason.AsSpan(0, ReasonLimit - 3), "...");

    /// <summary>Says the activity is still working until it ends, so a slow walk does not lose the attempt.</summary>
    private static async Task BeatAsync(CancellationToken cancellationToken)
    {
        if (!ActivityExecutionContext.HasCurrent)
        {
            return;
        }

        using var timer = new PeriodicTimer(TemplateResolutionActivityOptions.HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ActivityExecutionContext.Current.Heartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // The resolution has ended: there is nothing left to report.
        }
    }
}
