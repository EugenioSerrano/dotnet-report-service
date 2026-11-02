using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Uploads;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(ServiceNames.Api);
builder.AddServiceConfiguration();
builder.AddMetadataDatabase();
builder.AddConsumerRegistry();
builder.Services.AddHealthChecks();
builder.AddObjectStorage("api");

// Upload sessions: their lifetime is validated configuration, their records live in the
// metadata database, and the clock every session's timestamps come from is a service so a
// test can fix it.
builder.AddValidatedConfiguration<UploadOptions>(UploadOptions.SectionPath);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ConsumerRefusalMetrics>();
builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
    services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<IUploadStore, PostgresUploadStore>();

// Report jobs: acceptance stores the job, its output, its template reference and its start
// request in one transaction of the same database, and answers only after that commit.
builder.Services.AddSingleton<IJobStore, PostgresJobStore>();
builder.Services.AddSingleton<JobAcceptanceMetrics>();

var app = builder.Build();
app.UseApiExceptionHandler();
app.UseConsumerRegistry();
app.MapStorageDiagnostics();
app.MapTelemetryDiagnostics();
app.MapTelemetryPropagation();
app.MapUploadEndpoints();
app.MapUploadContentEndpoint();
app.MapCompleteUploadEndpoint();
app.MapCreateReportJobEndpoint();
app.Run();
