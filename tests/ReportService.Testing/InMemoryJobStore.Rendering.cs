using ReportService.Jobs;
using ReportService.Uploads;

namespace ReportService.Testing;

/// <summary>
/// The partition, block-commit, publication and finalisation records of the in-memory
/// store, under the same lock and with the same semantics as the database: a record
/// written once is reported with what exists, and a commit is refused when the job is
/// ending or the output is no longer running. Because the shared contract runs against
/// both, the double cannot drift away from the store it stands in for.
/// </summary>
public sealed partial class InMemoryJobStore
{
    private readonly Dictionary<string, PartitionRecord> _partitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PartCommit> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArtifactRecord> _artifacts = new(StringComparer.Ordinal);

    /// <summary>The partition a job's data step recorded, when it did.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public PartitionRecord? PartitionOf(string reportJobId)
    {
        lock (_gate)
        {
            return _partitions.GetValueOrDefault(reportJobId);
        }
    }

    /// <summary>The artifact published for an output, when one was.</summary>
    /// <param name="outputId">The identifier of the output.</param>
    public ArtifactRecord? ArtifactOf(string outputId)
    {
        lock (_gate)
        {
            return _artifacts.GetValueOrDefault(outputId);
        }
    }

    /// <inheritdoc />
    public Task<TransitionOutcome> TransitionJobAsync(
        string reportJobId,
        IReadOnlyList<ReportJobStatus> from,
        ReportJobStatus to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(from);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job))
            {
                return Task.FromResult(TransitionOutcome.NotApplicable);
            }

            if (job.Status == to)
            {
                return Task.FromResult(TransitionOutcome.AlreadyApplied);
            }

            if (!from.Contains(job.Status))
            {
                return Task.FromResult(TransitionOutcome.NotApplicable);
            }

            _jobs[reportJobId] = job with
            {
                Status = to,
                StartedAt = job.StartedAt ?? now.ToUniversalTime(),
            };
            return Task.FromResult(TransitionOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<TransitionOutcome> TransitionOutputAsync(
        string outputId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);

        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var output))
            {
                return Task.FromResult(TransitionOutcome.NotApplicable);
            }

            if (output.Status == OutputStatus.Running)
            {
                return Task.FromResult(TransitionOutcome.AlreadyApplied);
            }

            if (output.Status != OutputStatus.Pending)
            {
                return Task.FromResult(TransitionOutcome.NotApplicable);
            }

            _outputs[outputId] = output with { Status = OutputStatus.Running, StartedAt = now.ToUniversalTime() };
            return Task.FromResult(TransitionOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<PartitionCommitResult> CommitPartitionAsync(
        PartitionRecord partition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partition);

        lock (_gate)
        {
            if (_partitions.TryGetValue(partition.ReportJobId, out var existing))
            {
                return Task.FromResult(new PartitionCommitResult(PartitionCommitOutcome.AlreadyCommitted, existing));
            }

            _partitions.Add(partition.ReportJobId, partition);
            return Task.FromResult(new PartitionCommitResult(PartitionCommitOutcome.Committed, partition));
        }
    }

    /// <inheritdoc />
    public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            return Task.FromResult(_partitions.GetValueOrDefault(reportJobId));
        }
    }

    /// <inheritdoc />
    public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (_gate)
        {
            if (!AllowsCommit(commit.OutputId))
            {
                return Task.FromResult(new PartCommitResult(PartCommitOutcome.GuardRefused, null));
            }

            var key = KeyOf(commit.OutputId, commit.BlockIndex);
            if (_parts.TryGetValue(key, out var existing))
            {
                return Task.FromResult(new PartCommitResult(PartCommitOutcome.AlreadyCommitted, existing));
            }

            _parts.Add(key, commit);
            return Task.FromResult(new PartCommitResult(PartCommitOutcome.Committed, commit));
        }
    }

    /// <inheritdoc />
    public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);

        lock (_gate)
        {
            return Task.FromResult(_parts.GetValueOrDefault(KeyOf(outputId, blockIndex)));
        }
    }

    /// <inheritdoc />
    public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        lock (_gate)
        {
            if (_artifacts.TryGetValue(artifact.OutputId, out var existing))
            {
                return Task.FromResult(new PublishResult(PublishOutcome.AlreadyPublished, existing.ArtifactId));
            }

            if (!AllowsCommit(artifact.OutputId))
            {
                return Task.FromResult(new PublishResult(PublishOutcome.GuardRefused, null));
            }

            _artifacts.Add(artifact.OutputId, artifact);
            var output = _outputs[artifact.OutputId];
            _outputs[artifact.OutputId] = output with
            {
                Status = OutputStatus.Succeeded,
                FinishedAt = artifact.CreatedAt.ToUniversalTime(),
            };
            return Task.FromResult(new PublishResult(PublishOutcome.Published, artifact.ArtifactId));
        }
    }

    /// <inheritdoc />
    public Task<OutputFailureOutcome> FailOutputAsync(
        string outputId,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);
        ArgumentNullException.ThrowIfNull(code);

        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var output)
                || output.Status is not (OutputStatus.Pending or OutputStatus.Running))
            {
                return Task.FromResult(OutputFailureOutcome.AlreadyTerminal);
            }

            _outputs[outputId] = output with
            {
                Status = OutputStatus.Failed,
                ErrorCode = code,
                FinishedAt = now.ToUniversalTime(),
            };
            return Task.FromResult(OutputFailureOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public Task<JobFinalizationResult> FinalizeJobAsync(
        string reportJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.IsTerminal)
            {
                return Task.FromResult(new JobFinalizationResult(JobFinalizationOutcome.AlreadyTerminal, null));
            }

            var outputs = _outputs.Values
                .Where(output => string.Equals(output.ReportJobId, reportJobId, StringComparison.Ordinal))
                .OrderBy(output => output.OutputId, StringComparer.Ordinal)
                .ToList();
            var failed = outputs.FirstOrDefault(output => output.Status == OutputStatus.Failed);
            var allSucceeded = outputs.Count > 0 && outputs.All(output => output.Status == OutputStatus.Succeeded);
            if (failed is null && !allSucceeded)
            {
                return Task.FromResult(new JobFinalizationResult(JobFinalizationOutcome.AlreadyTerminal, null));
            }

            var status = failed is null ? ReportJobStatus.Succeeded : ReportJobStatus.Failed;
            _jobs[reportJobId] = job with
            {
                Status = status,
                ErrorCode = failed?.ErrorCode,
                ErrorReason = failed is null ? null : "output-failed",
                FinishedAt = now.ToUniversalTime(),
            };
            return Task.FromResult(new JobFinalizationResult(JobFinalizationOutcome.Applied, status));
        }
    }

    private bool AllowsCommit(string outputId)
    {
        if (!_outputs.TryGetValue(outputId, out var output))
        {
            return false;
        }

        var job = _jobs.GetValueOrDefault(output.ReportJobId);
        return job is not null
            && job.Status is not (ReportJobStatus.Cancelling or ReportJobStatus.Cancelled)
            && output.Status == OutputStatus.Running;
    }

    private static string KeyOf(string outputId, int blockIndex) => $"{outputId}/{blockIndex}";
}
