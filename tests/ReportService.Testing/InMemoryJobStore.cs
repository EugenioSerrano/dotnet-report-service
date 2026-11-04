using ReportService.Jobs;
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
    private readonly List<RecordedJobIntent> _intents = [];
    private int _acceptCalls;
    private int _findCalls;
    private int _failCalls;
    private int _findStatusCalls;
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

    /// <summary>How many times <see cref="AcceptAsync"/> was called.</summary>
    public int AcceptCalls => Volatile.Read(ref _acceptCalls);

    /// <summary>How many times <see cref="FindForWorkflowAsync"/> was called.</summary>
    public int FindCalls => Volatile.Read(ref _findCalls);

    /// <summary>How many times <see cref="FailJobAsync"/> was called.</summary>
    public int FailCalls => Volatile.Read(ref _failCalls);

    /// <summary>How many times <see cref="FindStatusAsync"/> was called.</summary>
    public int FindStatusCalls => Volatile.Read(ref _findStatusCalls);

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
    public async Task<JobAcceptanceResult> AcceptAsync(
        JobAcceptance acceptance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        Interlocked.Increment(ref _acceptCalls);

        // The upload is read the way the service reads it — scoped by its owner — and the
        // decision is the library's, so the double and the real store answer alike.
        var upload = await _uploads.FindAsync(acceptance.UploadId, acceptance.ConsumerId, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
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
                instant);
            _jobs.Add(job.ReportJobId, job);
            _progress.Add(job.ReportJobId, JobProgress.Empty);
            _outputs.Add(acceptance.OutputId, new ReportOutputRecord(
                acceptance.OutputId, job.ReportJobId, OutputFormat.Pdf, OutputStatus.Pending));
            _references.Add(job.ReportJobId, new TemplateReferenceRecord(
                job.ReportJobId, acceptance.TemplateId, acceptance.ReferenceForm)
            {
                RequestedVersion = acceptance.RequestedVersion,
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
        CancellationToken cancellationToken)
    {
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
                    FinishedAt = now,
                };
            }

            _jobs[reportJobId] = job with
            {
                Status = ReportJobStatus.Failed,
                ErrorCode = code,
                ErrorReason = reason,
                FinishedAt = now,
            };
            return Task.FromResult(JobFailureOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken)
    {
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
