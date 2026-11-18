using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ReportService.Storage;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The <c>template-source</c> health check: on each probe it lists the source's
/// class far enough to know the class answers, within <see cref="TimeBound"/>,
/// and never writes, reads an object or deletes anything — a probe on a root a
/// publisher owns is the defect this check exists not to be. Healthy carries
/// <c>provider</c>, <c>class</c> and <c>descriptorVersions</c>; unhealthy carries
/// <c>provider</c> and the <c>error</c> code — never a key, a path or a
/// credential. The check never throws; a change to unhealthy is logged once, and
/// so is the recovery.
/// </summary>
public sealed partial class TemplateSourceHealthCheck : IHealthCheck
{
    /// <summary>The name the check is registered under.</summary>
    public const string Name = "template-source";

    /// <summary>The longest a probe may take.</summary>
    public static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(5);

    private const int Unknown = 0;
    private const int Healthy = 1;
    private const int Unhealthy = 2;

    private readonly IObjectStore _store;
    private readonly TemplateSourceContract _contract;
    private readonly ILogger<TemplateSourceHealthCheck> _logger;
    private int _state = Unknown;

    /// <summary>Creates the check over the source's own store.</summary>
    /// <param name="store">The source's instance, never the process's writable store.</param>
    /// <param name="contract">What the process states about the class it reads.</param>
    /// <param name="logger">The check's log.</param>
    public TemplateSourceHealthCheck(IObjectStore store, TemplateSourceContract contract, ILogger<TemplateSourceHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _contract = contract;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? error;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bound.CancelAfter(TimeBound);
            try
            {
                await foreach (var entry in _store.ListAsync(_contract.Layout.ClassPrefix, bound.Token).ConfigureAwait(false))
                {
                    // One page is enough: the question is whether the class answers at all.
                    _ = entry;
                    break;
                }

                error = null;
            }
            catch (OperationCanceledException)
            {
                error = StorageErrorCodes.Timeout;
            }
            catch (StorageException exception)
            {
                error = exception.Error.Code;
            }
            catch (Exception)
            {
                // A health check reports; it never throws.
                error = StorageErrorCodes.Unexpected;
            }
        }

        if (error is null)
        {
            if (Interlocked.Exchange(ref _state, Healthy) == Unhealthy)
            {
                LogRecovered();
            }

            return HealthCheckResult.Healthy(data: new Dictionary<string, object>
            {
                ["provider"] = _contract.Provider,
                ["class"] = _contract.Layout.ClassPrefix,
                ["descriptorVersions"] = _contract.DescriptorVersions,
            });
        }

        if (Interlocked.Exchange(ref _state, Unhealthy) != Unhealthy)
        {
            LogFailed(error);
        }

        return HealthCheckResult.Unhealthy(data: new Dictionary<string, object>
        {
            ["provider"] = _contract.Provider,
            ["error"] = error,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Template source check failed: {Code}")]
    private partial void LogFailed(string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Template source check recovered")]
    private partial void LogRecovered();
}
