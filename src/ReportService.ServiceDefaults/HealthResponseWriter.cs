using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The JSON body of the health endpoints:
/// <c>{ "status", "checks": [ { "name", "status", "durationMs", "data" } ] }</c>.
/// Only the listed data keys are written; a check's description and exception
/// are never written, so no credential reaches the body.
/// </summary>
public static class HealthResponseWriter
{
    /// <summary>The only data keys a health body may carry.</summary>
    public static readonly IReadOnlySet<string> AllowedDataKeys =
        new HashSet<string>(
            ["entries", "fingerprint", "address", "namespace", "queues", "provider", "writable", "readable", "expected", "applied", "error"],
            StringComparer.Ordinal);

    /// <summary>Writes the report as JSON; use as a health endpoint's response writer.</summary>
    public static Task Json(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(Render(report), Encoding.UTF8);
    }

    /// <summary>Renders the report as JSON text.</summary>
    public static string Render(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteStartArray("checks");
            foreach (var (name, entry) in report.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("status", entry.Status.ToString());
                writer.WriteNumber("durationMs", (long)entry.Duration.TotalMilliseconds);
                writer.WriteStartObject("data");
                foreach (var (key, value) in entry.Data.Where(d => AllowedDataKeys.Contains(d.Key)))
                {
                    writer.WritePropertyName(key);
                    JsonSerializer.Serialize(writer, value, value.GetType());
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
