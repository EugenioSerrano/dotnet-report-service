using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Take;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>What one take is asked for: a version, the consumer it is taken for, and the job it runs for.</summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The exact version to take.</param>
/// <param name="RequestingConsumerId">The consumer the version is taken for.</param>
/// <param name="ReportJobId">The job whose step this is, when there is one.</param>
public sealed record TakeTemplateVersionInput(
    string TemplateId,
    string Version,
    string RequestingConsumerId,
    string? ReportJobId = null);

/// <summary>
/// What the caller needs from a take: which version it now has, the values it is
/// pinned to, and which of the take's success states produced it — never the
/// package or its manifest (ADR-012 §3.7).
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version.</param>
/// <param name="OwnerConsumerId">The consumer the version belongs to.</param>
/// <param name="Shared">Whether every consumer may use it.</param>
/// <param name="CommitSha">The commit the version was read at.</param>
/// <param name="PackageSha256">The SHA-256 of the package's bytes, lower-case hexadecimal.</param>
/// <param name="PackageSize">The size of the package's bytes.</param>
/// <param name="Engine">The engine that renders it, as the record stores it.</param>
/// <param name="EngineVersion">The engine version that will render it.</param>
/// <param name="SchemaRef">The package-relative path of the schema.</param>
/// <param name="Outcome">One of <see cref="TakeTemplateVersionOutcomes"/>.</param>
public sealed record TakeTemplateVersionResult(
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

/// <summary>The three ways a take succeeds, as the caller is told them.</summary>
public static class TakeTemplateVersionOutcomes
{
    /// <summary>The take read the version and kept it.</summary>
    public const string Taken = "TAKEN";

    /// <summary>The version was already taken and the caller may use it.</summary>
    public const string AlreadyTaken = "ALREADY_TAKEN";

    /// <summary>The version was taken concurrently with different bytes; the taken copy stays.</summary>
    public const string ConflictLogged = "CONFLICT_LOGGED";
}

/// <summary>
/// The take as the queue serves it: one activity, on the PDF rendering queue,
/// which reads one version of one template and answers the values a job is pinned
/// to. A refusal ends the activity with the take's own code as the failure's type
/// and its own retryable flag, so the engine retries a source that is down and
/// never retries a package its author has to fix. The activity beats while it
/// runs; it never carries the package, its manifest or the source's own message.
/// </summary>
public sealed class TakeTemplateVersionActivity(TemplateTake take)
{
    /// <summary>The name the activity is registered and scheduled under.</summary>
    public const string Name = "TakeTemplateVersion";

    /// <summary>Takes one template version on behalf of one consumer.</summary>
    /// <param name="input">The version to take and who asks for it.</param>
    [Activity(Name)]
    public async Task<TakeTemplateVersionResult> RunAsync(TakeTemplateVersionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        using var beatEnds = new CancellationTokenSource();
        var beat = BeatAsync(beatEnds.Token);
        TemplateTakeOutcome outcome;
        try
        {
            outcome = await take
                .TakeAsync(input.TemplateId, input.Version, input.RequestingConsumerId, commit: null, input.ReportJobId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await beatEnds.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }

        if (outcome is RefusedVersion refusal)
        {
            // The refusal is the job's failure: its code is the failure's type, and
            // only a source that may come back is retried.
            throw new ApplicationFailureException(refusal.Reason, errorType: refusal.Code, nonRetryable: !refusal.Retryable);
        }

        var record = AssertRecord(outcome);
        return new TakeTemplateVersionResult(
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
            outcome switch
            {
                ConflictLoggedVersion => TakeTemplateVersionOutcomes.ConflictLogged,
                TakenVersion => TakeTemplateVersionOutcomes.Taken,
                _ => TakeTemplateVersionOutcomes.AlreadyTaken,
            });
    }

    private static ReportService.TemplatePackages.Records.TemplateVersionRecord AssertRecord(TemplateTakeOutcome outcome) =>
        outcome.Record ?? throw new InvalidOperationException("the take answered a state this activity does not know");

    /// <summary>Says the activity is still working until it ends, so a slow source does not lose the attempt.</summary>
    private static async Task BeatAsync(CancellationToken cancellationToken)
    {
        if (!ActivityExecutionContext.HasCurrent)
        {
            return;
        }

        using var timer = new PeriodicTimer(TakeTemplateVersionActivityOptions.HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ActivityExecutionContext.Current.Heartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // The take has ended: there is nothing left to report.
        }
    }
}
