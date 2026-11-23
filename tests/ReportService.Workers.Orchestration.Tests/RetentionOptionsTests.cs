using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage.Filesystem;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Retention;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The output retention the worker runs under: required, greater than zero and at most 3650
/// days, refused at start by a process built from the worker's own registrations — the same
/// container the process builds before it serves anything — naming <c>Retention:Outputs</c> and
/// never the value it read. A value of seven days starts, and it is the value the publication
/// then records on every artifact.
/// </summary>
public sealed class RetentionOptionsTests
{
    /// <summary>The value the local composition supplies, and the one the development profile does not.</summary>
    private const string SevenDays = "7.00:00:00";

    [Fact]
    public void SevenDaysIsAValidOutputRetention()
    {
        Assert.Empty(new RetentionOptions { Outputs = TimeSpan.FromDays(7) }.Validate());
    }

    [Fact]
    public void TheLongestAcceptedRetentionIs3650Days()
    {
        Assert.Empty(new RetentionOptions { Outputs = TimeSpan.FromDays(3650) }.Validate());

        var problem = Assert.Single(new RetentionOptions { Outputs = TimeSpan.FromDays(3650) + TimeSpan.FromTicks(1) }.Validate());
        Assert.Equal(RetentionOptions.OutputsSetting, problem.Setting);
        Assert.Contains("3650 days or less", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("00:00:00.0000001", problem.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARetentionThatIsNotPositiveIsRefusedByName(int days)
    {
        var problem = Assert.Single(new RetentionOptions { Outputs = TimeSpan.FromDays(days) }.Validate());

        Assert.Equal(RetentionOptions.OutputsSetting, problem.Setting);
        Assert.DoesNotContain("00:00:00", problem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("-1.00:00:00", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkerRefusesToStartWithoutAUsableOutputRetention()
    {
        foreach (var value in new[] { null, "00:00:00", "-00:00:01", "3651.00:00:00" })
        {
            var refusal = Start(value);

            // The setting is named, the rule is stated, and the value read never leaves the process.
            Assert.Equal($"{RetentionOptions.SectionPath}:{RetentionOptions.OutputsSetting}", refusal.Setting);
            Assert.StartsWith(
                $"setting '{RetentionOptions.SectionPath}:{RetentionOptions.OutputsSetting}': ",
                refusal.Message,
                StringComparison.Ordinal);
            if (value is not null)
            {
                Assert.DoesNotContain(value, refusal.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheWorkerStartsWithSevenDaysAndPublishesThatRetention()
    {
        using var root = TemporaryRoot();
        using var host = Host(root.FullName, SevenDays);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<IOptions<RetentionOptions>>().Value;
        Assert.Equal(TimeSpan.FromDays(7), options.Outputs);
        Assert.Empty(options.Validate());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts the worker's own host with <paramref name="outputs"/> and answers its refusal.</summary>
    private static ConfigurationError Start(string? outputs)
    {
        using var root = TemporaryRoot();
        using var host = Host(root.FullName, outputs);
        var raised = Record.Exception(() => host.StartAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        return Assert.IsType<ConfigurationError>(FindConfigurationError(raised));
    }

    // The host the process builds, with the settings the worker needs and the case's retention.
    private static IHost Host(string storageRoot, string? outputs)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DatabaseOptions.ConnectionStringSetting] = "Host=127.0.0.1;Port=5432;Database=report_service;Username=report_service;Password=unused",
            [DatabaseOptions.ExpectedSchemaVersionSetting] = "0003_upload-content-snapshots-and-intents.sql",
            [$"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}"] = "filesystem",
            [$"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.Root)}"] = storageRoot,
            [$"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.CreateIfMissing)}"] = "true",
            [$"{WorkflowEngineOptions.SectionPath}:{nameof(WorkflowEngineOptions.Address)}"] = "127.0.0.1:7233",
            [$"{VerificationOptions.SectionPath}:{VerificationOptions.MaxStoredBytesSetting}"] = "4294967296",
            [$"{VerificationOptions.SectionPath}:{VerificationOptions.MaxUncompressedBytesSetting}"] = "4294967296",
            [$"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}"] = "01:00:00",
            [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.PollIntervalSetting}"] = "00:00:01",
            [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.BatchSizeSetting}"] = "50",
            [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.AlertAfterSetting}"] = "00:05:00",
            [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.ClaimBudgetSetting}"] = "00:00:30",
            [$"{WorkerOptions.SectionPath}:{WorkerOptions.DataActivitySlotsSetting}"] = "2",
        };
        if (outputs is not null)
        {
            settings[$"{RetentionOptions.SectionPath}:{RetentionOptions.OutputsSetting}"] = outputs;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        OrchestrationWorkerComposition.Configure(builder);
        return builder.Build();
    }

    // The refusal may arrive wrapped by the host's own lifetime handling: the first configuration
    // error of the chain is the one the start gave.
    private static ConfigurationError? FindConfigurationError(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }

            if (current is AggregateException aggregate)
            {
                var found = aggregate.InnerExceptions.Select(FindConfigurationError).FirstOrDefault(error => error is not null);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static Root TemporaryRoot() => new();

    /// <summary>A directory of the case's own, removed when the case ends.</summary>
    private sealed class Root : IDisposable
    {
        public Root()
        {
            FullName = Directory.CreateTempSubdirectory("retention-options-").FullName;
        }

        public string FullName { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }
}
