using ReportService.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// The dependency images are pinned in one file, shared with the integration tests.
var pins = CompositionPins.Read(builder.AppHostDirectory);

// Metadata database (PostgreSQL 18). Ephemeral: no data volume.
var postgres = builder.AddPostgres("postgres")
    .WithImage(pins.Database.Image, pins.Database.Tag);
var metadataDb = postgres.AddDatabase("reportservice");

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

// PDF worker: connects to the engine on the PDF rendering queue; ready once Chromium
// launched and the engine answers.
builder.AddContainer("render-worker", renderWorkerImage.Repository, renderWorkerImage.Tag)
    .WithImagePullPolicy(ImagePullPolicy.Never)
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WaitFor(temporal);

// Orchestration worker: a host process on the orchestration and data queues; ready once
// the engine answers.
builder.AddProject<Projects.ReportService_Workers_Orchestration>("orchestration-worker")
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", "default")
    .WithHttpHealthCheck("/health")
    .WaitFor(temporal);

// API: holds no workflow-engine client and does not wait for the engine or the workers,
// so it starts and is ready while the engine is down. It waits for its own dependencies.
var api = builder.AddProject<Projects.ReportService_Api>("api")
    .WithReference(metadataDb)
    .WithReference(cache)
    .WaitFor(metadataDb)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health");

// A consumer registry file other than the API's own (for example, one a test writes).
if (builder.Configuration["Consumers:File"] is { Length: > 0 } registryFile)
{
    api.WithEnvironment("Consumers__File", registryFile);
}

builder.Build().Run();
