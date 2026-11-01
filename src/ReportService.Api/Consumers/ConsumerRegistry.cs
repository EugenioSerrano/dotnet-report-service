using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Consumers;

/// <summary>
/// The consumer registry of the running service. It is validated when the
/// service starts (an invalid registry stops start-up) and re-read whenever
/// the configuration changes:
/// <list type="bullet">
/// <item>a valid change replaces the snapshot atomically;</item>
/// <item>an invalid change is refused with a warning naming each faulty entry, and the last valid registry stays in force; readiness is never affected.</item>
/// </list>
/// A request reads <see cref="Current"/> once and uses that snapshot throughout,
/// so no request ever sees a partly applied registry.
/// </summary>
public sealed partial class ConsumerRegistry : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConsumerRegistry> _logger;
    private readonly IDisposable _subscription;
    private ConsumerRegistrySnapshot _current;
    private volatile bool _disposed;

    /// <summary>Validates the registry now; throws a <see cref="ConfigurationError"/> naming each faulty entry.</summary>
    public ConsumerRegistry(IConfiguration configuration, ILogger<ConsumerRegistry> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var result = ConsumerRegistryValidator.Validate(configuration);
        LogWarnings(result.Warnings);
        if (result.Snapshot is null)
        {
            throw new ConfigurationError(result.Problems);
        }

        _current = result.Snapshot;
        LogLoaded(_current.Entries.Count, _current.Fingerprint);
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    /// <summary>The registry in force. Never changes in place.</summary>
    public ConsumerRegistrySnapshot Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _subscription.Dispose();
    }

    private void Reload()
    {
        if (_disposed)
        {
            return;
        }

        var result = ConsumerRegistryValidator.Validate(_configuration);
        LogWarnings(result.Warnings);
        if (result.Snapshot is not { } candidate)
        {
            LogRefused(string.Join("; ", result.Problems.Select(p => $"{p.Setting}: {p.Reason}")));
            return;
        }

        var current = Current;
        if (candidate.Fingerprint == current.Fingerprint && candidate.Entries.SequenceEqual(current.Entries))
        {
            return;
        }

        Interlocked.Exchange(ref _current, candidate);
        LogChanged(candidate.Entries.Count, candidate.Fingerprint);
    }

    private void LogWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            LogWarning(warning);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer registry loaded: {Entries} entries, fingerprint {Fingerprint}")]
    private partial void LogLoaded(int entries, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer registry changed: {Entries} entries, fingerprint {Fingerprint}")]
    private partial void LogChanged(int entries, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer registry change refused; the last valid registry stays in force: {Problems}")]
    private partial void LogRefused(string problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer registry: {Warning}")]
    private partial void LogWarning(string warning);
}
