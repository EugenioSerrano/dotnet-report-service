using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReportService.Api.Consumers;

/// <summary>
/// The read-only diagnostic of the registry: always healthy (the registry is
/// configuration, not a dependency), reporting the count and the fingerprint
/// of the registry in force — never an identity. A refused change never flips it.
/// </summary>
public sealed class ConsumerRegistryHealthCheck(ConsumerRegistry registry) : IHealthCheck
{
    /// <summary>The name the check is registered under.</summary>
    public const string Name = "consumer-registry";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = registry.Current;
        return Task.FromResult(HealthCheckResult.Healthy(data: new Dictionary<string, object>
        {
            ["entries"] = snapshot.Entries.Count,
            ["fingerprint"] = snapshot.Fingerprint,
        }));
    }
}
