using ReportService.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Metadata database (PostgreSQL 18). Ephemeral: no data volume.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18.6");
var metadataDb = postgres.AddDatabase("reportservice");

// Distributed cache (Valkey 9.1, Redis protocol).
var cache = builder.AddValkey("cache")
    .WithImageTag("9.1.2");

// Temporal development server (official CLI image).
var temporal = builder.AddContainer("temporal", "temporalio/temporal", "1.9.1")
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

var renderWorker = builder.AddContainer("render-worker", renderWorkerImage.Repository, renderWorkerImage.Tag)
    .WithImagePullPolicy(ImagePullPolicy.Never)
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpHealthCheck("/health", endpointName: "http");

// API stub: starts only when every dependency is healthy.
builder.AddProject<Projects.ReportService_Api>("api")
    .WithReference(metadataDb)
    .WithReference(cache)
    .WithEnvironment("Temporal__Address",
        ReferenceExpression.Create($"{temporalGrpc.Property(EndpointProperty.HostAndPort)}"))
    .WaitFor(metadataDb)
    .WaitFor(cache)
    .WaitFor(temporal)
    .WaitFor(renderWorker)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
