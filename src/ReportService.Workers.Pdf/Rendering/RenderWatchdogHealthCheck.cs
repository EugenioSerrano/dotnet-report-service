using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// Reports the worker unhealthy from the instant the watchdog stopped a render until the
/// process ends, with the constant sentence a probe reads; healthy otherwise. It is served
/// by the worker's delivered <c>/health</c> surface and adds no route.
/// </summary>
/// <param name="watchdog">The worker's watchdog.</param>
internal sealed class RenderWatchdogHealthCheck(RenderWatchdog watchdog) : IHealthCheck
{
    /// <summary>The description an unhealthy worker reports; it carries no value and no identifier.</summary>
    public const string UnhealthyDescription = "A render passed its deadline; this worker is stopping";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(watchdog.IsUnhealthy
            ? HealthCheckResult.Unhealthy(UnhealthyDescription)
            : HealthCheckResult.Healthy());
    }
}
