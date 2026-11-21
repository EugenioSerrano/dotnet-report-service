using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Configuration;

/// <summary>The JSON body of the health endpoints: documented shape, listed data keys only.</summary>
public sealed class HealthResponseWriterTests
{
    private const string PlantedSecret = "planted-secret-0b52e1";

    [Fact]
    public void BodyHasTheDocumentedShape()
    {
        var report = Report(
            ("workflow-engine", HealthStatus.Healthy, new Dictionary<string, object> { ["address"] = "localhost:7233", ["namespace"] = "default" }),
            ("chromium", HealthStatus.Unhealthy, new Dictionary<string, object>()));

        using var body = JsonDocument.Parse(HealthResponseWriter.Render(report));

        var root = body.RootElement;
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());
        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(["workflow-engine", "chromium"], checks.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("Healthy", checks[0].GetProperty("status").GetString());
        Assert.Equal(12, checks[0].GetProperty("durationMs").GetInt64());
        Assert.Equal("localhost:7233", checks[0].GetProperty("data").GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.Object, checks[1].GetProperty("data").ValueKind);
    }

    [Fact]
    public void DataKeysOutsideTheListAreNeverWritten()
    {
        var report = Report(("consumer-registry", HealthStatus.Healthy, new Dictionary<string, object>
        {
            ["entries"] = 2,
            ["fingerprint"] = "abc",
            ["password"] = PlantedSecret,
            ["connectionString"] = PlantedSecret,
        }));

        var json = HealthResponseWriter.Render(report);

        Assert.DoesNotContain(PlantedSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(json);
        var data = body.RootElement.GetProperty("checks")[0].GetProperty("data");
        Assert.Equal(["entries", "fingerprint"], data.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TheMetadataDatabaseKeysAreWrittenAndOthersStillDropped()
    {
        var report = Report(("metadata-database", HealthStatus.Unhealthy, new Dictionary<string, object>
        {
            ["expected"] = "0001_baseline.sql",
            ["applied"] = "none",
            ["error"] = "unreachable",
            ["password"] = PlantedSecret,
        }));

        var json = HealthResponseWriter.Render(report);

        Assert.DoesNotContain(PlantedSecret, json, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(json);
        var data = body.RootElement.GetProperty("checks")[0].GetProperty("data");
        Assert.Equal(["expected", "applied", "error"], data.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0001_baseline.sql", data.GetProperty("expected").GetString());
        Assert.Equal("none", data.GetProperty("applied").GetString());
        Assert.Equal("unreachable", data.GetProperty("error").GetString());
    }

    [Fact]
    public void DescriptionAndExceptionTextAreNeverWritten()
    {
        var entry = new HealthReportEntry(
            HealthStatus.Unhealthy,
            "connect to " + PlantedSecret,
            TimeSpan.Zero,
            new InvalidOperationException(PlantedSecret),
            new Dictionary<string, object>());
        var report = new HealthReport(new Dictionary<string, HealthReportEntry> { ["x"] = entry }, TimeSpan.Zero);

        Assert.DoesNotContain(PlantedSecret, HealthResponseWriter.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesJsonToTheResponse()
    {
        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;

        await HealthResponseWriter.Json(context, Report(("self", HealthStatus.Healthy, new Dictionary<string, object>())));

        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        using var body = JsonDocument.Parse(stream.ToArray());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
    }

    private static HealthReport Report(params (string Name, HealthStatus Status, Dictionary<string, object> Data)[] entries) =>
        new(
            entries.ToDictionary(
                e => e.Name,
                e => new HealthReportEntry(e.Status, null, TimeSpan.FromMilliseconds(12.7), null, e.Data)),
            TimeSpan.FromMilliseconds(20));
}
