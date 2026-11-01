using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The development-only telemetry endpoints. They exist to prove what the
/// processes emit, so they are mounted only when
/// <c>Telemetry:Diagnostics:Enabled</c> is true, and they are never a telemetry
/// destination and never a product API:
/// <list type="bullet">
/// <item><c>GET /diagnostics/telemetry/metrics</c> reads the metric capture of the process;</item>
/// <item><c>POST /diagnostics/telemetry/unit-of-work</c> runs one unit of work under a synthetic job identifier;</item>
/// <item><c>GET /diagnostics/telemetry/trace</c> answers with the trace id of the request's own span.</item>
/// </list>
/// The API additionally mounts <c>GET /diagnostics/telemetry/propagate</c>, which
/// calls the orchestration worker and reports whether both spans share one trace.
/// </summary>
public static class TelemetryDiagnostics
{
    /// <summary>The path under which the endpoints are mounted.</summary>
    public const string RoutePrefix = "/diagnostics/telemetry";

    /// <summary>The route that reads the metric capture.</summary>
    public const string MetricsRoute = RoutePrefix + "/metrics";

    /// <summary>The route that runs one unit of work.</summary>
    public const string UnitOfWorkRoute = RoutePrefix + "/unit-of-work";

    /// <summary>The route that answers with the request's trace id.</summary>
    public const string TraceRoute = RoutePrefix + "/trace";

    /// <summary>The route that proves context propagation across a process boundary.</summary>
    public const string PropagateRoute = RoutePrefix + "/propagate";

    /// <summary>
    /// The configuration key the composition uses to give a process the
    /// orchestration worker's address; absent outside a composition.
    /// </summary>
    public const string OrchestrationWorkerAddressSetting = "services__orchestration-worker__http__0";

    /// <summary>The name the unit of work's own span carries.</summary>
    public const string UnitOfWorkSpanName = "unit-of-work";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly TimeSpan UnitOfWorkDuration = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WorkerCallTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mounts the capture read, the unit of work and the trace id when the diagnostics are on.</summary>
    public static IEndpointRouteBuilder MapTelemetryDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!TelemetryDiagnosticsSettings.Read(endpoints.ServiceProvider.GetRequiredService<IConfiguration>()).Enabled)
        {
            return endpoints;
        }

        endpoints.MapGet(MetricsRoute, MetricsAsync);
        endpoints.MapPost(UnitOfWorkRoute, UnitOfWorkAsync);
        endpoints.MapGet(TraceRoute, TraceAsync);
        return endpoints;
    }

    /// <summary>Mounts the propagation pair on the API when the diagnostics are on.</summary>
    public static IEndpointRouteBuilder MapTelemetryPropagation(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!TelemetryDiagnosticsSettings.Read(endpoints.ServiceProvider.GetRequiredService<IConfiguration>()).Enabled)
        {
            return endpoints;
        }

        endpoints.MapGet(PropagateRoute, PropagateAsync);
        return endpoints;
    }

    private static async Task MetricsAsync(HttpContext context)
    {
        var settings = TelemetryDiagnosticsSettings.Read(context.RequestServices.GetRequiredService<IConfiguration>());
        var buffer = context.RequestServices.GetRequiredService<TelemetryCaptureBuffer>();
        var body = buffer.Take(settings.ExportIntervalMilliseconds).ToJson();
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task UnitOfWorkAsync(HttpContext context)
    {
        UnitOfWorkRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<UnitOfWorkRequest>(RequestOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "telemetry.invalid-body", "the body must be { \"jobId\": \"<identifier>\" }").ConfigureAwait(false);
            return;
        }

        if (request?.JobId is not { } jobId || !JobContext.IsValid(jobId))
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "telemetry.invalid-job", "a job identifier is 1 to 128 characters of a-z, 0-9, '.', '_' or '-'").ConfigureAwait(false);
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReportService.Job");
        var buildInfo = context.RequestServices.GetRequiredService<BuildInfoMetric>();
        using (JobContext.Begin(jobId))
        {
            logger.LogInformation("unit of work started");
            using var span = TelemetryDefaults.ActivitySource.StartActivity(UnitOfWorkSpanName);
            buildInfo.Record();
            await Task.Delay(UnitOfWorkDuration, context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(
                new
                {
                    jobId,
                    traceId = TraceIdOf(span),
                    spanId = span?.SpanId.ToHexString() ?? string.Empty,
                },
                RequestOptions,
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task TraceAsync(HttpContext context) =>
        context.Response.WriteAsJsonAsync(new { traceId = TraceIdOf(Activity.Current) }, RequestOptions, context.RequestAborted);

    private static async Task PropagateAsync(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var worker = configuration[OrchestrationWorkerAddressSetting];
        if (string.IsNullOrWhiteSpace(worker))
        {
            await RefuseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "telemetry.no-worker",
                "the composition did not provide the orchestration worker's address").ConfigureAwait(false);
            return;
        }

        var own = TraceIdOf(Activity.Current);
        string workerTraceId;
        using (var client = new HttpClient { Timeout = WorkerCallTimeout })
        {
            try
            {
                using var response = await client.GetAsync(new Uri(new Uri(worker, UriKind.Absolute), TraceRoute), context.RequestAborted).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var answer = await response.Content.ReadFromJsonAsync<WorkerTrace>(RequestOptions, context.RequestAborted).ConfigureAwait(false);
                workerTraceId = answer?.TraceId ?? string.Empty;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "telemetry.worker-unreachable", exception.GetType().Name).ConfigureAwait(false);
                return;
            }
        }

        await context.Response.WriteAsJsonAsync(
            new { traceId = own, workerTraceId, same = string.Equals(own, workerTraceId, StringComparison.Ordinal) },
            RequestOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static string TraceIdOf(Activity? activity) =>
        activity is null ? string.Empty : activity.TraceId.ToHexString();

    private static Task RefuseAsync(HttpContext context, int status, string code, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;
        return context.Response.WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"{{\"code\":\"{code}\",\"reason\":\"{reason.Replace("\"", "'", StringComparison.Ordinal)}\"}}"),
            context.RequestAborted);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Created by the request body's JSON deserializer.")]
    private sealed record UnitOfWorkRequest(string? JobId);

    private sealed record WorkerTrace(string? TraceId);
}
