using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Resolution;
using ReportService.Testing;
using ReportService.Workers.Pdf.Templates;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The by-name resolution's registration on the queue the worker declares: the
/// activity joins the worker the take already serves — one engine worker for
/// <c>report-pdf-rendering</c>, bounded to one activity at a time — the adapter
/// over the real take is what the resolver receives as its seam, and the
/// execution settings are the ones a caller is given. A queue this process does
/// not declare still stops start-up by naming the queue.
/// </summary>
public sealed class TemplateResolutionRegistrationTests
{
    [Fact]
    public void TheResolutionJoinsTheWorkerTheTakeServes()
    {
        using var host = Build(out var registrations);
        var queue = TemplateResolutionActivityOptions.Queue;

        var planned = Assert.Single(WorkerPlan.Plan([queue], registrations));
        Assert.Equal(queue, planned.Queue);
        Assert.Contains(planned.Options.Activities, activity => activity.Name == ResolveTemplateVersionActivity.Name);
        Assert.Contains(planned.Options.Activities, activity => activity.Name == TakeTemplateVersionActivity.Name);
        Assert.NotNull(host.Services.GetRequiredService<ResolveTemplateVersionActivity>());
    }

    [Fact]
    public void ASingleWorkerServesTheQueueAndAtOneActivityAtATime()
    {
        using var host = Build(out var registrations);
        var queue = TemplateResolutionActivityOptions.Queue;

        var planned = Assert.Single(WorkerPlan.Plan([queue], registrations));

        Assert.Equal(1, planned.Options.MaxConcurrentActivities);
        Assert.Equal(TakeTemplateVersionActivityOptions.Queue, TemplateResolutionActivityOptions.Queue);
    }

    [Fact]
    public void TheResolverReceivesTheAdapterOverTheRealTake()
    {
        using var host = Build(out _);

        var seam = host.Services.GetRequiredService<ITemplateTake>();

        Assert.IsType<TemplateTakeAdapter>(seam);
        Assert.NotNull(host.Services.GetRequiredService<TemplateVersionResolver>());
    }

    [Fact]
    public void ARegistrationOnAnUndeclaredQueueStopsStartUpNamingIt()
    {
        using var host = Build(out var registrations);
        var work = Assert.Single(registrations, registration => Serves(registration, ResolveTemplateVersionActivity.Name));
        var stray = work with { Queue = "report-undeclared" };

        var error = Assert.Throws<ConfigurationError>(() => WorkerPlan.Plan([TemplateResolutionActivityOptions.Queue], [stray]));

        Assert.Contains("report-undeclared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolutionsExecutionSettingsAreTheOnesTheCallerIsGiven()
    {
        var options = TemplateResolutionActivityOptions.Create();

        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(300), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);

        // The two codes a refusal can carry that are deterministic for a given branch
        // state; an unsupported capability is a candidate's refusal and never a job's code.
        Assert.NotNull(options.RetryPolicy.NonRetryableErrorTypes);
        Assert.Equal(
            ["TEMPLATE_NOT_FOUND", "TEMPLATE_INVALID"],
            options.RetryPolicy.NonRetryableErrorTypes!);
        Assert.DoesNotContain("UNSUPPORTED_CAPABILITY", options.RetryPolicy.NonRetryableErrorTypes!);
    }

    [Fact]
    public void TheResolutionKeepsItsBeatWhileItRuns()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), TemplateResolutionActivityOptions.HeartbeatEvery);
        Assert.True(TemplateResolutionActivityOptions.HeartbeatEvery < TemplateResolutionActivityOptions.HeartbeatTimeout);
    }

    private static bool Serves(TaskQueueWork registration, string activityName)
    {
        var options = new Temporalio.Worker.TemporalWorkerOptions(registration.Queue);
        registration.Configure(options);
        return options.Activities.Any(activity => activity.Name == activityName);
    }

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
        builder.AddTemplateResolutionWork();
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
