using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.AppHost;
using ReportService.Migrations;

// The end-to-end level prepares the render worker image once for the whole run,
// before any composition starts: this switch is that one preparation. The
// composition itself is not started and no resource of it is created.
var prepareOnly = args.Contains(RenderWorkerImage.PrepareOnlyArgument, StringComparer.Ordinal);
var builder = DistributedApplication.CreateBuilder(
    args.Where(argument => !string.Equals(
        argument, RenderWorkerImage.PrepareOnlyArgument, StringComparison.Ordinal)).ToArray());

if (prepareOnly)
{
    // The process exits after the preparation; the factory is not disposed.
    var logging = LoggerFactory.Create(options => options.AddSimpleConsole(console => console.SingleLine = true));
    using var services = new ServiceCollection().AddSingleton<ILoggerFactory>(logging).BuildServiceProvider();
    var image = await RenderWorkerImage.DescribeAsync(builder.AppHostDirectory);
    await RenderWorkerImage.PrepareAsync(image, services, CancellationToken.None);
    return;
}

// The dependency images are pinned in one file, shared with the integration tests.
var pins = CompositionPins.Read(builder.AppHostDirectory);

// The application role's password: generated per composition start, held as a
// secret parameter and never stored.
var metadataPassword = builder.AddParameter(
    "metadata-db-password",
    Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
    publishValueAsDefault: false,
    secret: true);

// Metadata database (PostgreSQL 18). Ephemeral: no data volume. The server keeps
// its own bootstrap superuser, which only it and this composition's own health
// checks use; the init script creates the application role and the database it
// owns before anything connects for work.
var postgres = builder.AddPostgres("postgres")
    .WithImage(pins.Database.Image, pins.Database.Tag)
    .WithInitFiles(Path.Combine(builder.AppHostDirectory, "postgres-init"))
    .WithEnvironment("REPORTSERVICE_DB_PASSWORD", metadataPassword);
var metadataDb = postgres.AddDatabase("reportservice"); // the init script already created it; the server ignores the duplicate

// What every product process receives: the application role only, never the
// server's bootstrap superuser.
var metadataConnection = ReferenceExpression.Create(
    $"Host={postgres.Resource.Host};Port={postgres.Resource.Port};Username=reportservice;Password={metadataPassword.Resource};Database=reportservice");

// The one-shot migrator: applies the embedded scripts and exits. The processes
// that use the database start only after it succeeded.
var migrations = builder.AddProject<Projects.ReportService_Migrations>("migrations")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WaitFor(metadataDb);
// Distributed cache (Valkey 9.1, Redis protocol).
var cache = builder.AddValkey("cache")
    .WithImage(pins.Cache.Image, pins.Cache.Tag);

// Temporal development server (official CLI image).
var temporal = builder.AddContainer("temporal", pins.WorkflowEngine.Image, pins.WorkflowEngine.Tag)
    .WithArgs("server", "start-dev", "--ip", "0.0.0.0")
    .WithEndpoint(targetPort: 7233, name: "grpc")
    .WithHttpEndpoint(targetPort: 8233, name: "ui")
    .WithHttpHealthCheck("/", endpointName: "ui");
var temporalGrpc = temporal.GetEndpoint("grpc");

// Render worker: the Linux image built by the .NET SDK on the PDF base image.
// Before any resource starts, the image is prepared (see RenderWorkerImage).
var renderWorkerImage = await RenderWorkerImage.DescribeAsync(builder.AppHostDirectory);
var imagePrepared = string.Equals(
    Environment.GetEnvironmentVariable(RenderWorkerImage.PreparedVariable),
    "true",
    StringComparison.OrdinalIgnoreCase);
if (builder.ExecutionContext.IsRunMode)
{
    builder.OnBeforeStart((@event, cancellationToken) => imagePrepared
        ? RenderWorkerImage.UsePreparedAsync(renderWorkerImage, @event.Services, cancellationToken)
        : RenderWorkerImage.PrepareAsync(renderWorkerImage, @event.Services, cancellationToken));
}

// The engine address as each consumer reaches it: a container resolves it on the
// composition's container network (temporal:7233), a host process on the host.
var temporalAddress = ReferenceExpression.Create($"{temporalGrpc.Property(EndpointProperty.HostAndPort)}");

// Object storage: one directory shared by the three processes (see SharedStorageRoot).
// The host processes use the host path; the PDF worker sees the same directory mounted
// at /data/storage. The development diagnostics that prove the sharing are switched on here.
var storage = await SharedStorageRoot.DescribeAsync(renderWorkerImage.RepositoryRoot);
if (builder.ExecutionContext.IsRunMode)
{
    Directory.CreateDirectory(storage.HostPath);
}

// Where the PDF worker sends its telemetry: the dashboard this composition starts
// secures its OTLP receiver on the address the launch profile names, and a container
// reaches the host — not its loopback — through the host-reachable name below. A
// deployment that knows its own OTLP address sets Telemetry:OtlpEndpoint instead and
// the variable below takes that value. Without either, the worker exports nowhere.
var renderWorkerOtlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"]
    ?? TelemetryAddress.ContainerReachable(builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);

// PDF worker: connects to the engine on the PDF rendering queue; ready once Chromium
// launched and the engine answers. It uses no metadata record yet, so it has no
// database setting and no database check.
var renderWorker = builder.AddContainer("render-worker", renderWorkerImage.Repository, renderWorkerImage.Tag)
    .WithImagePullPolicy(ImagePullPolicy.Never)
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithBindMount(storage.MountSource, SharedStorageRoot.ContainerPath, isReadOnly: false)
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", SharedStorageRoot.ContainerPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "false")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__ExportIntervalMilliseconds", "2000")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WaitFor(temporal)
    .WaitForCompletion(migrations);
if (renderWorkerOtlpEndpoint is { Length: > 0 })
{
    renderWorker.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", renderWorkerOtlpEndpoint);
}

// The template source the worker reads a version from: one repository and branch for
// the deployment, from its own configuration, and the read-only credential as a secret
// parameter. No repository is named here — the deployment's is its own — so a
// composition without one stops the worker naming the setting instead of reading a
// repository nobody chose.
var templateSourceRepository = builder.Configuration["TemplateSource:Repository"] ?? string.Empty;
var templateSourceBranch = builder.Configuration["TemplateSource:Branch"] ?? "main";
var templateSourceToken = builder.AddParameter("template-source-token", secret: true);
renderWorker
    .WithEnvironment("TemplateSource__Repository", templateSourceRepository)
    .WithEnvironment("TemplateSource__Branch", templateSourceBranch)
    .WithEnvironment("TemplateSource__Token", templateSourceToken);

// Orchestration worker: a host process on the orchestration and data queues; ready once
// the engine answers and the metadata database is at the version the code expects.
// One stored-size limit is given to both the API and this worker: a body accepted by
// one and refused by the other would be a defect of the composition, not of a process.
var maxStoredBytes = builder.Configuration["Uploads:MaxStoredBytes"] ?? "4294967296";

// The verification deadline of a confirmed upload is one value for the whole composition:
// the API records it at the confirmation, and the worker gives the verification the same
// span as its schedule-to-close, so the engine stops retrying when the upload's own
// deadline passes.
var verificationDeadline = builder.Configuration["Uploads:VerificationDeadline"] ?? "06:00:00";
var orchestrationWorker = builder.AddProject<Projects.ReportService_Workers_Orchestration>("orchestration-worker")
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithEnvironment("Uploads__MaxStoredBytes", maxStoredBytes)
    .WithEnvironment("Uploads__VerificationDeadline", verificationDeadline)
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", storage.HostPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "true")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__ExportIntervalMilliseconds", "2000")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
    .WithHttpHealthCheck("/health")
    .WaitFor(temporal)
    .WaitForCompletion(migrations);

// API: holds no workflow-engine client and does not wait for the engine or the workers,
// so it starts and is ready while the engine is down. It waits for its own dependencies,
// among them the migrator's successful exit. It is given the orchestration worker's
// address so its development-only propagation check can call it.
var api = builder.AddProject<Projects.ReportService_Api>("api")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
    .WithEnvironment("Uploads__MaxStoredBytes", maxStoredBytes)
    .WithEnvironment("Uploads__VerificationDeadline", verificationDeadline)
    .WithReference(cache)
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", storage.HostPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "true")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__Enabled", "true")
    .WithEnvironment("Telemetry__Diagnostics__ExportIntervalMilliseconds", "2000")
    .WaitFor(cache)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health")
    .WithReference(orchestrationWorker);

// A consumer registry file other than the API's own (for example, one a test writes).
if (builder.Configuration["Consumers:File"] is { Length: > 0 } registryFile)
{
    api.WithEnvironment("Consumers__File", registryFile);
}

// A session lifetime other than the API's own (for example, one a test sets), so a
// short-lived session can be observed on the running service.
if (builder.Configuration["Uploads:SessionLifetime"] is { Length: > 0 } apiSessionLifetime)
{
    api.WithEnvironment("Uploads__SessionLifetime", apiSessionLifetime);
}

builder.Build().Run();
