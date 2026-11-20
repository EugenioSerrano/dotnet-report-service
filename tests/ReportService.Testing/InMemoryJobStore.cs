using ReportService.Jobs;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Testing;

/// <summary>
/// The report job record store the tests use in place of the database. It answers
/// the same contract as the real store — an acceptance that stores the job, its
/// output, its template reference and its start request together or stores nothing,
/// a refusal of every upload that is not usable or no longer is, and a failure that
/// ends the job and whatever output of it had not ended — and it records what it
/// received, so a test can assert that a refused request wrote nothing and that a
/// repeated step changed nothing. The shared contract runs against this class and
/// against the real store, so the double cannot drift away from what it stands in for.
/// </summary>
public sealed partial class InMemoryJobStore : IJobStore
{
    /// <summary>The kind of request an acceptance records for the job's start.</summary>
    public const string StartJobKind = "start-job";

    private readonly Lock _gate = new();
    private readonly IUploadStore _uploads;
    private readonly Dictionary<string, ReportJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportOutputRecord> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TemplateReferenceRecord> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ConsumerId, string Key), BoundRequest> _idempotency = [];
    private readonly Dictionary<string, RequestFingerprint> _fingerprints = new(StringComparer.Ordinal);
    private readonly List<RecordedJobIntent> _intents = [];
    private int _acceptCalls;
    private int _findCalls;
    private int _failCalls;
    private int _findStatusCalls;
    private int _listArtifactCalls;
    private int _findArtifactCalls;
    private int _writeCalls;
    private int _progressWriteCalls;

    /// <summary>Creates the store over the upload records it checks an acceptance against.</summary>
    /// <param name="uploads">The upload records, as the service holds them.</param>
    public InMemoryJobStore(IUploadStore uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        _uploads = uploads;
    }

    /// <summary>A start request the store recorded when a job was accepted.</summary>
    /// <param name="Kind">The kind of request.</param>
    /// <param name="ReportJobId">The job the request names.</param>
    /// <param name="RecordedAt">When the request was recorded, in UTC.</param>
    public sealed record RecordedJobIntent(string Kind, string ReportJobId, DateTimeOffset RecordedAt);

    /// <summary>
    /// The job a key of one consumer is bound to, with the identity of the request that bound
    /// it: the pair a real store keeps in the job's row and its unique index.
    /// </summary>
    /// <param name="ReportJobId">The first job's identifier.</param>
    /// <param name="Fingerprint">The identity of the request that bound the key.</param>
    public sealed record BoundRequest(string ReportJobId, RequestFingerprint Fingerprint);

    /// <summary>How many times <see cref="AcceptAsync"/> was called.</summary>
    public int AcceptCalls => Volatile.Read(ref _acceptCalls);

    /// <summary>How many times <see cref="FindForWorkflowAsync"/> was called.</summary>
    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>How many times <see cref="FailJobAsync"/> was called.</summary>
    public int FailCalls => Volatile.Read(ref _failCalls);

    /// <summary>How many times <see cref="FindStatusAsync"/> was called.</summary>
    public int FindStatusCalls => Volatile.Read(ref _findStatusCalls);

    /// <summary>How many times <see cref="ListArtifactsAsync"/> was called.</summary>
    public int ListArtifactCalls => Volatile.Read(ref _listArtifactCalls);

    /// <summary>How many times <see cref="FindArtifactAsync"/> was called.</summary>
    public int FindArtifactCalls => Volatile.Read(ref _findArtifactCalls);

    /// <summary>
    /// How many calls were made to a method that could change a record of a job, of its
    /// outputs or of its artifacts. A read never counts, so a case can prove that an answer
    /// wrote nothing at all.
    /// </summary>
    public int WriteCalls => Volatile.Read(ref _writeCalls);

    /// <summary>How many times <see cref="RecordRecordsReadAsync"/> was called.</summary>
    public int ProgressWriteCalls => Volatile.Read(ref _progressWriteCalls);

    /// <summary>When set, every progress write throws it; used to prove a failing write never fails a step.</summary>
    public Exception? ProgressWriteFailure { get; set; }

    /// <summary>The jobs the store holds, in the order they were accepted.</summary>
    public IReadOnlyList<ReportJobRecord> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Values];
            }
        }
    }

    /// <summary>The start requests the store recorded, in the order they were recorded.</summary>
    public IReadOnlyList<RecordedJobIntent> Intents
    {
        get
        {
            lock (_gate)
            {
                return [.. _intents];
            }
        }
    }

    /// <summary>The template reference a job was accepted with, when it was accepted.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public TemplateReferenceRecord? ReferenceOf(string reportJobId)
    {
        lock (_gate)
        {
            return _references.GetValueOrDefault(reportJobId);
        }
    }

    /// <summary>The identity of the request that created a job, exactly as the store recorded it.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public RequestFingerprint? FingerprintOf(string reportJobId)
    {
        lock (_gate)
        {
            return _fingerprints.TryGetValue(reportJobId, out var fingerprint) ? fingerprint : null;
        }
    }

    /// <summary>The job a key of one consumer is bound to, or null when the key is unused.</summary>
    /// <param name="consumerId">The consumer the key belongs to.</param>
    /// <param name="key">The key exactly as it was sent.</param>
    public BoundRequest? BoundRequestOf(string consumerId, string key)
    {
        lock (_gate)
        {
            return _idempotency.GetValueOrDefault((consumerId, key));
        }
    }

    /// <summary>
    /// Records the baseline of an accepted reference, exactly as an acceptance that chose one
    /// would have written it. It exists for the cases of a by-name job whose source is
    /// unreachable: the baseline is decided by the request, long before the step runs.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="baselineVersion">The baseline the acceptance recorded, or null for none.</param>
    public void SetBaseline(string reportJobId, string? baselineVersion)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            if (_references.TryGetValue(reportJobId, out var reference))
            {
                _references[reportJobId] = reference with { BaselineVersion = baselineVersion };
            }
        }
    }

    /// <summary>The outputs of a job, in output identifier order.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public IReadOnlyList<ReportOutputRecord> OutputsOf(string reportJobId)
    {
        lock (_gate)
        {
            return [.. _outputs.Values
                .Where(output => string.Equals(output.ReportJobId, reportJobId, StringComparison.Ordinal))
                .OrderBy(output => output.OutputId, StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
        string consumerId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint.Value);

        lock (_gate)
        {
            return Task.FromResult<IdempotencyLookup>(
                _idempotency.TryGetValue((consumerId, idempotencyKey), out var bound)
                    ? bound.Fingerprint == fingerprint
                        ? new IdempotencyLookup.SameRequest(bound.ReportJobId)
                        : new IdempotencyLookup.DifferentRequest()
                    : new IdempotencyLookup.None());
        }
    }

    /// <inheritdoc />
    public async Task<JobAcceptanceResult> AcceptAsync(
        JobAcceptance acceptance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCalls);
        ArgumentNullException.ThrowIfNull(acceptance);
        Interlocked.Increment(ref _acceptCalls);

        // The upload is read the way the service reads it — scoped by its owner — and the
        // decision is the library's, so the double and the real store answer alike.
        var upload = await _uploads.FindAsync(acceptance.UploadId, acceptance.ConsumerId, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            // The key is answered before the upload is looked at, exactly as the real store's
            // unique index decides the race before its upload check: a repeat of an accepted
            // request keeps answering its first job whatever the upload has become since.
            if (acceptance.IdempotencyKey is { } key
                && _idempotency.TryGetValue((acceptance.ConsumerId, key), out var bound))
            {
                return bound.Fingerprint == acceptance.Fingerprint
                    ? new JobAcceptanceResult.SameRequest(bound.ReportJobId)
                    : new JobAcceptanceResult.DifferentRequest();
            }

            if (upload is null
                || upload.Status is not (UploadStatus.Ready or UploadStatus.Verifying)
                || UploadExpiry.IsOverdue(upload, now))
            {
                return new JobAcceptanceResult.UploadUnusable();
            }

            if (_jobs.ContainsKey(acceptance.ReportJobId))
            {
                throw new InvalidOperationException("the job identifier is already in use");
            }

            var instant = now.ToUniversalTime();
            var job = new ReportJobRecord(
                acceptance.ReportJobId,
                acceptance.ConsumerId,
                acceptance.UploadId,
                ReportJobStatus.Queued,
                instant,
                instant)
            {
                Culture = acceptance.Culture,
                TimeZone = acceptance.TimeZone,
            };
            _jobs.Add(job.ReportJobId, job);
            _progress.Add(job.ReportJobId, JobProgress.Empty);
            _fingerprints.Add(job.ReportJobId, acceptance.Fingerprint);
            if (acceptance.IdempotencyKey is { } boundKey)
            {
                _idempotency.Add((acceptance.ConsumerId, boundKey), new BoundRequest(job.ReportJobId, acceptance.Fingerprint));
            }
            _outputs.Add(acceptance.OutputId, new ReportOutputRecord(
                acceptance.OutputId, job.ReportJobId, OutputFormat.Pdf, OutputStatus.Pending));
            _references.Add(job.ReportJobId, new TemplateReferenceRecord(
                job.ReportJobId, acceptance.TemplateId, acceptance.ReferenceForm)
            {
                RequestedVersion = acceptance.RequestedVersion,
                BaselineVersion = acceptance.BaselineVersion,
                Culture = acceptance.Culture,
                TimeZone = acceptance.TimeZone,
            });
            _intents.Add(new RecordedJobIntent(StartJobKind, job.ReportJobId, instant));
            return new JobAcceptanceResult.Accepted(job);
        }
    }

    /// <inheritdoc />
    public async Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        Interlocked.Increment(ref _findCalls);

        ReportJobRecord? job;
        ReportOutputRecord[] outputs;
        lock (_gate)
        {
            job = _jobs.GetValueOrDefault(reportJobId);
            outputs = job is null
                ? []
                : [.. _outputs.Values
                    .Where(output => string.Equals(output.ReportJobId, reportJobId, StringComparison.Ordinal))
                    .OrderBy(output => output.OutputId, StringComparer.Ordinal)];
        }

        if (job is null)
        {
            return null;
        }

        var upload = await _uploads.LoadForVerificationAsync(job.UploadId, cancellationToken).ConfigureAwait(false);
        return new JobStateRecord(
            job,
            outputs,
            upload?.Status ?? UploadStatus.Expired,
            upload?.ExpiresAt ?? job.CreatedAt,
            upload?.VerificationDeadline,
            upload?.RejectionCode,
            upload?.RejectionReason);
    }

    /// <inheritdoc />
    public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            return Task.FromResult(_references.GetValueOrDefault(reportJobId));
        }
    }

    /// <inheritdoc />
    public Task<JobFailureOutcome> FailJobAsync(
        string reportJobId,
        string code,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        FailJobAsync(reportJobId, code, reason, null, now, cancellationToken);

    /// <inheritdoc />
    public Task<JobFailureOutcome> FailJobAsync(
        string reportJobId,
        string code,
        string reason,
        string? message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCalls);
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(reason);
        Interlocked.Increment(ref _failCalls);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.IsTerminal)
            {
                return Task.FromResult(JobFailureOutcome.AlreadyTerminal);
            }

            foreach (var output in _outputs.Values
                         .Where(output => string.Equals(output.ReportJobId, reportJobId, StringComparison.Ordinal)
                             && output.Status is OutputStatus.Pending or OutputStatus.Running)
                         .ToList())
            {
                _outputs[output.OutputId] = output with
                {
                    Status = OutputStatus.Failed,
                    ErrorCode = code,
                    ErrorReason = reason,
                    FinishedAt = now,
                };
            }

            _jobs[reportJobId] = job with
            {
                Status = ReportJobStatus.Failed,
                ErrorCode = code,
                ErrorReason = reason,
                ErrorMessage = message,
                FinishedAt = now,
            };
            return Task.FromResult(JobFailureOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<TemplatePinState> PinTemplateAsync(
        string reportJobId,
        TemplatePin pin,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        TemplateLocaleDefaults? presentation)
    {
        Interlocked.Increment(ref _writeCalls);
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(pin);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.IsTerminal)
            {
                return Task.FromResult(TemplatePinState.JobEnded());
            }

            if (!_references.TryGetValue(reportJobId, out var reference))
            {
                // A job without a reference is not a state the service can reach; the real
                // store's foreign key answers the same way, by writing nothing.
                return Task.FromResult(TemplatePinState.JobEnded());
            }

            if (reference.Pin is { } existing)
            {
                return Task.FromResult(TemplatePinState.AlreadyPinned(existing));
            }

            _references[reportJobId] = reference with { Pin = pin };

            // The job is fixed to the presentation its package declares, for the member the
            // request left open; a value already set is never replaced, as the database's
            // set-once guard refuses a change of it. The step's own read of the reference
            // answers the same two values, as the store's statement joins the job for them.
            var culture = job.Culture ?? presentation?.Culture;
            var timeZone = job.TimeZone ?? presentation?.TimeZone;
            _jobs[reportJobId] = job with { Culture = culture, TimeZone = timeZone };
            _references[reportJobId] = _references[reportJobId] with { Culture = culture, TimeZone = timeZone };
            return Task.FromResult(TemplatePinState.Pinned(pin));
        }
    }

    /// <inheritdoc />
    public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCalls);
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.Status != ReportJobStatus.Queued)
            {
                return Task.FromResult(BeginInputWaitOutcome.NotQueued);
            }

            _jobs[reportJobId] = job with { Stage = InputWaitStage.WaitingForInput };
            return Task.FromResult(BeginInputWaitOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writeCalls);
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job)
                || !string.Equals(job.Stage, InputWaitStage.WaitingForInput, StringComparison.Ordinal))
            {
                return Task.FromResult(EndInputWaitOutcome.NotWaiting);
            }

            _jobs[reportJobId] = job with { Stage = null };
            return Task.FromResult(EndInputWaitOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        lock (_gate)
        {
            // The same predicate and the same order as the statement the database runs: the
            // queued jobs of the upload, with the waiting stage or with none yet.
            IReadOnlyList<string> waiting =
            [
                .. _jobs.Values
                    .Where(job => string.Equals(job.UploadId, uploadId, StringComparison.Ordinal)
                        && job.Status == ReportJobStatus.Queued
                        && (job.Stage is null || string.Equals(job.Stage, InputWaitStage.WaitingForInput, StringComparison.Ordinal)))
                    .OrderBy(job => job.CreatedAt)
                    .ThenBy(job => job.ReportJobId, StringComparer.Ordinal)
                    .Select(job => job.ReportJobId),
            ];
            return Task.FromResult(waiting);
        }
    }
}
