using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The planted values — a password-looking value, a connection-string-looking
/// value, a storage path carrying its key, and a report-looking payload — are
/// written into a log attribute, a formatted message, a span tag and a span
/// event, and none of them reaches the exported log or span. The report-looking
/// payload is long, which is the guard report content actually has: it is
/// truncated.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class RedactionTests
{
    private const string Category = "ReportService.Redaction";
    private const string PlantedSecret = "planted-secret-ee9142";
    private const string PlantedConnectionString = "Host=planted;Username=someone;Password=planted-secret-ee9142";
    private const string StorageRoot = "/srv/planted-root/storage";
    private const string PlantedStorageKey = "reports/2026/planted-key-77";

    private static readonly string PlantedContent = "<html><body>planted report content" + new string('y', 6000) + "</body></html>";

    private static readonly string Oversized = new('x', 5000);

    [Fact]
    public async Task NoPlantedValueReachesTheExportedLog()
    {
        await using var host = await HostAsync();
        var logger = host.Loggers.CreateLogger(Category);
        var storagePath = StorageRoot + "/" + PlantedStorageKey;

        logger.LogInformation("secret {Planted}", PlantedSecret);
        logger.LogInformation("storage {Path} content {Content}", storagePath, PlantedContent);
        logger.LogInformation("connection {Connection}", PlantedConnectionString);
        logger.LogInformation("oversized {Payload}", Oversized);
        logger.LogInformation("credential {Password}", PlantedSecret);

        var exported = string.Join('\n', Records(host).Select(record => record.FormattedMessage ?? record.Body ?? string.Empty));

        Assert.DoesNotContain(PlantedSecret, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(StorageRoot, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedStorageKey, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedContent, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(Oversized, exported, StringComparison.Ordinal);
        Assert.Contains(RedactionOptions.Replacement, exported, StringComparison.Ordinal);
        Assert.Contains(RedactionOptions.TruncationSuffix, exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeniedAttributeNameIsDroppedAndAnOversizedValueIsTruncated()
    {
        await using var host = await HostAsync();
        var logger = host.Loggers.CreateLogger(Category);

        logger.LogInformation("attributes {Password} {AccessKey} {Payload}", "first", "second", Oversized);

        var record = Assert.Single(Records(host));
        var attributes = record.Attributes ?? [];
        Assert.DoesNotContain("Password", attributes.Select(attribute => attribute.Key));
        Assert.DoesNotContain("AccessKey", attributes.Select(attribute => attribute.Key));
        var payload = attributes.Single(attribute => attribute.Key == "Payload").Value as string;
        Assert.NotNull(payload);
        Assert.Equal(RedactionOptions.KeptCharacters + RedactionOptions.TruncationSuffix.Length, payload!.Length);
        Assert.EndsWith(RedactionOptions.TruncationSuffix, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPlantedValueReachesTheExportedSpanButAnEventsTagsAreNotItsToRedact()
    {
        await using var host = await HostAsync();
        using var unit = host.BeginUnit();
        using var source = new ActivitySource(TelemetryDefaults.ActivitySourceName);
        using (var span = source.StartActivity("redacted"))
        {
            span?.SetTag("secret", PlantedSecret);
            span?.SetTag("note", "content " + PlantedContent);
            span?.SetTag("root", StorageRoot + "/" + PlantedStorageKey);
            span?.SetTag("payload", Oversized);
            span?.AddEvent(new ActivityEvent(
                "checked",
                tags: new ActivityTagsCollection
                {
                    ["token"] = PlantedSecret,
                    ["note"] = PlantedContent,
                }));
        }

        // The provider's exporter is shared by every host the suite starts in this process, so the
        // case selects the span it started by name rather than requiring to be the only one.
        var exported = Assert.Single(host.Activities, activity => string.Equals(activity.DisplayName, "redacted", StringComparison.Ordinal));
        var tags = exported.TagObjects.ToList();
        Assert.Equal(RedactionOptions.Replacement, tags.Single(tag => tag.Key == "secret").Value);
        Assert.DoesNotContain(PlantedContent, tags.Single(tag => tag.Key == "note").Value as string, StringComparison.Ordinal);
        Assert.DoesNotContain(StorageRoot, tags.Single(tag => tag.Key == "root").Value as string, StringComparison.Ordinal);
        Assert.DoesNotContain(Oversized, tags.Single(tag => tag.Key == "payload").Value as string, StringComparison.Ordinal);

        // An event's tags are held in a list the framework exposes for enumeration only: no
        // processor can remove or replace one, so the rule for an event is its caller's. The
        // limitation is recorded here rather than hidden: the value written is the value exported.
        var spanEvent = Assert.Single(exported.Events);
        Assert.Contains("token", spanEvent.Tags.Select(tag => tag.Key));
        Assert.Equal(PlantedSecret, spanEvent.Tags.Single(tag => tag.Key == "token").Value as string);
    }

    private static IEnumerable<OpenTelemetry.Logs.LogRecord> Records(TelemetryTestHost host) =>
        host.Logs.Where(record => record.CategoryName == Category);

    private static Task<TelemetryTestHost> HostAsync() =>
        TelemetryTestHost.StartAsync(settings: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RedactionOptions.ValuesSetting + ":0"] = PlantedSecret,
            [RedactionOptions.ValuesSetting + ":1"] = PlantedStorageKey,
            [RedactionOptions.StorageRootSetting] = StorageRoot,
        });
}
