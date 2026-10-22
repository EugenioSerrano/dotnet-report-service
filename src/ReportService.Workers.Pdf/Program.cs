using ReportService.Workers.Pdf;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BrowserProbe>();
builder.Services.AddHostedService(static services => services.GetRequiredService<BrowserProbe>());
builder.Services.AddHealthChecks().AddCheck<BrowserHealthCheck>("chromium");

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();
