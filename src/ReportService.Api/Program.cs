using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceConfiguration();
builder.AddConsumerRegistry();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseConsumerRegistry();
app.Run();
