using ReportService.Jobs;

namespace ReportService.Testing;

/// <summary>The job's effective class, read and moved as the database store does.</summary>
public sealed partial class InMemoryJobStore : IJobPriorityStore
{
    private int _promotions;

    /// <summary>How many calls moved a job from low to normal.</summary>
    public int Promotions => Volatile.Read(ref _promotions);

    /// <inheritdoc />
    public Task<JobPriority?> ReadEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        lock (_gate)
        {
            return Task.FromResult(_jobs.TryGetValue(reportJobId, out var job) ? job.EffectivePriorityClass : (JobPriority?)null);
        }
    }

    /// <inheritdoc />
    public Task<bool> PromoteEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.EffectivePriorityClass != JobPriority.Low)
            {
                return Task.FromResult(false);
            }

            _jobs[reportJobId] = job with { EffectivePriorityClass = JobPriority.Normal };
            Interlocked.Increment(ref _promotions);
            return Task.FromResult(true);
        }
    }

    /// <summary>Sets a seeded job's effective class, as an acceptance with that class would.</summary>
    /// <param name="reportJobId">The job.</param>
    /// <param name="priority">The class.</param>
    public void PlaceInClass(string reportJobId, JobPriority priority)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        lock (_gate)
        {
            if (_jobs.TryGetValue(reportJobId, out var job))
            {
                _jobs[reportJobId] = job with { EffectivePriorityClass = priority, DeclaredPriority = priority };
            }
        }
    }
}
