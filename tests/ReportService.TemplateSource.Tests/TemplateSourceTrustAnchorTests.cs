using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.ServiceDefaults;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The one read of the trust anchor file: no setting holds no certificate, one or
/// more certificate blocks are read, a private key beside them is ignored, and each
/// refusal is a configuration problem on the setting — pinned to its exact reason,
/// and never carrying the path, a subject or the file's contents.
/// </summary>
public sealed class TemplateSourceTrustAnchorTests
{
    [Fact]
    public void NoConfiguredAnchorHoldsNoCertificateAndNoProblem()
    {
        var anchor = TemplateSourceTrustAnchor.Load(null, NullLogger.Instance);

        Assert.Empty(anchor.Certificates);
        Assert.Null(anchor.Problem);
    }

    [Fact]
    public void ABlankAnchorIsReadAsNothingToTrust()
    {
        var anchor = TemplateSourceTrustAnchor.Load("   ", NullLogger.Instance);

        Assert.Empty(anchor.Certificates);
        Assert.Null(anchor.Problem);
    }

    [Fact]
    public void OneCertificateBlockIsOneCertificate()
    {
        using var directory = new TemporaryDirectory();
        using var authority = TestCertificateAuthority.Create();
        var path = Write(directory, "one.pem", authority.Authority.ExportCertificatePem());

        var anchor = TemplateSourceTrustAnchor.Load(path, NullLogger.Instance);

        Assert.Null(anchor.Problem);
        var certificate = Assert.Single(anchor.Certificates);
        Assert.Equal(authority.Thumbprint, certificate.Thumbprint);
    }

    [Fact]
    public void EveryCertificateBlockIsRead()
    {
        using var directory = new TemporaryDirectory();
        using var first = TestCertificateAuthority.Create("first");
        using var second = TestCertificateAuthority.Create("second");
        var path = Write(
            directory,
            "two.pem",
            string.Join(Environment.NewLine, first.Authority.ExportCertificatePem(), second.Authority.ExportCertificatePem()));

        var anchor = TemplateSourceTrustAnchor.Load(path, NullLogger.Instance);

        Assert.Null(anchor.Problem);
        Assert.Equal(
            [first.Thumbprint, second.Thumbprint],
            anchor.Certificates.Select(certificate => certificate.Thumbprint));
    }

    [Fact]
    public void APrivateKeyBesideACertificateIsIgnored()
    {
        using var directory = new TemporaryDirectory();
        using var authority = TestCertificateAuthority.Create();
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var path = Write(
            directory,
            "certificate-and-key.pem",
            authority.Authority.ExportCertificatePem() + Environment.NewLine + key.ExportPkcs8PrivateKeyPem());

        var anchor = TemplateSourceTrustAnchor.Load(path, NullLogger.Instance);

        Assert.Null(anchor.Problem);
        Assert.Equal(authority.Thumbprint, Assert.Single(anchor.Certificates).Thumbprint);
    }

    [Fact]
    public void APathThatDoesNotExistIsRefused()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "absent", "trust-anchor.pem");

        var problem = Refused(path, "does not exist");

        Assert.DoesNotContain(path, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatIsADirectoryCannotBeRead()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "anchor-as-a-directory");
        Directory.CreateDirectory(path);

        var problem = Refused(path, "cannot be read");

        Assert.DoesNotContain(path, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfProseHoldsNoCertificate()
    {
        using var directory = new TemporaryDirectory();
        var path = Write(directory, "prose.pem", "this document holds no certificate block at all\n");

        var problem = Refused(path, "holds no certificate");

        Assert.DoesNotContain("holds no certificate block", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockThatIsNotACertificateIsRefusedAsMalformed()
    {
        using var directory = new TemporaryDirectory();
        var path = Write(
            directory,
            "malformed.pem",
            "-----BEGIN CERTIFICATE-----\nthis body is not a certificate\n-----END CERTIFICATE-----\n");

        Refused(path, "is not a readable PEM document");
    }

    [Fact]
    public void OneGoodBlockBesideOneBadBlockIsRefusedRatherThanHalfRead()
    {
        using var directory = new TemporaryDirectory();
        using var authority = TestCertificateAuthority.Create();
        var path = Write(
            directory,
            "half-readable.pem",
            string.Join(
                Environment.NewLine,
                authority.Authority.ExportCertificatePem(),
                "-----BEGIN CERTIFICATE-----\nthis body is not a certificate\n-----END CERTIFICATE-----\n"));

        var anchor = TemplateSourceTrustAnchor.Load(path, NullLogger.Instance);

        Assert.Empty(anchor.Certificates);
        Assert.Equal("is not a readable PEM document", anchor.Problem?.Reason);
    }

    private static ConfigurationProblem Refused(string path, string reason)
    {
        var anchor = TemplateSourceTrustAnchor.Load(path, NullLogger.Instance);

        Assert.Empty(anchor.Certificates);
        var problem = Assert.IsType<ConfigurationProblem>(anchor.Problem);
        Assert.Equal(nameof(TemplateSourceOptions.TrustAnchor), problem.Setting);
        Assert.Equal(reason, problem.Reason);
        return problem;
    }

    private static string Write(TemporaryDirectory directory, string name, string content)
    {
        var path = Path.Combine(directory.Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A directory unique to one test, removed with it.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "report-service-trust-anchor",
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
