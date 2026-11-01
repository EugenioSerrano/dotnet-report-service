using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The consumer registry on the running API: a valid change takes effect within
/// 5 s without a restart; an invalid change is refused, the last valid registry
/// stays in force, the service stays ready, and the refusal names the faulty
/// entries without any value.
/// </summary>
public sealed class ConsumerRegistryReloadTests : IDisposable
{
    private const string PlantedSecret = "planted-secret-3a77f0";
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan ChangeBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "report-service-e2e", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public ConsumerRegistryReloadTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ValidChangeAppliesWithoutRestartAndInvalidChangeIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, """
            { "consumers": [
                { "consumerId": "dev-consumer", "active": true },
                { "consumerId": "dev-consumer-disabled", "active": false } ] }
            """, cancellationToken);

        var appHost = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        var logs = new ConcurrentQueue<string>();
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = WatchLogsAsync(app, logs, watching.Token);
        using var api = app.CreateHttpClient("api");
        var pid = await ProcessIdAsync(app, cancellationToken);
        var fingerprintA = await FingerprintAsync(api, cancellationToken);

        // A valid change: a third consumer.
        await File.WriteAllTextAsync(registry, """
            { "consumers": [
                { "consumerId": "dev-consumer", "active": true },
                { "consumerId": "dev-consumer-disabled", "active": false },
                { "consumerId": "third-consumer", "active": true } ] }
            """, cancellationToken);
        var written = Stopwatch.StartNew();
        var fingerprintB = fingerprintA;
        await Wait.UntilAsync(
            "the registry change on the running API",
            async token => (fingerprintB = await FingerprintAsync(api, token)) != fingerprintA,
            ChangeBound,
            PollInterval,
            cancellationToken);
        var applied = written.Elapsed;

        Assert.NotEqual(fingerprintA, fingerprintB);
        Assert.True(applied <= ChangeBound, $"applied after {applied.TotalSeconds:F1} s");
        Assert.Equal(pid, await ProcessIdAsync(app, cancellationToken));

        // An invalid change: a duplicate identity and an entry without its active state,
        // each carrying a secret-looking value.
        await File.WriteAllTextAsync(registry, $$"""
            { "consumers": [
                { "consumerId": "dev-consumer", "active": true },
                { "consumerId": "dev-consumer", "active": true, "token": "{{PlantedSecret}}" },
                { "consumerId": "fourth-consumer", "password": "{{PlantedSecret}}" } ] }
            """, cancellationToken);
        await Wait.UntilAsync(
            "the refusal of the invalid registry change",
            _ => Task.FromResult(logs.Any(l => l.Contains("Consumer registry change refused", StringComparison.Ordinal))),
            ChangeBound,
            PollInterval,
            cancellationToken);

        Assert.Equal(fingerprintB, await FingerprintAsync(api, cancellationToken));
        using (var health = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", health.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(pid, await ProcessIdAsync(app, cancellationToken));
        // One write may be reported more than once by the file watcher; every refusal line is the same.
        var refusal = logs.First(l => l.Contains("Consumer registry change refused", StringComparison.Ordinal));
        Assert.Contains("consumers[1]: consumer 'dev-consumer' duplicates consumers[0]", refusal, StringComparison.Ordinal);
        Assert.Contains("consumers[2]: consumer 'fourth-consumer': active is required", refusal, StringComparison.Ordinal);

        // The absence of the planted value means something only over the lines the API wrote
        // while reading the invalid file: the warnings about the very entries that carry it.
        Assert.Contains(logs, l => l.Contains("consumers[1] (consumer 'dev-consumer'): unknown property 'token' is ignored", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("consumers[2] (consumer 'fourth-consumer'): unknown property 'password' is ignored", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains(PlantedSecret, StringComparison.Ordinal));

        await watching.CancelAsync();
        await watcher;
    }

    private static async Task<string> FingerprintAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health/registry", UriKind.Relative), cancellationToken));
        return body.RootElement.GetProperty("checks")[0].GetProperty("data").GetProperty("fingerprint").GetString()!;
    }

    // The process id the composition reports for a project resource; the hosting library's own
    // constant for this property name is not public.
    private const string ProcessIdProperty = "executable.pid";

    // The API's process id as the composition reports it; unchanged means not restarted.
    private static async Task<string> ProcessIdAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var resource = await app.ResourceNotifications.WaitForResourceAsync(
            "api",
            e => e.Snapshot.Properties.Any(p => p.Name == ProcessIdProperty && p.Value is not null),
            cancellationToken);
        return Convert.ToString(resource.Snapshot.Properties.Single(p => p.Name == ProcessIdProperty).Value, CultureInfo.InvariantCulture)!;
    }

    // The composition writes a project's output under the name of the instance it started
    // (the resource name plus a unique suffix), not under the bare resource name; watching
    // the resource itself follows every instance it has.
    private static async Task WatchLogsAsync(DistributedApplication app, ConcurrentQueue<string> logs, CancellationToken cancellationToken)
    {
        var service = app.Services.GetRequiredService<ResourceLoggerService>();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Single(r => r.Name == "api");
        try
        {
            await foreach (var batch in service.WatchAsync(resource).WithCancellation(cancellationToken))
            {
                foreach (var line in batch)
                {
                    logs.Enqueue(line.Content);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
