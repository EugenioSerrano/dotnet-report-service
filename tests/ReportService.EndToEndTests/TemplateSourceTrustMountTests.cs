using System.Globalization;
using ReportService.AppHost;

namespace ReportService.EndToEndTests;

/// <summary>
/// The composition's forwarding rule for the source's trust anchor: nothing is
/// mounted and no setting is given when the deployment configured none — whether
/// the setting is absent, empty or blank — and a configured one is mounted at the
/// container's own path, with the host path in the form the container engine
/// accepts.
/// </summary>
public sealed class TemplateSourceTrustMountTests
{
    private const string ContainerAnchorPath = "/etc/report-service/template-source/trust-anchor.pem";
    private const string AnchorSetting = "TemplateSource__TrustAnchor";

    [Fact]
    public async Task NoConfiguredAnchorHasNothingToMount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await TemplateSourceTrustMount.DescribeAsync(null, cancellationToken));
        Assert.Null(await TemplateSourceTrustMount.DescribeAsync(string.Empty, cancellationToken));
        Assert.Null(await TemplateSourceTrustMount.DescribeAsync("   ", cancellationToken));
    }

    [Fact]
    public void TheContainerPathAndTheSettingAreTheOnesTheWorkerReads()
    {
        Assert.Equal(ContainerAnchorPath, TemplateSourceTrustMount.ContainerPath);
        Assert.Equal(AnchorSetting, TemplateSourceTrustMount.SettingName);
    }

    [Fact]
    public async Task AConfiguredAnchorIsMountedAtTheContainersPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var hostPath = Path.Combine(directory.Root, "trust-anchor.pem");
        await File.WriteAllTextAsync(hostPath, "the rule reads the path and never the file\n", cancellationToken);

        var mount = Assert.IsType<TemplateSourceTrustMount>(
            await TemplateSourceTrustMount.DescribeAsync(hostPath, cancellationToken));

        Assert.True(
            string.Equals(hostPath, mount.MountSource, StringComparison.Ordinal)
                || string.Equals(EngineForm(hostPath), mount.MountSource, StringComparison.Ordinal),
            $"the mount source '{mount.MountSource}' is neither the host path nor its engine form");
    }

    // The form a Windows drive path takes when the container engine runs in a WSL
    // distribution (D:\a\b -> /mnt/d/a/b); Docker Desktop translates it itself. Both
    // are the path the engine is given, and the rule may answer either.
    private static string EngineForm(string windowsPath) =>
        windowsPath.Length >= 3 && char.IsAsciiLetter(windowsPath[0]) && windowsPath[1] == ':' && windowsPath[2] is '\\' or '/'
            ? "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
            : windowsPath;

    /// <summary>A directory unique to one test, removed with it.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "report-service-trust-mount",
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
