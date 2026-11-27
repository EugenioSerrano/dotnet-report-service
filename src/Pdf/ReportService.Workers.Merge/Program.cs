using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using ReportService.ServiceDefaults;

namespace ReportService.Workers.Merge;

/// <summary>
/// The merge worker's process: the merge task queue, the assembly step, the object
/// store and the metadata database. It answers readiness through <c>/health</c> and
/// runs one assembly at a time, as the architecture requires of a merge process.
/// </summary>
public sealed class MergeWorkerProgram
{
    /// <summary>The exit code of a process whose start was refused by its configuration.</summary>
    public const int RefusedStartExitCode = 1;

    /// <summary>Starts the process and serves until it is stopped.</summary>
    /// <param name="args">The command line.</param>
    public static async Task Main(string[] args)
    {
        var app = BuildHost(args);
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Builds the process's host, not started.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The built host.</returns>
    public static WebApplication BuildHost(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        MergeWorkerComposition.Configure(builder);
        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        return app;
    }
}
