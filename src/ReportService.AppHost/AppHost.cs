using System.Security.Cryptography;
using ReportService.AppHost;
using ReportService.Migrations;

var builder = DistributedApplication.CreateBuilder(args);

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
if (builder.ExecutionContext.IsRunMode)
{
    builder.OnBeforeStart((@event, cancellationToken) =>
        RenderWorkerImage.PrepareAsync(renderWorkerImage, @event.Services, cancellationToken));
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

// PDF worker: connects to the engine on the PDF rendering queue; ready once Chromium
// launched and the engine answers. It uses no metadata record yet, so it has no
// database setting and no database check.
builder.AddContainer("render-worker", renderWorkerImage.Repository, renderWorkerImage.Tag)
    .WithImagePullPolicy(ImagePullPolicy.Never)
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithBindMount(storage.MountSource, SharedStorageRoot.ContainerPath, isReadOnly: false)
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", SharedStorageRoot.ContainerPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "false")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WaitFor(temporal);

// Orchestration worker: a host process on the orchestration and data queues; ready once
// the engine answers and the metadata database is at the version the code expects.
builder.AddProject<Projects.ReportService_Workers_Orchestration>("orchestration-worker")
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", storage.HostPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "true")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
    .WithHttpHealthCheck("/health")
    .WaitFor(temporal)
    .WaitForCompletion(migrations);

// API: holds no workflow-engine client and does not wait for the engine or the workers,
// so it starts and is ready while the engine is down. It waits for its own dependencies,
// among them the migrator's successful exit.
var api = builder.AddProject<Projects.ReportService_Api>("api")
    .WithEnvironment("ConnectionStrings__reportservice", metadataConnection)
    .WithEnvironment("Database__ExpectedSchemaVersion", MigrationCatalog.ExpectedVersion)
    .WithReference(cache)
    .WithEnvironment("Storage__Provider", SharedStorageRoot.Provider)
    .WithEnvironment("Storage__Filesystem__Root", storage.HostPath)
    .WithEnvironment("Storage__Filesystem__CreateIfMissing", "true")
    .WithEnvironment("Storage__Diagnostics__Enabled", "true")
    .WaitFor(cache)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

// A consumer registry file other than the API's own (for example, one a test writes).
if (builder.Configuration["Consumers:File"] is { Length: > 0 } registryFile)
{
    api.WithEnvironment("Consumers__File", registryFile);
}

builder.Build().Run();
