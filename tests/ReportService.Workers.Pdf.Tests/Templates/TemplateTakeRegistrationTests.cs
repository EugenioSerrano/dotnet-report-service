using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Take;
using ReportService.Workers.Pdf.Templates;
using ReportService.Testing;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The take's registration on the queue this worker declares: one work
/// registration, one options object and one engine worker for
/// <c>report-pdf-rendering</c>, bounded to one activity at a time; the activity
/// resolvable from that registration; and a registration on a queue the process
/// does not declare stopping start-up by naming the queue. This is the first
/// registration on this queue, so it carries the queue's rule.
/// </summary>
public sealed class TemplateTakeRegistrationTests
{
    [Fact]
    public void TheTakeRegistersOnceOnTheQueueItDeclares()
    {
        using var host = Build(out var registrations);

        var work = Assert.Single(registrations, registration => registration.Queue == TakeTemplateVersionActivityOptions.Queue);
        var planned = Assert.Single(WorkerPlan.Plan([TakeTemplateVersionActivityOptions.Queue], [work]));
        Assert.Equal(TakeTemplateVersionActivityOptions.Queue, planned.Queue);
        Assert.Contains(planned.Options.Activities, activity => activity.Name == TakeTemplateVersionActivity.Name);
        Assert.NotNull(host.Services.GetRequiredService<TakeTemplateVersionActivity>());
    }

    [Fact]
    public void TheQueueRunsOneActivityAtATime()
    {
        using var host = Build(out var registrations);

        var planned = Assert.Single(WorkerPlan.Plan([TakeTemplateVersionActivityOptions.Queue], registrations));

        Assert.Equal(1, planned.Options.MaxConcurrentActivities);
    }

    [Fact]
    public void ARegistrationOnAnUndeclaredQueueStopsStartUpNamingIt()
    {
        using var host = Build(out var registrations);
        var work = Assert.Single(registrations, registration => registration.Queue == TakeTemplateVersionActivityOptions.Queue);
        var stray = work with { Queue = "report-undeclared" };

        var error = Assert.Throws<ConfigurationError>(() => WorkerPlan.Plan([TakeTemplateVersionActivityOptions.Queue], [stray]));

        Assert.Contains("report-undeclared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTakesExecutionSettingsAreTheOnesTheCallerIsGiven()
    {
        var options = TakeTemplateVersionActivityOptions.Create();

        Assert.Equal(TakeTemplateVersionActivityOptions.Queue, options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(300), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);
        Assert.Equal(
            ["TEMPLATE_NOT_FOUND", "TEMPLATE_INVALID", "UNSUPPORTED_CAPABILITY"],
            options.RetryPolicy.NonRetryableErrorTypes);
    }

    [Fact]
    public async Task AVersionWhoseOwnerIsAnotherConsumerReachesTheCallerAsNotFound()
    {
        await using var harness = new TakeHarness();
        harness.Seed("invoice-energy", "1.1.0", owner: "billing-system");
        var activity = new TakeTemplateVersionActivity(harness.Take());

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => ActivityAsync(activity, "invoice-energy", "1.1.0", "reporting-portal"));

        Assert.Equal(TemplateTakeCodes.TemplateNotFound, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    private static Task<TakeTemplateVersionResult> ActivityAsync(
        TakeTemplateVersionActivity activity,
        string templateId,
        string version,
        string consumer) =>
        new Temporalio.Testing.ActivityEnvironment().RunAsync(
            () => activity.RunAsync(new TakeTemplateVersionInput(templateId, version, consumer, "job-1")));

    private static IHost Build(out IReadOnlyList<TaskQueueWork> registrations)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TemplateSource:Repository"] = "report-service/templates",
            ["TemplateSource:Branch"] = "main",
            [DatabaseOptions.ConnectionStringSetting] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
            [DatabaseOptions.ExpectedSchemaVersionSetting] = "0000",
        });
        builder.Services.AddSingleton<IObjectStore>(new NoObjects());
        builder.AddValidatedConfiguration<TemplateSourceOptions>(TemplateSourceOptions.SectionPath);
        builder.AddMetadataDatabase();
        builder.AddTemplateTakeWork();
        var host = builder.Build();
        registrations = [.. host.Services.GetServices<TaskQueueWork>()];
        return host;
    }

    /// <summary>A store the registration cases never reach; resolving the graph needs one.</summary>
    private sealed class NoObjects : IObjectStore
    {
        public StorageCapabilities Capabilities => new(SignedUpload: false, SignedDownload: false, NativeCreateOnly: true, RangeReads: false);

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
