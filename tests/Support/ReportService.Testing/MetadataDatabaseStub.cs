using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReportService.Testing;

/// <summary>
/// Replaces a process's metadata-database check with one of the test's own, so
/// a host that is not the subject of a database test runs without opening a
/// connection. The two settings the check needs are supplied beside it.
/// </summary>
public static class MetadataDatabaseStub
{
    /// <summary>The name the metadata database check carries.</summary>
    public const string CheckName = "metadata-database";

    /// <summary>The schema version a composition of this repository expects.</summary>
    public const string ExpectedVersion = "0001_baseline.sql";

    /// <summary>A syntactically valid connection string that points at nothing.</summary>
    public const string ConnectionString = "Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=nothing";

    /// <summary>Replaces the check with one that answers <paramref name="status"/>.</summary>
    public static IServiceCollection UseHealthy(this IServiceCollection services, HealthStatus status = HealthStatus.Healthy)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            var kept = options.Registrations.Where(registration => registration.Name != CheckName).ToList();
            options.Registrations.Clear();
            foreach (var registration in kept)
            {
                options.Registrations.Add(registration);
            }

            options.Registrations.Add(new HealthCheckRegistration(CheckName, new StubCheck(status), failureStatus: null, tags: null));
        });
        return services;
    }

    private sealed class StubCheck(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(
                status,
                "metadata database check replaced by a stub",
                data: new Dictionary<string, object>
                {
                    ["expected"] = ExpectedVersion,
                    ["applied"] = ExpectedVersion,
                }));
    }
}
