using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;

namespace ReportService.EndToEndTests;

/// <summary>
/// The composition every end-to-end class starts. It is the AppHost's own, with
/// the additions that belong to the test run and change no product behaviour: the
/// telemetry view is enabled, answers without a credential on this machine and
/// receives telemetry on the one address its own container can reach, the
/// dependency wait keeps the behaviour the composition was written for, the
/// render worker is pointed at that view before its container is created — a
/// container does not inherit the AppHost's telemetry address and cannot be given
/// one afterwards — the template a class names without a version is published
/// into the root the worker reads its templates from, before any process starts,
/// and each worker's container name is declared in the composition's own model,
/// so a harness addresses exactly this composition's container.
/// </summary>
internal static class TestComposition
{
    /// <summary>The template identifier the classes that name only a template submit.</summary>
    public const string ByNameTemplate = "pilot-grouped";

    /// <summary>The one release published for it, so a by-name reference resolves to it.</summary>
    public const string ByNameTemplateVersion = "1.0.0";

    /// <summary>
    /// The consumer the end-to-end classes submit as, and the owner the published
    /// template names: a consumer may use the versions it owns or a shared one.
    /// </summary>
    public const string FixtureConsumer = "consumer-a";

    /// <summary>The directory name the worker's template source root has under the repository root.</summary>
    public const string SourceDirectoryName = ".template-source";

    /// <summary>
    /// The one publication of this run, made the first time a composition asks for it: the
    /// source root is one directory whatever the number of compositions, and every class of
    /// the run reads the same class. Publishing it once keeps the run's own writes out of the
    /// way of the listings the worker's start and its readiness check perform on it.
    /// </summary>
    private static readonly Lazy<Task> Seed = new(
        PublishByNameTemplateAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Serialises the construction of the testing builders, and that alone. The
    /// builder's constructor imports any legacy user-secrets for the host's
    /// user-secrets id by writing and moving one file in place; every composition
    /// of this run shares the one user-secrets id, so several builders constructed
    /// at the same instant race on that file and all but one fail with an access
    /// error. Holding this for the construction lets the first one finish the import
    /// before the next begins (once the file exists the import is a no-op); the gate
    /// is released the moment the builder is constructed, so everything else — the
    /// template seed, the view configuration, the worker endpoint wiring, the build
    /// and the run itself — still proceeds in parallel across compositions.
    /// </summary>
    private static readonly SemaphoreSlim BuilderConstruction = new(1, 1);

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

    /// <summary>The merge worker's logical resource name in the run's model.</summary>
    public const string MergeWorkerName = "merge-worker";

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
    /// <param name="arguments">
    /// Arguments the AppHost reads as its own configuration, placed after the test's own. A
    /// class that states a key the composition also carries wins with the value it states,
    /// because the configuration provider keeps the last value given for a key.
    /// </param>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        // The source root is the publisher's, and this run's publisher is here: a class that
        // names a template and leaves the version to the composition reads the class the worker
        // reads, so the template is published into the source's own root before any process
        // starts. Publishing nothing would leave that class empty, and the job would be refused
        // for a template that was never published rather than for anything the case asserts.
        await Seed.Value;

        // The receiver's address is resolved here, once for this composition: the
        // value written into the run's configuration below is the one value both the
        // receiver's bind and the container's destination are read from, so a machine
        // with more than one interface cannot answer differently for the two.
        var receiver = RequestedReceiver(Address());

        // The construction is serialised on its own so the shared user-secrets import that the
        // builder's constructor performs runs once-at-a-time across compositions; see the note on
        // BuilderConstruction. The gate is held across the construction only and released in the
        // finally, so the rest of this method and the run stay parallel.
        IDistributedApplicationTestingBuilder builder;
        await BuilderConstruction.WaitAsync(cancellationToken);
        try
        {
            builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(
                new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
                arguments,
                cancellationToken);
        }
        finally
        {
            BuilderConstruction.Release();
        }

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

        // The render worker's container name belongs to this composition alone, as the merge
        // worker's does: it is declared in the composition's own model before that model is built,
        // so the restart harness can read it back and address exactly this composition's container
        // through the runtime's own log stream. The resource's logical name, its endpoint, its
        // health check and every command stay `render-worker`, so the entries drive it unchanged.
        builder.CreateResourceBuilder(worker).WithContainerName(NewRenderWorkerContainerName());

        // The merge worker's container name belongs to this composition alone: it is declared
        // in the composition's own model before that model is built, so the restart harness can
        // read it back and address exactly this composition's container — never a listing of
        // the daemon that every parallel composition's merge worker also satisfies.
        var mergeWorker = (ContainerResource)builder.Resources.Single(resource => resource.Name == MergeWorkerName);
        builder.CreateResourceBuilder(mergeWorker).WithContainerName(NewMergeWorkerContainerName());

        return builder;
    }

    /// <summary>
    /// A render-worker container name unique to one composition: the resource's logical name and
    /// sixteen hex digits of a fresh identifier. The name is part of the composition's own model,
    /// never of a daemon listing, so a sibling composition, a stale container from an earlier run
    /// or a container the runtime might otherwise re-use can never carry it.
    /// </summary>
    public static string NewRenderWorkerContainerName() =>
        RenderWorkerName + "-" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// The render-worker container name the composition declared in its own model. The name is
    /// read back from the annotation the composition put on its render worker resource before the
    /// model was built; nothing about the runtime's containers participates. It is what the render
    /// log tap addresses: the runtime's own log stream of exactly this composition's container.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The model holds no render worker resource, or the resource carries no declared container
    /// name, so this composition cannot say which container is its own.
    /// </exception>
    public static string DeclaredRenderWorkerContainerName(IDistributedApplicationTestingBuilder builder)
    {
        var renderWorker = builder.Resources.FirstOrDefault(resource => string.Equals(resource.Name, RenderWorkerName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The composition's model holds no '{RenderWorkerName}' resource, so the render worker's container name cannot be read back.");
        var declared = renderWorker.Annotations.OfType<ContainerNameAnnotation>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The composition's '{RenderWorkerName}' resource carries no declared container name, so the restart harness cannot address this composition's container.");
        return declared.Name;
    }

    /// <summary>
    /// A merge-worker container name unique to one composition: the resource's logical name
    /// and sixteen hex digits of a fresh identifier. The name is part of the composition's own
    /// model, never of a daemon listing, so a sibling composition, a stale container from an
    /// earlier run or a container the runtime might otherwise re-use can never carry it.
    /// </summary>
    public static string NewMergeWorkerContainerName() =>
        MergeWorkerName + "-" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// The merge-worker container name the composition declared in its own model. The name is
    /// read back from the annotation the composition put on its merge worker resource before
    /// the model was built; nothing about the runtime's containers participates.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The model holds no merge worker resource, or the resource carries no declared container
    /// name, so this composition cannot say which container is its own.
    /// </exception>
    public static string DeclaredMergeWorkerContainerName(IDistributedApplicationTestingBuilder builder)
    {
        var mergeWorker = builder.Resources.FirstOrDefault(resource => string.Equals(resource.Name, MergeWorkerName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The composition's model holds no '{MergeWorkerName}' resource, so the merge worker's container name cannot be read back.");
        var declared = mergeWorker.Annotations.OfType<ContainerNameAnnotation>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The composition's '{MergeWorkerName}' resource carries no declared container name, so the restart harness cannot address this composition's container.");
        return declared.Name;
    }

    /// <summary>
    /// Publishes the template a job that names only a template resolves, into the root
    /// the render worker reads its templates from — the source's own class, mounted
    /// read-only into that process. The package is one the product's checks accept for a
    /// template this deployment's publisher owns, so the by-name resolution has exactly
    /// one release to walk; nothing is written to a class the service itself keeps. The
    /// version's record and files are removed through the fixture before the publication,
    /// so a run leaves one record under the version and changed bytes replace the old
    /// record instead of leaving a second one beside it. The window between the removal
    /// and the publication is a test-environment failure, never product behaviour.
    /// </summary>
    private static async Task PublishByNameTemplateAsync()
    {
        var root = Path.Combine(RepositoryPaths.Root, SourceDirectoryName);
        Directory.CreateDirectory(root);
        var source = await TemplateSourceClass.CreateAsync(root);
        try
        {
            await source.RemoveAsync(ByNameTemplate, ByNameTemplateVersion);
            await source.PublishAsync(
                ByNameTemplate,
                ByNameTemplateVersion,
                TemplatePackageFixtures.Valid(ByNameTemplate, ByNameTemplateVersion, FixtureConsumer));

            // The page-accounting fixture is published beside the by-name package so the
            // composition's own template source holds the package the accounting cases take:
            // it is a source publication, not a taken version, and a case still takes it.
            await source.RemoveAsync(PageAccountingFixtures.TemplateId, PageAccountingFixtures.Version);
            await source.PublishAsync(
                PageAccountingFixtures.TemplateId,
                PageAccountingFixtures.Version,
                PageAccountingFixtures.Files(FixtureConsumer, PageAccountingFixtures.TemplateId));
        }
        finally
        {
            await source.DisposeAsync();
        }
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
