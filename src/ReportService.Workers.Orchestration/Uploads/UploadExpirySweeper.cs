using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The periodic sweep that makes an upload's stored state catch up with the truth
/// without waiting for a request: every interval it expires the uploads that are
/// past the end of their session or past the verification deadline of their
/// confirmation, and deletes what they were still holding. It expires in batches,
/// so a backlog never runs as one unbounded statement, and its update skips the
/// rows another replica holds, so several of them — and the API's own expiry — can
/// run at once and still expire an upload exactly once. A failure of the database
/// or of the storage is logged and the sweep waits for its next round; it never
/// stops the process and never blocks an upload from being expired later.
/// </summary>
public sealed class UploadExpirySweeper : BackgroundService
{
    /// <summary>The name of the span written for one sweep round.</summary>
    public const string SweepActivityName = "upload.expiry_sweep";

    /// <summary>The tag a sweep round carries the number of uploads it expired on.</summary>
    public const string ExpiredCountTag = "upload.expiry_sweep.expired";

    private static readonly ActivitySource Source = new(TelemetryDefaults.ActivitySourceName);

    private readonly IUploadStore _uploads;
    private readonly IObjectStore _objects;
    private readonly UploadExpiryOptions _options;
    private readonly TimeProvider _clock;
    private readonly OrchestrationMetrics _metrics;
    private readonly ILogger<UploadExpirySweeper> _logger;

    /// <summary>Creates the sweep over the stores, its settings and the process's clock.</summary>
    /// <param name="uploads">The upload records.</param>
    /// <param name="objects">The object storage port, for the staging object of an expired upload.</param>
    /// <param name="options">The interval and the batch size, as the process validated them.</param>
    /// <param name="clock">The clock the sweep reads the instant from and waits on.</param>
    /// <param name="metrics">The process's instruments.</param>
    /// <param name="logger">The log the sweep writes to, with identifiers and causes only.</param>
    public UploadExpirySweeper(
        IUploadStore uploads,
        IObjectStore objects,
        IOptions<UploadExpiryOptions> options,
        TimeProvider clock,
        OrchestrationMetrics metrics,
        ILogger<UploadExpirySweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _uploads = uploads;
        _objects = objects;
        _options = options.Value;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.SweepInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The process is stopping while a round runs: a round that was cut off
                // is not a failure, and the host is waiting for this service to end.
                return;
            }
        }
    }

    /// <summary>
    /// Runs one sweep: as many rounds as it takes until a round finds less than a
    /// batch, each round deciding at one instant. Returns how many uploads it
    /// expired, which is zero when the database or the storage refused the round.
    /// </summary>
    /// <param name="cancellationToken">The process's stopping token.</param>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity(SweepActivityName);
        var expired = 0;
        try
        {
            while (true)
            {
                var now = UploadWireValues.TruncateToMillisecond(_clock.GetUtcNow());
                var batch = await _uploads.ExpireOverdueAsync(now, _options.BatchSize, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var upload in batch)
                {
                    await ReleaseAsync(upload, cancellationToken).ConfigureAwait(false);
                }

                expired += batch.Count;
                if (batch.Count < _options.BatchSize)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The next interval tries again; one failed round never ends the process.
            _logger.LogWarning(
                "An expiry sweep round could not finish ({FailureType}); the next round tries again",
                exception.GetType().Name);
        }

        activity?.SetTag(ExpiredCountTag, expired);
        return expired;
    }

    // What an expired upload no longer needs: its staging object, one log line and one count.
    private async Task ReleaseAsync(ExpiredUpload upload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Upload {UploadId} expired ({ExpiryCause})", upload.UploadId, upload.Cause);
        _metrics.UploadOutcome(OrchestrationMetrics.ExpiredOutcome, OrchestrationMetrics.NoCode, upload.Cause);
        try
        {
            var deleted = await _objects.DeleteAsync(upload.StagingKey, cancellationToken).ConfigureAwait(false);
            if (!deleted.Succeeded && deleted.Outcome != StorageOutcome.NotFound)
            {
                _logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", deleted.Outcome.ToString());
            }
        }
        catch (StorageException exception)
        {
            // The upload has already expired; what is left behind is the later cleanup's.
            _logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", exception.Outcome.ToString());
        }
    }
}
