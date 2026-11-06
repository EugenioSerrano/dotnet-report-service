using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ReportService.TemplateSource;

/// <summary>
/// The primary handler the source client is built with. Without an anchor it is
/// the plain handler: the platform's store and every platform default apply. With
/// one, the handler's TLS options carry a chain policy that trusts exactly the
/// supplied authorities — no more — and keeps every other check: the certificate's
/// name must still match the address, no callback replaces the validation, and no
/// client certificate is presented. The policy and its store are built per handler
/// and never shared, so the trust reaches this one client and nothing else.
/// </summary>
public static class TemplateSourceHttpHandler
{
    /// <summary>Builds the handler; with no certificate it is the plain handler.</summary>
    /// <param name="anchor">The authorities the source's certificate must chain to; empty for the platform store.</param>
    public static SocketsHttpHandler Create(IReadOnlyList<X509Certificate2> anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        var handler = new SocketsHttpHandler();
        if (anchor.Count == 0)
        {
            // The platform store and every platform default: this handler sets no TLS option of its own.
            return handler;
        }

        handler.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = ChainPolicy(anchor) };
        return handler;
    }

    /// <summary>Builds the chain policy that trusts exactly <paramref name="anchor"/>.</summary>
    /// <param name="anchor">The authorities the source's certificate must chain to.</param>
    public static X509ChainPolicy ChainPolicy(IReadOnlyList<X509Certificate2> anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,

            // The source is trusted through the deployment's own certificates and nothing else. Revocation is
            // not checked: a private authority reached by this worker publishes no list to consult, and a lookup
            // that cannot succeed would turn every read into an unavailable source. Replacing the file - and so
            // restarting the worker - is how a certificate is withdrawn here.
            RevocationMode = X509RevocationMode.NoCheck,
        };
        foreach (var certificate in anchor)
        {
            policy.CustomTrustStore.Add(certificate);
        }

        return policy;
    }
}
