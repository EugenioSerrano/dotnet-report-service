using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The telemetry every process shares: structured logs with their scopes,
/// traces for the requests and outgoing calls, and metrics — under one
/// <c>service.name</c> per process, with the build's version as
/// <c>service.version</c>. The destination is configuration: an OTLP endpoint
/// is exported to only when one is configured, and a process with none starts
/// and runs without an export attempt.
/// </summary>
public static class TelemetryDefaults
{
    /// <summary>The meter the product's own instruments are created on.</summary>
    public const string MeterName = "ReportService";

    /// <summary>The activity source the product's own spans are started from.</summary>
    public const string ActivitySourceName = "ReportService";

    /// <summary>The setting that names the OTLP endpoint, alongside the standard environment variable.</summary>
    public const string OtlpEndpointSetting = "Telemetry:OtlpEndpoint";

    /// <summary>The standard environment variable that names the OTLP endpoint.</summary>
    public const string OtlpEndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The standard environment variable that selects the OTLP protocol.</summary>
    public const string OtlpProtocolEnvironmentVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>The resource attribute that names the environment a process runs in.</summary>
    public const string DeploymentEnvironmentAttribute = "deployment.environment";

    /// <summary>The value of <see cref="OtlpProtocolEnvironmentVariable"/> that selects OTLP over HTTP.</summary>
    public const string HttpProtobufProtocol = "http/protobuf";

    private const string HealthPath = "/health";

    /// <summary>The source the product's own spans are started from; the tests' handlers read it.</summary>
    internal static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    /// <summary>
    /// Registers the three signals of <paramref name="serviceName"/>: the
    /// resource attributes, the instrumentation, the job and redaction
    /// processors, the destination exporter when an endpoint is configured, and
    /// — only under the development diagnostics flag — the metric capture.
    /// </summary>
    /// <param name="builder">The process's host builder.</param>
    /// <param name="serviceName">The <c>service.name</c> this process reports.</param>
    /// <param name="configureTelemetry">
    /// The one seam a test uses: it runs on the same metric and trace pipeline the
    /// process registers, so a test can add an in-memory exporter. No environment
    /// variable and no production path selects a test exporter.
    /// </param>
    /// <param name="configureLogging">The same seam on the log pipeline.</param>
    public static IHostApplicationBuilder AddServiceDefaults(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<IOpenTelemetryBuilder>? configureTelemetry = null,
        Action<OpenTelemetryLoggerOptions>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: ProductVersion.Informational)
            .AddAttributes([new KeyValuePair<string, object>(DeploymentEnvironmentAttribute, DeploymentEnvironment(builder))]);
        var redaction = RedactionOptions.Load(builder.Configuration);
        var diagnostics = TelemetryDiagnosticsSettings.Read(builder.Configuration);
        var capture = new TelemetryCaptureBuffer();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            logging.SetResourceBuilder(resource);
            logging.AddProcessor(new RedactionLogProcessor(redaction));
            configureLogging?.Invoke(logging);
        });

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resource);
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddRuntimeInstrumentation();
                metrics.AddMeter(MeterName);
                if (diagnostics.Enabled)
                {
                    // The capture sits beside the destination exporter on its own reader: it sees
                    // the same points with or without a destination, and it is not a destination.
                    metrics.AddReader(new PeriodicExportingMetricReader(
                        new TelemetryCaptureExporter(capture),
                        diagnostics.ExportIntervalMilliseconds));
                }
            })
            .WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(resource);
                tracing.AddSource(ActivitySourceName);
                tracing.AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    !context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase));
                tracing.AddHttpClientInstrumentation();
                tracing.AddProcessor(new JobIdActivityProcessor());
                tracing.AddProcessor(new RedactionActivityProcessor(redaction));
            });

        configureTelemetry?.Invoke(openTelemetry);

        if (Endpoint(builder.Configuration) is { } endpoint)
        {
            openTelemetry.UseOtlpExporter(Protocol(builder.Configuration), endpoint);
        }

        if (diagnostics.Enabled)
        {
            builder.Services.AddSingleton(capture);
        }

        builder.Services.AddSingleton(new BuildInfoMetric());
        builder.Services.AddHostedService(services => new TelemetryStartup(
            serviceName,
            ProductVersion.Informational,
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<BuildInfoMetric>()));
        return builder;
    }

    /// <summary>The configured OTLP endpoint, or <see langword="null"/> when none is configured.</summary>
    public static Uri? Endpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[OtlpEndpointSetting];
        var value = string.IsNullOrWhiteSpace(configured) ? configuration[OtlpEndpointEnvironmentVariable] : configured;
        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    /// <summary>The OTLP protocol the dashboard's endpoint scheme selects; gRPC unless the deployment says HTTP.</summary>
    public static OtlpExportProtocol Protocol(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(configuration[OtlpProtocolEnvironmentVariable], HttpProtobufProtocol, StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    private static string DeploymentEnvironment(IHostApplicationBuilder builder) =>
        builder.Environment.EnvironmentName is { Length: > 0 } name ? name : "unknown";
}
