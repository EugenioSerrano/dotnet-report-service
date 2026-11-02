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

    /// <summary>
    /// The dashboard setting that gives the view's run store a directory of its own.
    /// The view keeps a lock per run under its data directory and refuses to start
    /// when another view holds the same run, so two compositions that start at the
    /// same instant must not share one.
    /// </summary>
    public const string DashboardDataDirectorySetting = "ASPIRE_DASHBOARD_DATA_DIRECTORY";

    /// <summary>The variable a process reads its telemetry destination from.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The render worker's resource name.</summary>
    public const string RenderWorkerName = "render-worker";

    /// <summary>The telemetry view's resource name in the run's model.</summary>
    public const string DashboardResourceName = "aspire-dashboard";

    /// <summary>The name the view gives its receiver endpoint in the run's model.</summary>
    public const string DashboardReceiverEndpointName = "otlp-grpc";

    /// <summary>
    /// The port the run asks the view's receiver to listen on. The run's ports
    /// are its own, so the platform may hold the receiver elsewhere; the port the
    /// container is told is the one the model reports, never this one.
    /// </summary>
    public const int RequestedReceiverPort = 19701;

    /// <summary>
    /// The name the local container runtime gives the machine the composition
    /// runs on. It is used to discover the interface a container reaches this
    /// machine by; it is never the name the container dials, because inside the
    /// container it resolves to the runtime's own gateway, not to that interface.
    /// </summary>
    public const string ContainerHostName = "host.docker.internal";

    /// <summary>Creates the testing builder of the composition, ready to build.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="arguments">Arguments the AppHost reads as its own configuration.</param>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        // The receiver's address is resolved here, once for this composition: the
        // value written into the run's configuration below is the one value both the
        // receiver's bind and the container's destination are read from, so a machine
        // with more than one interface cannot answer differently for the two.
        var receiver = RequestedReceiver(Address());
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(
            new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
            arguments,
            cancellationToken);

        // The testing builder starts its telemetry view secured and blanks both receiver
        // addresses, and it says so in a configuration source of its own that outranks an
        // argument of the same name. Its view is not part of the model yet — it is created when
        // the run starts — so the run states what it needs in a source of its own instead: added
        // after the builder's, it is read last, and the view's own configuration copies every
        // ASPIRE_DASHBOARD_* entry of the live configuration into its process. The address here
        // is the interface the container reaches this machine on; the port is the run's own,
        // because the run's ports are assigned per run rather than taken from a fixed number.
        // The view also gets a data directory of its own, so its run lock belongs to this
        // composition alone and a second view started at the same instant cannot collide with it.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AnonymousSetting] = "true",
            [DashboardOtlpEndpointSetting] = Text(receiver),
            [DashboardDataDirectorySetting] = ViewDataDirectory(),
        });

        // Enabling the telemetry view switches the default wait behaviour to stop as soon as a
        // dependency is briefly unavailable. The composition declares plain waits, so the run
        // sets the behaviour explicitly instead of inheriting the view's default.
        builder.Services.Configure<ResourceNotificationServiceOptions>(options =>
            options.DefaultWaitBehavior = WaitBehavior.WaitOnResourceUnavailable);

        // The container is pointed at the view through the endpoint the run gives it, read from
        // the model while the container is being created — that is still before it starts, and
        // it is the only moment the run knows the port the view listens on. Its address is read
        // back from the receiver entry written above: the same value the receiver binds, stated
        // once, so the two halves of the destination cannot drift apart.
        var worker = (ContainerResource)builder.Resources.Single(resource => resource.Name == RenderWorkerName);
        builder.CreateResourceBuilder(worker).WithEnvironment(context =>
        {
            context.EnvironmentVariables[OtlpEndpointVariable] = Text(ContainerReceiver(builder));
        });
        return builder;
    }

    /// <summary>
    /// A data directory for this composition's telemetry view, created if it does
    /// not exist. The view keeps one run database and one lock file per run below
    /// it, and two views writing one directory refuse to start; a directory per
    /// composition is what keeps a test class independent of any other composition
    /// running beside it. It lives in the machine's temporary directory and is left
    /// for the operating system to clean up.
    /// </summary>
    public static string ViewDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "report-service-telemetry-view", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The address the run's telemetry view receives telemetry on: the one this
    /// machine's container reaches, and no other. Publishing the receiver on every
    /// interface would exceed what the run needs, so the address is the one the
    /// container's own name for this machine resolves to here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name does not resolve to an IPv4 address here.</exception>
    public static IPAddress Address()
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

        return addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException(
                $"The PDF worker reaches this machine as '{ContainerHostName}', and the name resolves only to [{string.Join(", ", addresses.Select(candidate => candidate.ToString()))}], none of them IPv4, so the run cannot state the one address its telemetry receiver must bind.");
    }

    /// <summary>The receiver as the run asks the view to bind it, address and requested port.</summary>
    public static Uri RequestedReceiver(IPAddress address) =>
        new UriBuilder(Uri.UriSchemeHttp, address.ToString(), RequestedReceiverPort).Uri;

    /// <summary>
    /// The address carried by a receiver entry. This is the one value a composition
    /// states and reads back: the receiver binds it and the render worker's container
    /// dials it, so it is parsed from the text the run wrote rather than resolved
    /// again — a machine with more than one interface answers a second resolution
    /// differently, and the container then exports into the void.
    /// </summary>
    /// <param name="receiverEndpoint">The receiver entry as the run states it.</param>
    /// <exception cref="InvalidOperationException">The entry is not an absolute address.</exception>
    public static IPAddress ConfiguredReceiverAddress(string? receiverEndpoint)
    {
        if (!Uri.TryCreate(receiverEndpoint, UriKind.Absolute, out var receiver)
            || !IPAddress.TryParse(receiver.Host, out var address))
        {
            throw new InvalidOperationException(
                $"The composition states its telemetry receiver as an absolute address, and '{receiverEndpoint}' is not one an address can be read back from, so the render worker's container cannot be pointed at the receiver.");
        }

        return address;
    }

    /// <summary>
    /// The receiver as the render worker's container reaches it: the address the
    /// receiver is bound on — read back from the receiver entry the run states — and
    /// the port the run allocated, which the model reports once the run allocated it,
    /// never the port this composition asked for.
    /// <para>
    /// The address is stated literally in the container's destination. The name
    /// <see cref="ContainerHostName"/> is not usable here: it resolves to this machine
    /// on the host, but inside a container it resolves to the container runtime's own
    /// gateway, which forwards to the host's loopback only — and the receiver is bound
    /// on the interface, so a container dialling that name is refused. Measured on the
    /// reference environment: <c>host.docker.internal</c> resolves to
    /// <c>192.168.65.254</c> inside the container, a dial there is refused, and a
    /// dial to the interface address the receiver is bound on connects.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The run has no receiver endpoint or no address to point the container at.</exception>
    public static Uri ContainerReceiver(IDistributedApplicationTestingBuilder builder)
    {
        var dashboard = builder.Resources.FirstOrDefault(resource => string.Equals(resource.Name, DashboardResourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The composition starts a telemetry view, and the run's model holds no '{DashboardResourceName}' resource to take its receiver's port from.");

        var endpoint = dashboard.Annotations.OfType<EndpointAnnotation>().FirstOrDefault(candidate => string.Equals(candidate.Name, DashboardReceiverEndpointName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The run's telemetry view has no '{DashboardReceiverEndpointName}' endpoint, so the composition cannot tell the PDF worker where to send its telemetry.");

        var allocated = endpoint.AllocatedEndpoint
            ?? throw new InvalidOperationException(
                $"The run had not allocated the telemetry view's receiver when the PDF worker's container was created, so the composition cannot tell the worker where to send its telemetry.");

        return ContainerReceiver(
            ConfiguredReceiverAddress(builder.Configuration[DashboardOtlpEndpointSetting]),
            allocated.Port);
    }

    /// <summary>
    /// The receiver as a container reaches it: the address the receiver is bound
    /// on, and the port the run allocated. The two halves are the same address on
    /// purpose — a container that is handed any other name exports into the void.
    /// </summary>
    /// <param name="receiverAddress">The address the receiver is bound on.</param>
    /// <param name="allocatedPort">The port the run allocated to the receiver.</param>
    public static Uri ContainerReceiver(IPAddress receiverAddress, int allocatedPort) =>
        new UriBuilder(Uri.UriSchemeHttp, receiverAddress.ToString(), allocatedPort).Uri;

    /// <summary>The address a process receives its telemetry destination from, as text.</summary>
    public static string Text(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);
}
