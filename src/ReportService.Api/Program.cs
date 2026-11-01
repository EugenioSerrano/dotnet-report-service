using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
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
builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
    services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<IUploadStore, PostgresUploadStore>();

var app = builder.Build();
app.UseApiExceptionHandler();
app.UseConsumerRegistry();
app.MapStorageDiagnostics();
app.MapTelemetryDiagnostics();
app.MapTelemetryPropagation();
app.MapUploadEndpoints();
app.Run();
