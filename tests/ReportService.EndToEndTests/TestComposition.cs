using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ReportService.EndToEndTests;

/// <summary>
/// The composition every end-to-end class starts. It is the AppHost's own, with
/// three additions that belong to the test run and change no product behaviour:
/// the telemetry view is enabled and anonymous on this machine, the dependency
/// wait keeps the behaviour the composition was written for, and the render
/// worker is pointed at that view before its container is created — a container
/// does not inherit the AppHost's telemetry address and cannot be given one
/// afterwards.
/// </summary>
internal static class TestComposition
{
    /// <summary>The switch that runs the test's telemetry view without a credential.</summary>
    public const string AnonymousSetting = "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS";

    /// <summary>The dashboard setting that fixes the receiver's address.</summary>
    public const string DashboardOtlpEndpointSetting = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";

    /// <summary>The variable a process reads its telemetry destination from.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The render worker's resource name.</summary>
    public const string RenderWorkerName = "render-worker";

    /// <summary>The address the run's telemetry view receives telemetry on.</summary>
    public static readonly Uri DashboardOtlpEndpoint = new("http://0.0.0.0:19701", UriKind.Absolute);

    /// <summary>The same receiver as a container reaches it: this machine, never its loopback.</summary>
    public static readonly Uri ContainerOtlpEndpoint = new("http://host.docker.internal:19701", UriKind.Absolute);

    /// <summary>Creates the testing builder of the composition, ready to build.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="arguments">Arguments the AppHost reads as its own configuration.</param>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(
            new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
            [AnonymousSetting + "=true", DashboardOtlpEndpointSetting + "=" + DashboardOtlpEndpoint, .. arguments],
            cancellationToken);

        // Enabling the telemetry view switches the default wait behaviour to stop as soon as a
        // dependency is briefly unavailable. The composition declares plain waits, so the run
        // sets the behaviour explicitly instead of inheriting the view's default.
        builder.Services.Configure<ResourceNotificationServiceOptions>(options =>
            options.DefaultWaitBehavior = WaitBehavior.WaitOnResourceUnavailable);

        var worker = (ContainerResource)builder.Resources.Single(resource => resource.Name == RenderWorkerName);
        builder.CreateResourceBuilder(worker).WithEnvironment(OtlpEndpointVariable, Text(ContainerOtlpEndpoint));
        return builder;
    }

    /// <summary>The address a process receives its telemetry destination from, as text.</summary>
    public static string Text(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);
}
