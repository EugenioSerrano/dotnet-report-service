using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplateSource;
using ReportService.Testing.TemplateSource;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The anchor on the take's own registration, through a host that is actually
/// started: an anchor that loads and no anchor both let the process start, and a
/// path that does not exist and an anchor beside an admitted plain-text loopback
/// both stop it with a configuration error naming <c>TemplateSource:TrustAnchor</c>
/// — before readiness, never at the first take.
/// </summary>
public sealed class TemplateSourceTrustRegistrationTests
{
    [Fact]
    public async Task AnAnchorThatLoadsStartsTheProcessAndHoldsItsCertificate()
    {
        using var directory = new TemporaryDirectory();
        using var authority = TestCertificateAuthority.Create();
        var path = authority.WritePem(directory.Root);
        using var host = Build(path);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var anchor = host.Services.GetRequiredService<TemplateSourceTrustAnchor>();
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(anchor.Problem);
        Assert.Equal(authority.Thumbprint, Assert.Single(anchor.Certificates).Thumbprint);
    }

    [Fact]
    public async Task NoAnchorStartsTheProcessOnThePlatformStore()
    {
        using var host = Build(null);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var anchor = host.Services.GetRequiredService<TemplateSourceTrustAnchor>();
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(anchor.Certificates);
        Assert.Null(anchor.Problem);
    }

    [Fact]
    public async Task APathThatDoesNotExistStopsTheProcessNamingTheSetting()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "absent.pem");
        using var host = Build(path);

        var error = await Assert.ThrowsAsync<ConfigurationError>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        var problem = Assert.Single(error.Problems, candidate => candidate.Setting == "TemplateSource:TrustAnchor");
        Assert.Equal("does not exist", problem.Reason);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnchorBesideAnAdmittedPlainTextLoopbackStopsTheProcessNamingTheSetting()
    {
        using var directory = new TemporaryDirectory();
        using var authority = TestCertificateAuthority.Create();
        var path = authority.WritePem(directory.Root);
        using var host = Build(path, apiBaseUrl: "http://127.0.0.1:5000/", allowInsecureLoopback: true);

        var error = await Assert.ThrowsAsync<ConfigurationError>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        var problem = Assert.Single(error.Problems, candidate => candidate.Setting == "TemplateSource:TrustAnchor");
        Assert.Equal(
            "is configured while the source is a plain-text loopback; the anchor would protect nothing",
            problem.Reason);
    }

    private static IHost Build(
        string? trustAnchor,
        string apiBaseUrl = "https://source.example/",
        bool allowInsecureLoopback = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TemplateSource:Repository"] = "report-service/templates",
            ["TemplateSource:Branch"] = "main",
            ["TemplateSource:ApiBaseUrl"] = apiBaseUrl,
            ["TemplateSource:AllowInsecureLoopback"] = allowInsecureLoopback ? "true" : "false",
            ["TemplateSource:TrustAnchor"] = trustAnchor,
            [DatabaseOptions.ConnectionStringSetting] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
            [DatabaseOptions.ExpectedSchemaVersionSetting] = "0001_initial.sql",
        });
        builder.Services.AddSingleton<IObjectStore>(new NoObjects());
        builder.AddMetadataDatabase();
        builder.AddTemplateTakeWork();
        return builder.Build();
    }

    /// <summary>A store these cases never reach; building the host validates the graph, which needs one.</summary>
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

    /// <summary>A directory unique to one test, removed with it.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "report-service-trust-registration",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A directory the process could not remove is not a test failure.
            }
        }
    }
}
