using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.Workers.Pdf.Rendering;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The composition the worker runs: the take's thirteen checks in their fixed order with
/// the record at the row bound last and the sample render before it, the sample renderer
/// the take's check resolves to the one implementation over the registered capability,
/// and the API project naming none of the take's, the engine's or the renderer's
/// assemblies. A wiring error is invisible to a hermetic suite, so it is asserted here
/// where the process is built.
/// </summary>
public sealed class SampleRegistrationTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void TheWorkerResolvesTheSampleRendererAndTheFourteenChecksInOrder()
    {
        using var host = Build();

        var renderer = host.Services.GetRequiredService<ISampleRenderer>();
        Assert.IsType<PdfSampleRenderer>(renderer);

        var checks = host.Services.GetServices<ITemplateVersionCheck>().ToArray();
        Assert.Equal(14, checks.Length);
        Assert.Equal(TemplateChecksRegistration.Order, checks.Select(check => check.Name));
        Assert.Equal(AtBoundSampleCheck.CheckName, checks[^1].Name);
        Assert.Equal(SampleRenderCheck.CheckName, checks[^2].Name);
    }

    [Fact]
    public void TheApiProjectReferencesNoneOfTheTakeEngineOrRenderingAssemblies()
    {
        var api = Path.Combine(Root, "src", "ReportService.Api", "ReportService.Api.csproj");
        Assert.True(File.Exists(api), api);

        var references = XDocument.Load(api).Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        foreach (var forbidden in new[]
        {
            "ReportService.TemplateChecks",
            "ReportService.Templates",
            "ReportService.Rendering",
            "ReportService.Pdf.Verification",
            "ReportService.Workers.Pdf",
        })
        {
            Assert.DoesNotContain(references, reference => reference.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static IHost Build()
    {
        var directory = Directory.CreateTempSubdirectory("sample-registration-");
        var storage = Path.Combine(directory.FullName, "storage");
        var source = Path.Combine(directory.FullName, "template-source");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(source);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Services.AddSingleton(new TemporaryTree(directory.FullName));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DatabaseOptions.ConnectionStringSetting] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
            [DatabaseOptions.ExpectedSchemaVersionSetting] = "0000",
            ["Storage:Provider"] = "filesystem",
            ["Storage:Filesystem:Root"] = storage,
            ["TemplateSource:Store:Filesystem:Root"] = source,
            ["TemplateSource:Store:Filesystem:CreateIfMissing"] = "true",
            ["Templates:Sample:RenderBudgetSeconds"] = "90",
        });
        builder.AddValidatedConfiguration<TemplateSourceOptions>(TemplateSourceOptions.SectionPath);
        builder.AddValidatedConfiguration<SampleRenderOptions>(SampleRenderOptions.SectionPath);
        builder.Services.AddSingleton(MeterIdentity.ForHost("reportservice-test"));
        builder.Services.AddSingleton<IObjectStore>(new NoObjects());
        builder.AddMetadataDatabase();
        builder.AddTemplateTakeWork();
        builder.AddPdfRenderingWork();

        // What the worker's own process does for the take's sample render.
        builder.Services.AddSingleton<SampleRenderMetrics>();
        builder.Services.AddSingleton<ISampleRenderer, PdfSampleRenderer>();
        return builder.Build();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>A directory the host removes when it is disposed.</summary>
    private sealed class TemporaryTree(string path) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
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
