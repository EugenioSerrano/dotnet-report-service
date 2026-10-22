using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReportService.Workers.Pdf;

/// <summary>
/// Reports healthy once the start-up probe has launched Chromium, and
/// unhealthy while it is still starting or after it failed.
/// </summary>
public sealed class BrowserHealthCheck(BrowserProbe probe) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = probe.Result;
        var health = result switch
        {
            null => HealthCheckResult.Unhealthy("Starting"),
            { Succeeded: true } => HealthCheckResult.Healthy($"Chromium {result.ChromiumVersion}"),
            _ => HealthCheckResult.Unhealthy("Chromium could not be launched"),
        };
        return Task.FromResult(health);
    }
}
