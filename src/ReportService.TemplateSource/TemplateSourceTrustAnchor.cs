using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using ReportService.ServiceDefaults;

namespace ReportService.TemplateSource;

/// <summary>
/// The source's trust anchor, read once for the life of the process: the public
/// certificates of the authorities a source's certificate must chain to. The file
/// is a PEM document holding one or more certificates; a private key beside them is
/// ignored, because a client authenticates a server and presents no certificate of
/// its own. Every refusal is a configuration problem on the setting and names no
/// path, no subject and no content, so a startup failure can never carry what the
/// file holds.
/// </summary>
public sealed partial class TemplateSourceTrustAnchor
{
    /// <summary>The log category of the anchor.</summary>
    public const string LogCategory = "ReportService.TemplateSource.TrustAnchor";

    private const string CertificateBegin = "-----BEGIN CERTIFICATE-----";
    private const string DoesNotExist = "does not exist";
    private const string CannotBeRead = "cannot be read";
    private const string HoldsNoCertificate = "holds no certificate";
    private const string NotReadable = "is not a readable PEM document";

    private readonly ILogger _logger;

    private TemplateSourceTrustAnchor(
        ILogger logger,
        IReadOnlyList<X509Certificate2> certificates,
        ConfigurationProblem? problem)
    {
        _logger = logger;
        Certificates = certificates;
        Problem = problem;
    }

    /// <summary>The certificates the source must chain to; empty when no anchor is configured.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; }

    /// <summary>The one problem of a refused file, on <c>TrustAnchor</c>; null when it was read.</summary>
    public ConfigurationProblem? Problem { get; }

    /// <summary>
    /// Reads the anchor at <paramref name="path"/> once. An absent or blank path holds
    /// no certificate and no problem; a document with one or more certificate blocks
    /// holds each of them; a document that claims a certificate and does not yield one
    /// — missing, unreadable, empty of certificates or malformed, in whole or in part —
    /// is refused with the one problem its condition has. It never throws.
    /// </summary>
    /// <param name="path">The configured file, or nothing.</param>
    /// <param name="logger">The log of the process that reads it.</param>
    public static TemplateSourceTrustAnchor Load(string? path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new TemplateSourceTrustAnchor(logger, [], null);
        }

        if (!File.Exists(path))
        {
            return Refused(logger, Directory.Exists(path) ? CannotBeRead : DoesNotExist);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return Refused(logger, CannotBeRead);
        }
        catch (UnauthorizedAccessException)
        {
            return Refused(logger, CannotBeRead);
        }

        var claimed = Occurrences(text, CertificateBegin);
        if (claimed == 0)
        {
            return Refused(logger, HoldsNoCertificate);
        }

        var certificates = new List<X509Certificate2>(claimed);
        var offset = 0;
        while (offset < text.Length && PemEncoding.TryFind(text.AsSpan(offset), out var fields))
        {
            var blockStart = offset + fields.Location.Start.Value;
            var blockEnd = offset + fields.Location.End.Value;
            var block = text.AsSpan(blockStart, blockEnd - blockStart);
            if (block.StartsWith(CertificateBegin, StringComparison.Ordinal))
            {
                try
                {
                    certificates.Add(X509Certificate2.CreateFromPem(block));
                }
                catch (CryptographicException)
                {
                    return Refused(logger, NotReadable);
                }
            }

            offset = blockEnd;
        }

        // A document that claims more certificate blocks than it yields is not read
        // in part: a half-read anchor would be a trust decision nobody made.
        if (certificates.Count < claimed)
        {
            return Refused(logger, NotReadable);
        }

        var anchor = new TemplateSourceTrustAnchor(logger, certificates, null);
        anchor.LogAnchorConfigured(certificates.Count, nameof(TemplateSourceOptions.TrustAnchor));
        return anchor;
    }

    private static TemplateSourceTrustAnchor Refused(ILogger logger, string reason) =>
        new(logger, [], new ConfigurationProblem(nameof(TemplateSourceOptions.TrustAnchor), reason));

    private static int Occurrences(string text, string marker)
    {
        var count = 0;
        for (var index = text.IndexOf(marker, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "the template source trusts {CertificateCount} supplied certificate(s) from {Setting}")]
    private partial void LogAnchorConfigured(int certificateCount, string setting);
}
