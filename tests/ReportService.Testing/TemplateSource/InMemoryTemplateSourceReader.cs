using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using SourcePackageFile = ReportService.TemplateSource.Source.SourceFile;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The template source port held in memory, for the by-name resolution's cases:
/// each published version is seeded by name, a version can be published or
/// removed, and the listing can be made to fail as an unreachable class does.
/// Every call is logged, so a case can prove what the resolution asked for. The
/// source class's own read is tested against the real Filesystem provider; this
/// is the consumer's double (ADR-020 §3.6 item 1).
/// </summary>
public sealed class InMemoryTemplateSourceReader : ITemplateSourceReader
{
    private readonly Dictionary<string, List<string>> _published = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicates = new(StringComparer.Ordinal);
    private readonly List<SourceRequest> _requestLog = [];

    /// <summary>A syntactically valid git commit the resolver cases use for their records.</summary>
    private const string CommitShaText = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    /// <summary>The commit the records of this double's cases carry, as the old source exposed it.</summary>
    public string CommitSha => CommitShaText;

    /// <summary>How many requests reached this source; a read that never asks leaves it unchanged.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Every request, with the step and the template and version it named.</summary>
    public IReadOnlyList<SourceRequest> RequestLog => _requestLog;

    /// <summary>Whether the source answers nothing, as an unreachable one.</summary>
    public bool Unreachable { get; private set; }

    /// <summary>Records a version folder that holds no file, as the old source did.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public void SeedVersionFolder(string templateId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (!_published.TryGetValue(templateId, out var versions))
        {
            versions = [];
            _published[templateId] = versions;
        }

        if (!versions.Contains(version, StringComparer.Ordinal))
        {
            versions.Add(version);
        }
    }

    /// <summary>Records a template folder that holds no version folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    public void SeedTemplateFolder(string templateId) => _published.TryAdd(templateId, []);

    /// <summary>Publishes a new version, as an edit of the source: later listings see it.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public void Publish(string templateId, string version) => SeedVersionFolder(templateId, version);

    /// <summary>
    /// Lists the version twice, as a publisher that published one version text
    /// twice does: the enumeration reports both records and groups neither, so a
    /// caller that decides a repeated version is a duplicate meets the real shape.
    /// </summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version both records name.</param>
    public void SeedDuplicatePublication(string templateId, string version)
    {
        SeedVersionFolder(templateId, version);
        _duplicates.Add(templateId + "/" + version);
    }

    /// <summary>Removes a version, as a publisher removing its folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public void Remove(string templateId, string version)
    {
        if (_published.TryGetValue(templateId, out var versions))
        {
            versions.Remove(version);
        }
    }

    /// <summary>Answers nothing, or answers again.</summary>
    /// <param name="unreachable">Whether the source cannot be reached.</param>
    public void SetUnreachable(bool unreachable) => Unreachable = unreachable;

    /// <inheritdoc />
    public Task<SourceVersionRead> ReadVersionAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        Step("descriptor", templateId, version);
        var descriptorHash = DescriptorHash(templateId, version);
        var files = new[] { new SourcePackageFile("template.yaml", 1, new string('a', 64)) };
        return Task.FromResult<SourceVersionRead>(new SourceVersionRead.Found(new SourcePublication(
            templateId,
            version,
            TemplateSourceKeys.Descriptor(templateId, version, descriptorHash),
            descriptorHash,
            1,
            files,
            1,
            1)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PublishedVersion>> ListPublishedVersionsAsync(string templateId, CancellationToken cancellationToken)
    {
        Step("list", templateId, version: null);
        var names = _published.TryGetValue(templateId, out var versions) ? versions : new List<string>();
        var listed = new List<PublishedVersion>(names.Count);
        foreach (var name in names.OrderBy(name => name, StringComparer.Ordinal))
        {
            listed.Add(Listed(templateId, name, DescriptorHash(templateId, name)));
            if (_duplicates.Contains(templateId + "/" + name))
            {
                // The second record of one version text: a different name, so a
                // different hash, and the enumeration reports both.
                listed.Add(Listed(templateId, name, DescriptorHash(templateId, name + "#second")));
            }
        }

        IReadOnlyList<PublishedVersion> answer = listed;
        return Task.FromResult(answer);
    }

    private static PublishedVersion Listed(string templateId, string version, string descriptorHash) =>
        new(version, TemplateSourceKeys.Descriptor(templateId, version, descriptorHash), descriptorHash, 1);

    /// <inheritdoc />
    public Task OpenFileAsync(
        string templateId,
        string version,
        SourcePackageFile file,
        Func<Stream, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        Step("file", templateId, version);
        return consume(new MemoryStream(Encoding.UTF8.GetBytes("manifestVersion: \"1.0\""), writable: false), cancellationToken);
    }

    /// <summary>The descriptor hash a version of this double carries, lower-case hexadecimal.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public static string DescriptorHash(string templateId, string version) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(templateId + "/" + version)));

    private void Step(string name, string? templateId, string? version)
    {
        RequestCount++;
        _requestLog.Add(new SourceRequest(name, templateId, version));
        if (Unreachable)
        {
            throw new TemplateSourceException(TemplateSourceFailure.Unreachable, StorageTemplateSource.UnavailableReason);
        }
    }

    /// <summary>One request the source received, with what it named.</summary>
    /// <param name="Step">The step: <c>list</c>, <c>descriptor</c> or <c>file</c>.</param>
    /// <param name="TemplateId">The template the call named, or <see langword="null"/> when it named none.</param>
    /// <param name="Version">The version the call named, or <see langword="null"/> when it named none.</param>
    public sealed record SourceRequest(string Step, string? TemplateId, string? Version);
}
