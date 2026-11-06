using System.Security.Cryptography.X509Certificates;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The one handler the source client is configured with: without an anchor it is
/// the plain handler — the platform store and every platform default — and with one
/// its chain policy trusts exactly the supplied certificates, nothing else, and
/// keeps the hostname check, the revocation mode and the absence of a validation
/// callback explicit.
/// </summary>
public sealed class TemplateSourceHttpHandlerTests
{
    [Fact]
    public void NoAnchorConfiguresNoTlsOptionOfItsOwn()
    {
        using var handler = TemplateSourceHttpHandler.Create([]);
        using var plain = new SocketsHttpHandler();

        // The runtime hands out the TLS options object on first read, so "left alone"
        // is asserted where it decides anything: no chain policy, no validation
        // callback and no client certificate — the same as a handler nobody configured.
        Assert.Null(handler.SslOptions!.CertificateChainPolicy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.SslOptions.ClientCertificates is null || handler.SslOptions.ClientCertificates.Count == 0);
        Assert.Null(plain.SslOptions!.CertificateChainPolicy);
    }

    [Fact]
    public void OneAnchorIsTheWholeTrustStore()
    {
        using var authority = TestCertificateAuthority.Create();
        using var handler = TemplateSourceHttpHandler.Create([authority.Authority]);

        var options = Assert.IsType<System.Net.Security.SslClientAuthenticationOptions>(handler.SslOptions);
        var policy = Assert.IsType<X509ChainPolicy>(options.CertificateChainPolicy);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, policy.TrustMode);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(X509VerificationFlags.NoFlag, policy.VerificationFlags);
        Assert.Equal(
            [authority.Thumbprint],
            policy.CustomTrustStore.Cast<X509Certificate2>().Select(certificate => certificate.Thumbprint));
    }

    [Fact]
    public void TheHandlerCarriesNoValidationCallbackAndNoClientCertificate()
    {
        using var authority = TestCertificateAuthority.Create();
        using var handler = TemplateSourceHttpHandler.Create([authority.Authority]);

        var options = handler.SslOptions!;

        Assert.Null(options.RemoteCertificateValidationCallback);
        Assert.True(options.ClientCertificates is null || options.ClientCertificates.Count == 0);
    }

    [Fact]
    public void EveryCallBuildsItsOwnPolicyOverTheCertificatesItWasGiven()
    {
        using var authority = TestCertificateAuthority.Create();
        using var first = TemplateSourceHttpHandler.Create([authority.Authority]);
        using var second = TemplateSourceHttpHandler.Create([authority.Authority]);

        var firstPolicy = Assert.IsType<X509ChainPolicy>(first.SslOptions?.CertificateChainPolicy);
        var secondPolicy = Assert.IsType<X509ChainPolicy>(second.SslOptions?.CertificateChainPolicy);

        Assert.NotSame(firstPolicy, secondPolicy);
        Assert.NotSame(firstPolicy.CustomTrustStore, secondPolicy.CustomTrustStore);
        Assert.True(firstPolicy.CustomTrustStore[0].Equals(authority.Authority));
        Assert.True(secondPolicy.CustomTrustStore[0].Equals(authority.Authority));
    }

    [Fact]
    public void ThePolicyIsTheOneTheHandlerCarries()
    {
        using var authority = TestCertificateAuthority.Create();

        var policy = TemplateSourceHttpHandler.ChainPolicy([authority.Authority]);

        Assert.Equal(X509ChainTrustMode.CustomRootTrust, policy.TrustMode);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(authority.Thumbprint, Assert.Single(policy.CustomTrustStore.Cast<X509Certificate2>()).Thumbprint);
    }
}
