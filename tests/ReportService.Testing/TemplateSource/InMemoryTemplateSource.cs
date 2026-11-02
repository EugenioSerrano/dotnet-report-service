using System.Globalization;
using System.Text;
using ReportService.TemplateSource;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// A template source held in memory: versions are seeded, a branch edit
/// replaces a version's files and moves the commit, and a read either answers
/// from memory or fails as an unreachable or a misconfigured source. The steps
/// a read went through are kept, and a hook runs between them, so a test can
/// pause one read while another finishes.
/// </summary>
public sealed class InMemoryTemplateSource : ITemplateSource
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _packages = new(StringComparer.Ordinal);
    private readonly List<string> _steps = [];
    private long _generation = 1;

    /// <summary>Every step a read went through, in order, across all reads.</summary>
    public IReadOnlyList<string> Steps => _steps;

    /// <summary>How many requests reached this source; a read that never asks leaves it unchanged.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Whether the source answers nothing, as an unreachable one.</summary>
    public bool Unreachable { get; private set; }

    /// <summary>Whether the source answers that the repository or credential is not usable.</summary>
    public bool Misconfigured { get; private set; }

    /// <summary>Runs before the named step, so a test can let another read finish first.</summary>
    public Action<string>? BeforeStep { get; set; }

    /// <summary>The commit the branch points at.</summary>
    public string CommitSha =>
        GitBlobs.Sha1Hex(Encoding.ASCII.GetBytes("commit " + _generation.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Records a package. Its blob names are the git names of its bytes.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="files">The package's files, by package-relative path.</param>
    public void SeedPackage(string templateId, string version, IReadOnlyDictionary<string, byte[]> files)
    {
        Package(templateId, version).Clear();
        foreach (var file in files)
        {
            Package(templateId, version)[file.Key] = file.Value;
        }
    }

    /// <summary>Replaces a package's files, as a change in the branch: the commit moves, the version does not.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="files">The package's new files.</param>
    public void Replace(string templateId, string version, IReadOnlyDictionary<string, byte[]> files)
    {
        SeedPackage(templateId, version, files);
        _generation++;
    }

    /// <summary>Answers nothing, or answers again.</summary>
    /// <param name="unreachable">Whether the source cannot be reached.</param>
    public void SetUnreachable(bool unreachable) => Unreachable = unreachable;

    /// <summary>Answers that the repository or the credential is not usable, or answers again.</summary>
    /// <param name="misconfigured">Whether the source is misconfigured.</param>
    public void SetMisconfigured(bool misconfigured) => Misconfigured = misconfigured;

    /// <inheritdoc />
    public Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken)
    {
        Step("commit");
        return Task.FromResult(new SourceCommit(CommitSha, CommitSha));
    }

    /// <inheritdoc />
    public Task<SourceFolder?> FindVersionFolderAsync(SourceCommit commit, string templateId, string version, CancellationToken cancellationToken)
    {
        Step("tree");
        return Task.FromResult(_packages.ContainsKey(Key(templateId, version)) ? new SourceFolder(FolderSha(templateId, version)) : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken)
    {
        Step("list");
        var package = _packages.Values.FirstOrDefault(files => string.Equals(FolderSha(files), folder.TreeSha, StringComparison.Ordinal));
        IReadOnlyList<SourceFile> files = package is null
            ? []
            : [.. package.OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new SourceFile(file.Key, GitBlobs.BlobSha(file.Value), file.Value.Length))];
        return Task.FromResult(files);
    }

    /// <inheritdoc />
    public async Task OpenFileAsync(SourceFile file, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(consume);
        Step("blob");
        var bytes = _packages.Values
            .SelectMany(package => package)
            .Where(entry => string.Equals(GitBlobs.BlobSha(entry.Value), file.BlobSha, StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .FirstOrDefault() ?? [];
        await using var content = new MemoryStream(bytes, writable: false);
        await consume(content, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(string templateId, string version) => templateId + "/" + version;

    private static string FolderSha(Dictionary<string, byte[]> files) =>
        GitBlobs.TreeSha(files
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => ("100644", file.Key, GitBlobs.BlobSha(file.Value))));

    private string FolderSha(string templateId, string version) => FolderSha(Package(templateId, version));

    private Dictionary<string, byte[]> Package(string templateId, string version)
    {
        var key = Key(templateId, version);
        if (!_packages.TryGetValue(key, out var files))
        {
            files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _packages[key] = files;
        }

        return files;
    }

    private void Step(string name)
    {
        RequestCount++;
        _steps.Add(name);
        BeforeStep?.Invoke(name);
        if (Unreachable)
        {
            throw new TemplateSourceException(TemplateSourceFailure.Unreachable, "The template source could not be reached. Retry later.");
        }

        if (Misconfigured)
        {
            throw new TemplateSourceException(TemplateSourceFailure.Misconfigured, "The template source does not serve the configured repository and branch.");
        }
    }
}
