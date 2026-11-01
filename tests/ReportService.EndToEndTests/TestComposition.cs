using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReportService.EndToEndTests;

/// <summary>
/// The composition every end-to-end class starts. It is the AppHost's own, with
/// three additions that belong to the test run and change no product behaviour:
/// the telemetry view is enabled, answers without a credential on this machine
/// and receives telemetry on the one address its own container can reach, the
/// dependency wait keeps the behaviour the composition was written for, and the
/// render worker is pointed at that view before its container is created — a
/// container does not inherit the AppHost's telemetry address and cannot be given
/// one afterwards.
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

    /// <summary>The port the run's telemetry view receives telemetry on.</summary>
    public const int ReceiverPort = 19701;

    /// <summary>The name the PDF worker's container reaches this machine by.</summary>
    public const string ContainerHostName = "host.docker.internal";

    /// <summary>Creates the testing builder of the composition, ready to build.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="arguments">Arguments the AppHost reads as its own configuration.</param>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var receiver = Receiver();
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(
            new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
            arguments,
            cancellationToken);

        // The testing builder starts its telemetry view secured and blanks both receiver
        // addresses, and it says so in a configuration source of its own that outranks an
        // argument of the same name. Its view is not part of the model yet — it is created when
        // the run starts — so the run states what it needs in a source of its own instead: added
        // after the builder's, it is read last, and the view's own configuration copies every
        // ASPIRE_DASHBOARD_* entry of the live configuration into its process.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AnonymousSetting] = "true",
            [DashboardOtlpEndpointSetting] = Text(receiver),
        });

        // Enabling the telemetry view switches the default wait behaviour to stop as soon as a
        // dependency is briefly unavailable. The composition declares plain waits, so the run
        // sets the behaviour explicitly instead of inheriting the view's default.
        builder.Services.Configure<ResourceNotificationServiceOptions>(options =>
            options.DefaultWaitBehavior = WaitBehavior.WaitOnResourceUnavailable);

        var worker = (ContainerResource)builder.Resources.Single(resource => resource.Name == RenderWorkerName);
        builder.CreateResourceBuilder(worker).WithEnvironment(OtlpEndpointVariable, Text(ContainerReceiver()));
        return builder;
    }

    /// <summary>
    /// The address the run's telemetry view receives telemetry on: the one this
    /// machine's container reaches, and no other. Publishing the receiver on every
    /// interface would exceed what the run needs, so the address is the one the
    /// container's own name for this machine resolves to here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name does not resolve to an IPv4 address here.</exception>
    public static Uri Receiver()
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(ContainerHostName);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"The PDF worker reaches this machine as '{ContainerHostName}', and the name does not resolve here, so the run cannot state the one address its telemetry receiver must bind.",
                exception);
        }

        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException(
                $"The PDF worker reaches this machine as '{ContainerHostName}', and the name resolves only to [{string.Join(", ", addresses.Select(candidate => candidate.ToString()))}], none of them IPv4, so the run cannot state the one address its telemetry receiver must bind.");
        return new UriBuilder(Uri.UriSchemeHttp, address.ToString(), ReceiverPort).Uri;
    }

    /// <summary>The same receiver as the PDF worker's container reaches it.</summary>
    public static Uri ContainerReceiver() => new UriBuilder(Uri.UriSchemeHttp, ContainerHostName, ReceiverPort).Uri;

    /// <summary>The address a process receives its telemetry destination from, as text.</summary>
    public static string Text(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);
}
