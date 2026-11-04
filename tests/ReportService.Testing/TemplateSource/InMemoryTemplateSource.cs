using System.Globalization;
using System.Text;
using ReportService.TemplateSource;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// A template source held in memory: versions are seeded, a branch edit
/// replaces or removes a version's folder and moves the commit, a template can
/// be made absent or too large to list, and a read either answers from memory or
/// fails as an unreachable or a misconfigured source. Every request is logged
/// with the step and the template and version it named, so a case can prove
/// which version a read addressed and which it never addressed; the steps a read
/// went through are kept, and a hook runs between them, so a test can pause one
/// read while another finishes.
/// </summary>
public sealed class InMemoryTemplateSource : ITemplateSource
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _templateFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _templateFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _absentTemplates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tooLargeTemplates = new(StringComparer.Ordinal);
    private readonly List<SourceRequest> _requestLog = [];
    private readonly List<string> _steps = [];
    private long _generation = 1;

    /// <summary>Every step a read went through, in order, across all reads.</summary>
    public IReadOnlyList<string> Steps => _steps;

    /// <summary>Every request, with the step and the template and version it named.</summary>
    public IReadOnlyList<SourceRequest> RequestLog => _requestLog;

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
        ThrowIfAbsent(templateId);
        Package(templateId, version).Clear();
        foreach (var file in files)
        {
            Package(templateId, version)[file.Key] = file.Value;
        }

        _templateFolders.Add(templateId);
    }

    /// <summary>Records a version folder that holds no file, as a branch may.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version folder's name.</param>
    public void SeedVersionFolder(string templateId, string version)
    {
        ThrowIfAbsent(templateId);
        Package(templateId, version);
        _templateFolders.Add(templateId);
    }

    /// <summary>Records a template folder that holds no version folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    public void SeedTemplateFolder(string templateId)
    {
        ThrowIfAbsent(templateId);
        _templateFolders.Add(templateId);
    }

    /// <summary>Publishes a new version folder, as an edit of the branch: the commit moves.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version folder's name.</param>
    public void Publish(string templateId, string version)
    {
        SeedVersionFolder(templateId, version);
        _generation++;
    }

    /// <summary>Records a file directly under the template folder, beside the version folders.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="name">The file's name.</param>
    public void SeedFileInTemplateFolder(string templateId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfAbsent(templateId);
        if (!_templateFiles.TryGetValue(templateId, out var files))
        {
            files = new HashSet<string>(StringComparer.Ordinal);
            _templateFiles[templateId] = files;
        }

        files.Add(name);
        _templateFolders.Add(templateId);
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

    /// <summary>Removes a version's folder, as a branch edit: the commit moves, the version is gone.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public void Remove(string templateId, string version)
    {
        if (_packages.Remove(Key(templateId, version)))
        {
            _generation++;
        }
    }

    /// <summary>Answers the template folder as absent, or answers it again.</summary>
    /// <param name="templateId">The template's identifier.</param>
    public void SetTemplateAbsent(string templateId)
    {
        if (_absentTemplates.Add(templateId))
        {
            _generation++;
        }
    }

    /// <summary>Answers the template folder's listing as truncated, as a source that could not list it whole.</summary>
    /// <param name="templateId">The template's identifier.</param>
    public void SetListingTooLarge(string templateId) => _tooLargeTemplates.Add(templateId);

    /// <summary>Answers nothing, or answers again.</summary>
    /// <param name="unreachable">Whether the source cannot be reached.</param>
    public void SetUnreachable(bool unreachable) => Unreachable = unreachable;

    /// <summary>Answers that the repository or the credential is not usable, or answers again.</summary>
    /// <param name="misconfigured">Whether the source is misconfigured.</param>
    public void SetMisconfigured(bool misconfigured) => Misconfigured = misconfigured;

    /// <inheritdoc />
    public Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken)
    {
        Step("commit", templateId: null, version: null);
        return Task.FromResult(new SourceCommit(CommitSha, CommitSha));
    }

    /// <inheritdoc />
    public Task<SourceFolder?> FindVersionFolderAsync(SourceCommit commit, string templateId, string version, CancellationToken cancellationToken)
    {
        Step("tree", templateId, version);
        return Task.FromResult(
            !_absentTemplates.Contains(templateId) && _packages.ContainsKey(Key(templateId, version))
                ? new SourceFolder(FolderSha(templateId, version))
                : null);
    }

    /// <inheritdoc />
    public Task<SourceTemplateVersions?> ListVersionsAsync(SourceCommit commit, string templateId, CancellationToken cancellationToken)
    {
        Step("tree", templateId, version: null);
        if (_absentTemplates.Contains(templateId))
        {
            return Task.FromResult<SourceTemplateVersions?>(null);
        }

        if (_tooLargeTemplates.Contains(templateId))
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.PackageTooLarge,
                $"The template folder 'templates/{templateId}/' holds more entries than the source will list in one answer.");
        }

        if (!_templateFolders.Contains(templateId))
        {
            return Task.FromResult<SourceTemplateVersions?>(null);
        }

        string[] names =
        [
            .. _packages.Keys
                .Where(key => key.StartsWith(templateId + "/", StringComparison.Ordinal))
                .Select(key => key[(templateId.Length + 1)..])
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
        return Task.FromResult<SourceTemplateVersions?>(new SourceTemplateVersions(names));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken)
    {
        Step("list", templateId: null, version: null);
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
        Step("blob", templateId: null, version: null);
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

    private void ThrowIfAbsent(string templateId)
    {
        if (_absentTemplates.Contains(templateId))
        {
            throw new InvalidOperationException($"the template '{templateId}' was made absent");
        }
    }

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

    private void Step(string name, string? templateId, string? version)
    {
        RequestCount++;
        _steps.Add(name);
        _requestLog.Add(new SourceRequest(name, templateId, version));
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

    /// <summary>One request the source received, with what it named.</summary>
    /// <param name="Step">The step: <c>commit</c>, <c>tree</c>, <c>list</c> or <c>blob</c>.</param>
    /// <param name="TemplateId">The template the call named, or <see langword="null"/> when it named none.</param>
    /// <param name="Version">The version the call named, or <see langword="null"/> when it named none.</param>
    public sealed record SourceRequest(string Step, string? TemplateId, string? Version);
}
