using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// A certificate authority generated for one measurement: its own certificate, a
/// server certificate it issues for a loopback listener, and the PEM file a
/// deployment would supply as the source's trust anchor. A fixed validity window
/// keeps a run reproducible; every key is generated in the process, kept only by
/// this object, and never written anywhere but the temporary directory a caller
/// names. Nothing is installed in a trust store.
/// </summary>
public sealed class TestCertificateAuthority : IDisposable
{
    /// <summary>The first instant a generated certificate is valid.</summary>
    public static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The last instant a generated certificate is valid.</summary>
    public static readonly DateTimeOffset NotAfter = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private TestCertificateAuthority(X509Certificate2 authority) => Authority = authority;

    /// <summary>The authority's own certificate, with the key it signs with.</summary>
    public X509Certificate2 Authority { get; }

    /// <summary>The authority's thumbprint, as a trust store would list it.</summary>
    public string Thumbprint => Authority.Thumbprint;

    /// <summary>Generates an authority of its own, valid over the fixed window.</summary>
    /// <param name="name">The common name the authority's subject carries.</param>
    public static TestCertificateAuthority Create(string name = "report-service test template source")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name} authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var selfSigned = request.CreateSelfSigned(NotBefore, NotAfter);
        return new TestCertificateAuthority(Persisted(selfSigned, X509KeyStorageFlags.EphemeralKeySet));
    }

    /// <summary>
    /// Issues the certificate a loopback listener serves: valid for
    /// <c>127.0.0.1</c> and <c>localhost</c>, for server authentication only,
    /// signed by this authority.
    /// </summary>
    public X509Certificate2 IssueSourceCertificate(string commonName = "127.0.0.1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] = (byte)((serial[0] & 0x7e) | 0x01);
        using var issued = request.Create(Authority, NotBefore.AddDays(1), NotAfter.AddDays(-1), serial);
        using var withKey = issued.CopyWithPrivateKey(key);

        // A server certificate's key must be one the platform's TLS implementation can
        // acquire credentials from: Windows refuses an ephemeral key in server mode. The
        // key container is the user's, and it goes away with the certificate.
        return Persisted(withKey, X509KeyStorageFlags.DefaultKeySet);
    }

    /// <summary>Writes this authority's own certificate as the PEM file a deployment supplies.</summary>
    /// <param name="directory">The directory the file is written into.</param>
    /// <param name="fileName">The file's name.</param>
    public string WritePem(string directory, string fileName = "trust-anchor.pem")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, Authority.ExportCertificatePem());
        return path;
    }

    /// <inheritdoc />
    public void Dispose() => Authority.Dispose();

    // A certificate whose private key a TLS implementation can use. An authority is only
    // ever used to sign, so its key stays in memory; a server certificate needs the key
    // flags its platform accepts (see IssueSourceCertificate).
    private static X509Certificate2 Persisted(X509Certificate2 certificate, X509KeyStorageFlags keyStorageFlags) =>
        X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags);
}
