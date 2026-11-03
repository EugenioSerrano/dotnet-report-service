using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// Reports, once at start, what the process is: its service name, its product
/// version with the pre-release label and the commit the build came from, and
/// the runtime it runs on (release §5, supportability). Nothing else is logged: a
/// setting value, a path or a credential never appears in this line.
/// </summary>
internal sealed class TelemetryStartup : IHostedService
{
    private readonly string _serviceName;
    private readonly string _version;
    private readonly ILoggerFactory _loggers;
    private readonly BuildInfoMetric _buildInfo;

    public TelemetryStartup(string serviceName, string version, ILoggerFactory loggers, BuildInfoMetric buildInfo)
    {
        _serviceName = serviceName;
        _version = version;
        _loggers = loggers;
        _buildInfo = buildInfo;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ILogger logger = _loggers.CreateLogger("ReportService.Startup");
        logger.LogInformation(
            "service={ServiceName} version={Version} runtime={Runtime} commit={Commit} started",
            _serviceName,
            _version,
            Environment.Version.ToString(),
            ProductVersion.CommitOf(_version));
        _buildInfo.Record();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // A unit of work opens its scope on the logger factory of the host that opens it, so
        // there is nothing process-wide to clear when this stop ends.
        return Task.CompletedTask;
    }
}
