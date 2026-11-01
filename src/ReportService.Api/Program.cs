using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceConfiguration();
builder.AddConsumerRegistry();
builder.Services.AddHealthChecks();
builder.AddObjectStorage("api");

var app = builder.Build();
app.UseConsumerRegistry();
app.MapStorageDiagnostics();
app.Run();
