using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReportService.TemplatePackages.Records;
using ReportService.TemplatePackages.Versioning;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// Chooses the version a by-name reference uses: the version folders of the
/// template at one commit plus the versions already taken, release versions
/// only, newest first by semantic-version precedence. Each candidate is taken
/// newest first through the take seam; what the take refuses is logged with what
/// to fix and skipped, so a broken newest version never fails the jobs that
/// name the template only. A resolution writes nothing: the take owns what it
/// records, and this class only reads the store and the source.
/// </summary>
public sealed partial class TemplateVersionResolver
{
    /// <summary>The requested form every resolution of this class answers for.</summary>
    public const string RequestedForm = "BY_NAME";

    /// <summary>The constant answer of a template this consumer may not see.</summary>
    public const string NotFoundReason = "The template was not found.";

    /// <summary>The answer of a source that could not be checked and a template with no release version already taken.</summary>
    public const string SourceUnavailableReason = "The template source could not be checked; try again later.";

    private readonly ITemplateSource _source;
    private readonly ITemplateVersionStore _store;
    private readonly ITemplateTake _take;
    private readonly TemplateByNameMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly TimeSpan _resolveDeadline;

    /// <summary>Creates a resolver over the source, the store and the take seam.</summary>
    /// <param name="source">The configured template source.</param>
    /// <param name="store">The taken versions.</param>
    /// <param name="take">The take seam a candidate is taken through.</param>
    /// <param name="metrics">The instruments of the by-name path.</param>
    /// <param name="timeProvider">The clock the deadline and the duration are read from.</param>
    /// <param name="logger">The log the resolution writes to.</param>
    /// <param name="resolveDeadline">How long the whole resolution may take.</param>
    public TemplateVersionResolver(
        ITemplateSource source,
        ITemplateVersionStore store,
        ITemplateTake take,
        TemplateByNameMetrics metrics,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan resolveDeadline)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(take);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (resolveDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resolveDeadline), "the resolution deadline must be positive");
        }

        _source = source;
        _store = store;
        _take = take;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _resolveDeadline = resolveDeadline;
    }

    /// <summary>Resolves the version a by-name reference uses.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="requestingConsumerId">The consumer the job belongs to.</param>
    /// <param name="reportJobId">The job on whose behalf the resolution runs, when there is one.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome and the trace the log and the metrics read.</returns>
    public async Task<(TemplateResolutionOutcome Outcome, TemplateResolutionTrace Trace)> ResolveAsync(
        string templateId,
        string requestingConsumerId,
        string? reportJobId,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();

        if (templateId is null
            || !TemplateIdPattern().IsMatch(templateId)
            || string.IsNullOrEmpty(requestingConsumerId))
        {
            // Acceptance refuses such a reference already; answering it here
            // costs no store call and no source call.
            return Finish(
                templateId ?? string.Empty,
                TemplateResolutionOutcome.Refused(TemplateResolutionCodes.TemplateNotFound, NotFoundReason, retryable: false),
                commit: null,
                sourceVersions: 0,
                takenVersions: 0,
                considered: [],
                skipped: [],
                examined: 0,
                sourceCut: false,
                deadlineReached: false,
                reportJobId,
                startedAt);
        }

        using var deadline = new CancellationTokenSource();
        using var timer = _timeProvider.CreateTimer(
            state => ((CancellationTokenSource)state!).Cancel(),
            deadline,
            _resolveDeadline,
            Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;

        SourceCommit? commit = null;
        SourceTemplateVersions? listing = null;
        TemplateSourceException? sourceFailure = null;
        var deadlineReached = false;

        try
        {
            commit = await _source.ResolveBranchAsync(token).ConfigureAwait(false);
            listing = await _source.ListVersionsAsync(commit, templateId, token).ConfigureAwait(false);
        }
        catch (TemplateSourceException exception) when (exception.Failure == TemplateSourceFailure.PackageTooLarge)
        {
            // The source answered, so the candidate set would simply be
            // incomplete: the template is refused, never the outage fallback.
            return Finish(
                templateId,
                TemplateResolutionOutcome.Refused(
                    TemplateResolutionCodes.TemplateInvalid,
                    $"The template folder has too many entries to list; reduce the number of version folders under templates/{templateId}/.",
                    retryable: false),
                commit,
                sourceVersions: 0,
                takenVersions: 0,
                considered: [],
                skipped: [],
                examined: 0,
                sourceCut: false,
                deadlineReached: false,
                reportJobId,
                startedAt);
        }
        catch (TemplateSourceException exception)
        {
            sourceFailure = exception;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            deadlineReached = true;
        }

        // The store is read even when the source could not be checked: it is
        // what an outage falls back to. The caller's token, not the deadline,
        // because the deadline bounds the source, not this process's database.
        var taken = await _store.ListByTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);

        if (sourceFailure is not null || deadlineReached)
        {
            return Unchecked(
                templateId,
                requestingConsumerId,
                commit,
                listing,
                taken,
                TemplateVersionCandidates.Build([], taken, requestingConsumerId),
                examined: 0,
                sourceFailure,
                deadlineReached,
                reportJobId,
                startedAt);
        }

        var candidates = TemplateVersionCandidates.Build(listing?.Names ?? [], taken, requestingConsumerId);
        if (listing is null && taken.Count == 0)
        {
            // The template folder does not exist and no version of it was ever
            // taken: absent, which is not the same as invalid.
            return Finish(
                templateId,
                TemplateResolutionOutcome.Refused(TemplateResolutionCodes.TemplateNotFound, NotFoundReason, retryable: false),
                commit,
                sourceVersions: 0,
                takenVersions: 0,
                candidates.Considered.Select(candidate => candidate.Version).ToArray(),
                candidates.Skipped,
                examined: 0,
                sourceCut: false,
                deadlineReached: false,
                reportJobId,
                startedAt);
        }

        var walkSkipped = new List<CandidateSkip>();
        TemplateResolutionOutcome? outcome = null;
        var examined = 0;

        foreach (var candidate in candidates.Considered)
        {
            if (_timeProvider.GetUtcNow() - startedAt >= _resolveDeadline)
            {
                deadlineReached = true;
                break;
            }

            examined++;

            if (candidate.Taken is { } record)
            {
                // Taken once: the record is the resolution, and the source is
                // not read for it.
                outcome = TemplateResolutionOutcome.Resolved(record, fromTakenCopy: false);
                break;
            }

            TakeAttempt attempt;
            try
            {
                attempt = await _take
                    .TakeAsync(templateId, candidate.Version, requestingConsumerId, commit, reportJobId, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                deadlineReached = true;
                break;
            }

            switch (attempt)
            {
                case TakenAttempt takenAttempt:
                    outcome = TemplateResolutionOutcome.Resolved(takenAttempt.Record, fromTakenCopy: false);
                    break;
                case RefusedAttempt refusal when refusal.Code is TemplateCheckCodes.TemplateInvalid or TemplateCheckCodes.UnsupportedCapability:
                    walkSkipped.Add(new CandidateSkip(candidate.Version, CandidateSkipReasons.Refused, refusal.Code, refusal.Reason));
                    continue;
                case RefusedAttempt refusal when refusal.Code == TemplateResolutionCodes.TemplateNotFound:
                    // At the resolution's commit a listed folder cannot be
                    // absent, so this is the take's foreign-owner answer.
                    walkSkipped.Add(new CandidateSkip(candidate.Version, CandidateSkipReasons.NotVisible, null, null));
                    continue;
                case RefusedAttempt refusal when refusal.Code == TemplateResolutionCodes.TemplateSourceUnavailable:
                    sourceFailure = new TemplateSourceException(TemplateSourceFailure.Unreachable, refusal.Reason);
                    break;
                default:
                    throw new InvalidOperationException("the take seam answered a refusal this resolution does not know");
            }

            break;
        }

        var skipped = new List<CandidateSkip>(candidates.Skipped);
        skipped.AddRange(walkSkipped);
        var considered = candidates.Considered.Select(candidate => candidate.Version).ToArray();

        if (outcome is not null)
        {
            return Finish(
                templateId, outcome, commit, listing!.Names.Count, taken.Count,
                considered, skipped, examined, sourceCut: false, deadlineReached: false, reportJobId, startedAt);
        }

        if (sourceFailure is not null || deadlineReached)
        {
            return Unchecked(
                templateId, requestingConsumerId, commit, listing, taken,
                candidates, examined, sourceFailure, deadlineReached, reportJobId, startedAt, skipped);
        }

        return Finish(
            templateId,
            NothingUsable(templateId, skipped),
            commit,
            listing!.Names.Count,
            taken.Count,
            considered,
            skipped,
            examined,
            sourceCut: false,
            deadlineReached: false,
            reportJobId,
            startedAt);
    }

    private static TemplateResolutionOutcome NothingUsable(
        string templateId,
        IReadOnlyList<CandidateSkip> skipped)
    {
        var refused = skipped.FirstOrDefault(skip => skip.Reason == CandidateSkipReasons.Refused);
        if (refused is not null)
        {
            return TemplateResolutionOutcome.Refused(
                TemplateResolutionCodes.TemplateInvalid,
                $"No usable version of template {templateId} was found: version {refused.Version} was refused. {refused.RefusalReason}",
                retryable: false);
        }

        var anyNotVisible = skipped.Any(skip => skip.Reason == CandidateSkipReasons.NotVisible);
        if (anyNotVisible)
        {
            // Every excluded candidate was one this consumer may not use, or a
            // foreign release sits beside a pre-release or a non-version name:
            // both answer the constant not-found text and name nothing.
            return TemplateResolutionOutcome.Refused(TemplateResolutionCodes.TemplateNotFound, NotFoundReason, retryable: false);
        }

        // The folder exists but holds no release candidate (only pre-releases,
        // only non-version names, or nothing), or it is gone while every taken
        // version is excluded: the author publishes a release version. The
        // newest version found is named, so the author knows where to start.
        var reason = $"Template {templateId} has no release version; publish a release version (a pre-release is used only when a job names it exactly).";
        var newest = skipped
            .Select(skip => (skip.Version, Parsed: SemanticVersion.TryParse(skip.Version, out var parsed) ? parsed : null))
            .Where(candidate => candidate.Parsed is not null)
            .OrderByDescending(candidate => candidate.Parsed!, Comparer<SemanticVersion?>.Default)
            .ThenByDescending(candidate => candidate.Version, StringComparer.Ordinal)
            .Select(candidate => candidate.Version)
            .FirstOrDefault();
        if (newest is not null)
        {
            reason += $" The newest version found is {newest}.";
        }

        return TemplateResolutionOutcome.Refused(
            TemplateResolutionCodes.TemplateInvalid,
            reason,
            retryable: false);
    }

    private (TemplateResolutionOutcome Outcome, TemplateResolutionTrace Trace) Unchecked(
        string templateId,
        string requestingConsumerId,
        SourceCommit? commit,
        SourceTemplateVersions? listing,
        IReadOnlyList<TemplateVersionRecord> taken,
        TemplateVersionCandidates candidates,
        int examined,
        TemplateSourceException? sourceFailure,
        bool deadlineReached,
        string? reportJobId,
        DateTimeOffset startedAt,
        IReadOnlyList<CandidateSkip>? skipped = null)
    {
        var fallback = taken
            .Where(record => TemplateVersionVisibility.MayUse(record, requestingConsumerId))
            .Select(record => (Record: record, Parsed: SemanticVersion.TryParse(record.Version, out var parsed) ? parsed : null))
            .Where(candidate => candidate.Parsed is not null && candidate.Parsed.PreRelease.Count == 0)
            .OrderByDescending(candidate => candidate.Parsed!, Comparer<SemanticVersion?>.Default)
            .ThenByDescending(candidate => candidate.Record.Version, StringComparer.Ordinal)
            .FirstOrDefault();

        var outcome = fallback.Record is not null
            ? TemplateResolutionOutcome.Resolved(fallback.Record, fromTakenCopy: true)
            : TemplateResolutionOutcome.Refused(TemplateResolutionCodes.TemplateSourceUnavailable, SourceUnavailableReason, retryable: true);

        return Finish(
            templateId,
            outcome,
            commit,
            sourceVersions: listing?.Names.Count ?? 0,
            takenVersions: taken.Count,
            considered: candidates.Considered.Select(candidate => candidate.Version).ToArray(),
            skipped: skipped ?? [],
            examined,
            sourceCut: true,
            deadlineReached,
            reportJobId,
            startedAt,
            sourceFailure);
    }

    private (TemplateResolutionOutcome Outcome, TemplateResolutionTrace Trace) Finish(
        string templateId,
        TemplateResolutionOutcome outcome,
        SourceCommit? commit,
        int sourceVersions,
        int takenVersions,
        IReadOnlyList<string> considered,
        IReadOnlyList<CandidateSkip> skipped,
        int examined,
        bool sourceCut,
        bool deadlineReached,
        string? reportJobId,
        DateTimeOffset startedAt,
        TemplateSourceException? sourceFailure = null)
    {
        var trace = new TemplateResolutionTrace(
            templateId,
            commit?.CommitSha,
            sourceVersions,
            takenVersions,
            considered,
            skipped,
            examined,
            sourceCut,
            deadlineReached);

        var outcomeName = outcome switch
        {
            ResolvedOutcome { FromTakenCopy: false } => "resolved",
            ResolvedOutcome => "resolved_from_taken_copy",
            _ => "refused",
        };
        var code = outcome is RefusedOutcome refused ? refused.Code : "none";
        var answeredVersion = outcome is ResolvedOutcome resolved ? resolved.Record.Version : "(none)";
        var durationMilliseconds = (long)(_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds;

        _logger.LogInformation(
            new EventId(EventIds.ByName, "template.by_name"),
            "template.by_name template {TemplateId} requested {RequestedForm}, commit {CommitSha}, source versions {SourceVersions}, taken versions {TakenVersions}, considered {Considered}, examined {Examined}, outcome {Outcome}, code {Code}, duration {DurationMs} ms, job {ReportJobId}",
            templateId,
            RequestedForm,
            commit?.CommitSha ?? "(none)",
            sourceVersions,
            takenVersions,
            considered.Count,
            examined,
            outcomeName,
            code,
            durationMilliseconds,
            reportJobId ?? "(none)");

        foreach (var skip in skipped)
        {
            if (skip.Reason == CandidateSkipReasons.Refused)
            {
                _logger.LogWarning(
                    new EventId(EventIds.Skipped, "template.by_name.skipped"),
                    "template.by_name.skipped template {TemplateId} version {Version} reason {Reason}, code {Code}: {RefusalReason}",
                    templateId,
                    skip.Version,
                    skip.Reason,
                    skip.Code ?? "(none)",
                    skip.RefusalReason ?? "(none)");
            }
            else
            {
                // A version this consumer may not use carries no code and no
                // refusal text; no skip record carries a file or a field.
                _logger.LogInformation(
                    new EventId(EventIds.Skipped, "template.by_name.skipped"),
                    "template.by_name.skipped template {TemplateId} version {Version} reason {Reason}",
                    templateId,
                    skip.Version,
                    skip.Reason);
            }
        }

        if (sourceCut)
        {
            var kind = sourceFailure is null
                ? "deadline"
                : sourceFailure.Failure switch
                {
                    TemplateSourceFailure.Misconfigured => "misconfigured",
                    TemplateSourceFailure.PackageTooLarge => "unreachable",
                    _ => "unreachable",
                };
            _logger.LogWarning(
                new EventId(EventIds.Unchecked, "template.by_name.unchecked"),
                "template.by_name.unchecked template {TemplateId}, the template source could not be checked, kind {Kind}, commit {CommitSha}, examined {Examined}, answered version {Version}",
                templateId,
                kind,
                commit?.CommitSha ?? "(none)",
                examined,
                answeredVersion);
        }

        _metrics.Resolution(outcomeName, code);
        _metrics.Candidates(considered.Count, examined);

        return (outcome, trace);
    }

    [GeneratedRegex(@"\A[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdPattern();

    /// <summary>The identifiers of this resolver's log records.</summary>
    public static class EventIds
    {
        /// <summary>One record per resolution, on every path.</summary>
        public const int ByName = 4100;

        /// <summary>One record per skipped candidate.</summary>
        public const int Skipped = 4101;

        /// <summary>The source could not be checked.</summary>
        public const int Unchecked = 4102;
    }
}
