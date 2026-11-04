using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// A template source that answers the four requests the client makes, in the
/// source's documented shapes and with real git object names, without a socket:
/// it is an <see cref="HttpMessageHandler"/>, so the client's own request
/// building, header handling, body bounding and failure classification are the
/// code under test. Every request is kept, so a test can prove what was asked —
/// and that nothing was asked at all.
/// </summary>
public sealed class GitHubTemplateSourceStub : HttpMessageHandler
{
    /// <summary>The owner every stub request names.</summary>
    public const string Owner = "report-service-tests";

    /// <summary>The repository every stub request names.</summary>
    public const string Repository = "template-source";

    /// <summary>The branch every stub request names.</summary>
    public const string Branch = "main";

    /// <summary>The base URL the client must be configured with.</summary>
    public const string BaseUrl = "https://api.github.test/";

    private readonly StubDirectory _root = new(parent: null);
    private readonly List<StubRequest> _requests = [];
    private readonly Dictionary<string, byte[]> _blobOverrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _truncatedTemplates = new(StringComparer.Ordinal);

    /// <summary>Blobs a replaced package no longer lists; a real repository keeps them reachable by hash.</summary>
    private readonly Dictionary<string, byte[]> _retiredBlobs = new(StringComparer.Ordinal);
    private long _generation = 1;
    private bool _stopped;
    private bool _truncated;
    private HttpStatusCode? _status;
    private bool _rateLimited;

    /// <summary>The commit the branch points at; it changes when a package is replaced.</summary>
    public string CommitSha =>
        GitBlobs.Sha1Hex(Encoding.ASCII.GetBytes("commit " + _generation.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Every request the client made, in order.</summary>
    public IReadOnlyList<StubRequest> Requests => new ReadOnlyCollection<StubRequest>(_requests);

    /// <summary>How many requests the client made.</summary>
    public int RequestCount => _requests.Count;

    /// <summary>The tree of the repository's root, which the commit request answers with.</summary>
    public string RootTreeSha => _root.Sha;

    /// <summary>Records a package under <c>templates/&lt;templateId&gt;/&lt;version&gt;/</c>.</summary>
    public void SeedPackage(string templateId, string version, IReadOnlyDictionary<string, byte[]> files) =>
        Add(["templates", templateId, version], files);

    /// <summary>Records a version folder that holds no file, under <c>templates/&lt;templateId&gt;/&lt;version&gt;/</c>.</summary>
    public void SeedVersionFolder(string templateId, string version) =>
        Add(["templates", templateId, version], new Dictionary<string, byte[]>());

    /// <summary>Records a template folder that holds no version folder.</summary>
    public void SeedTemplateFolder(string templateId) =>
        Add(["templates", templateId], new Dictionary<string, byte[]>());

    /// <summary>Records a file directly under the template folder, beside the version folders.</summary>
    public void SeedTemplateFile(string templateId, string name) =>
        Add(["templates", templateId], new Dictionary<string, byte[]> { [name] = Encoding.UTF8.GetBytes(name) });

    /// <summary>Answers the named template folder's tree as truncated.</summary>
    public void SetTemplateFolderTruncated(string templateId) => _truncatedTemplates.Add(templateId);

    /// <summary>Replaces a package's files, as a change in the branch: the commit moves, the version does not.</summary>
    public void ReplacePackage(string templateId, string version, IReadOnlyDictionary<string, byte[]> files)
    {
        Remove(["templates", templateId, version]);
        Add(["templates", templateId, version], files);
        _generation++;
    }

    /// <summary>Removes a version's folder, as a version deleted from the branch: the commit moves.</summary>
    public void RemoveVersion(string templateId, string version)
    {
        Remove(["templates", templateId, version]);
        _generation++;
    }

    /// <summary>Serves <paramref name="bytes"/> for a blob whatever its real hash is, as a corrupted source would.</summary>
    public void OverrideBlob(string blobSha, byte[] bytes) => _blobOverrides[blobSha] = bytes;

    /// <summary>Answers every tree as truncated, as a tree too large to list in one answer is.</summary>
    public void SetTruncated(bool truncated) => _truncated = truncated;

    /// <summary>Answers nothing at all, as a source that cannot be reached.</summary>
    public void SetUnreachable(bool unreachable) => _stopped = unreachable;

    /// <summary>Stops answering.</summary>
    public void Stop() => _stopped = true;

    /// <summary>Answers every request with <paramref name="status"/>; <paramref name="rateLimited"/> adds the rate-limit headers.</summary>
    public void SetStatus(HttpStatusCode status, bool rateLimited = false)
    {
        _status = status;
        _rateLimited = rateLimited;
    }

    /// <summary>Answers normally again after <see cref="SetStatus"/>.</summary>
    public void ClearStatus() => _status = null;

    /// <summary>Answers again after <see cref="Stop"/>.</summary>
    public void Start() => _stopped = false;

    /// <summary>The blob name of <paramref name="content"/>, as a package file carries it.</summary>
    public static string BlobSha(byte[] content) => GitBlobs.BlobSha(content);

    /// <summary>
    /// Answers one request directly, for the host that serves this double over a
    /// socket: the handler's own dispatch, reached without a message handler.
    /// </summary>
    /// <param name="request">The request to answer.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new InvalidOperationException("the client sent no URI");
        _requests.Add(new StubRequest(
            request.Method.Method,
            uri.AbsolutePath + uri.Query,
            request.Headers.UserAgent.ToString(),
            string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var version) ? string.Join(",", version) : string.Empty,
            request.Headers.Authorization?.ToString() ?? string.Empty));

        if (_stopped)
        {
            throw new HttpRequestException("the stub template source is not answering");
        }

        if (_status is { } status)
        {
            var refused = Answer(request, status, null);
            if (_rateLimited)
            {
                refused.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
                refused.Headers.TryAddWithoutValidation("retry-after", "60");
            }

            return Task.FromResult(refused);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 4
            || !string.Equals(segments[0], "repos", StringComparison.Ordinal)
            || !string.Equals(segments[1], Owner, StringComparison.Ordinal)
            || !string.Equals(segments[2], Repository, StringComparison.Ordinal))
        {
            return Task.FromResult(Answer(request, HttpStatusCode.NotFound, null));
        }

        var resource = segments[3];
        var name = segments.Length > 4 ? segments[4] : string.Empty;
        var response = resource switch
        {
            "commits" when segments.Length == 5 && string.Equals(name, Branch, StringComparison.Ordinal) =>
                Answer(request, HttpStatusCode.OK, CommitDocument()),
            "git" when segments.Length > 5 && string.Equals(name, "trees", StringComparison.Ordinal) =>
                Answer(request, HttpStatusCode.OK, TreeDocument(segments[5], uri.Query.Contains("recursive=1", StringComparison.Ordinal))),
            "git" when segments.Length > 5 && string.Equals(name, "blobs", StringComparison.Ordinal) =>
                Blob(request, segments[5]),
            _ => Answer(request, HttpStatusCode.NotFound, null),
        };

        return Task.FromResult(response);
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request, HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request };
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }

    private HttpResponseMessage Blob(HttpRequestMessage request, string blobSha)
    {
        if (!TryFindBlob(blobSha, out var content))
        {
            return Answer(request, HttpStatusCode.NotFound, null);
        }

        var bytes = _blobOverrides.TryGetValue(blobSha, out var overridden) ? overridden : content;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        response.Content = new ByteArrayContent(bytes);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.github.raw+json");
        return response;
    }

    private string CommitDocument() =>
        JsonSerializer.Serialize(new
        {
            sha = CommitSha,
            commit = new { tree = new { sha = RootTreeSha } },
        });

    private string TreeDocument(string treeSha, bool recursive)
    {
        var entries = new List<TreeEntry>();
        if (_root.TryFind(treeSha, string.Empty, out var directory, out _))
        {
            // A tree answer lists paths relative to the requested tree, never with its ancestors.
            AppendEntries(directory, string.Empty, recursive, entries);
        }

        var truncated = _truncated
            || _truncatedTemplates.Any(templateId => string.Equals(TemplateFolderSha(templateId), treeSha, StringComparison.Ordinal));
        return JsonSerializer.Serialize(new { sha = treeSha, tree = entries, truncated });
    }

    private string? TemplateFolderSha(string templateId)
    {
        if (!_root.TryGetDirectory("templates", out var templates)
            || !templates.TryGetDirectory(templateId, out var folder))
        {
            return null;
        }

        return folder.Sha;
    }

    private static void AppendEntries(StubDirectory directory, string prefix, bool recursive, List<TreeEntry> entries)
    {
        foreach (var child in directory.Children)
        {
            var path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            if (child.Directory is { } childDirectory)
            {
                entries.Add(new TreeEntry(path, "040000", "tree", childDirectory.Sha, 0));
                if (recursive)
                {
                    AppendEntries(childDirectory, path, recursive: true, entries);
                }
            }
            else
            {
                entries.Add(new TreeEntry(path, "100644", "blob", child.Sha, child.Content!.Length));
            }
        }
    }

    /// <summary>One entry of a tree answer.</summary>
    private sealed record TreeEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
        [property: System.Text.Json.Serialization.JsonPropertyName("mode")] string Mode,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
        [property: System.Text.Json.Serialization.JsonPropertyName("sha")] string Sha,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);

    private void Add(string[] tree, IReadOnlyDictionary<string, byte[]> files)
    {
        var directory = _root;
        foreach (var name in tree)
        {
            directory = directory.Directory(name);
        }

        foreach (var file in files)
        {
            directory.File(file.Key, file.Value);
        }
    }

    private void Remove(string[] tree)
    {
        var directory = _root;
        for (var index = 0; index < tree.Length - 1; index++)
        {
            if (!directory.TryGetDirectory(tree[index], out var next))
            {
                return;
            }

            directory = next;
        }

        if (directory.TryGetDirectory(tree[^1], out var removed))
        {
            foreach (var node in removed.Nodes())
            {
                if (node.Content is { } bytes)
                {
                    _retiredBlobs[node.Sha] = bytes;
                }
            }
        }

        directory.Remove(tree[^1]);
    }

    private bool TryFindBlob(string blobSha, out byte[] content)
    {
        if (_retiredBlobs.TryGetValue(blobSha, out var retired))
        {
            content = retired;
            return true;
        }

        foreach (var node in _root.Nodes())
        {
            if (node.Content is { } bytes && string.Equals(node.Sha, blobSha, StringComparison.Ordinal))
            {
                content = bytes;
                return true;
            }
        }

        content = [];
        return false;
    }

    /// <summary>One request the client made.</summary>
    /// <param name="Method">The method it used.</param>
    /// <param name="Path">The path and query it asked for.</param>
    /// <param name="UserAgent">The user agent it declared.</param>
    /// <param name="Accept">The media types it accepted.</param>
    /// <param name="ApiVersion">The API version it declared.</param>
    /// <param name="Authorization">The authorization it declared, empty when it declared none.</param>
    public sealed record StubRequest(string Method, string Path, string UserAgent, string Accept, string ApiVersion, string Authorization);

    private sealed class StubNode
    {
        public StubNode(string name, byte[]? content, StubDirectory? directory)
        {
            Name = name;
            Content = content;
            Directory = directory;
        }

        public string Name { get; }

        public byte[]? Content { get; }

        public StubDirectory? Directory { get; }

        public string Sha => Content is not null ? GitBlobs.BlobSha(Content) : Directory!.Sha;
    }

    private sealed class StubDirectory(StubDirectory? parent)
    {
        private readonly List<StubNode> _children = [];
        private string? _sha;

        public IReadOnlyList<StubNode> Children => _ordered ??= [.. _children.OrderBy(child => child.Directory is null ? child.Name : child.Name + "/", StringComparer.Ordinal)];

        public string Sha => _sha ??= GitBlobs.TreeSha(
            Children.Select(child => (child.Directory is null ? "100644" : "40000", child.Name, child.Sha)));

        public StubDirectory Directory(string name)
        {
            if (Find(name) is { Directory: { } existing })
            {
                return existing;
            }

            var created = new StubDirectory(this);
            Replace(name, new StubNode(name, null, created));
            return created;
        }

        public void File(string relativePath, byte[] content)
        {
            var segments = relativePath.Split('/');
            var directory = this;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                directory = directory.Directory(segments[index]);
            }

            directory.Replace(segments[^1], new StubNode(segments[^1], content, null));
        }

        public void Remove(string name)
        {
            if (Find(name) is { } node)
            {
                _children.Remove(node);
                Invalidate();
            }
        }

        public bool TryGetDirectory(string name, out StubDirectory directory)
        {
            if (Find(name) is { Directory: { } existing })
            {
                directory = existing;
                return true;
            }

            directory = this;
            return false;
        }

        public IEnumerable<StubNode> Nodes()
        {
            foreach (var child in Children)
            {
                yield return child;
                if (child.Directory is { } directory)
                {
                    foreach (var nested in directory.Nodes())
                    {
                        yield return nested;
                    }
                }
            }
        }

        public bool TryFind(string sha, string prefix, out StubDirectory directory, out string path)
        {
            if (string.Equals(Sha, sha, StringComparison.Ordinal))
            {
                directory = this;
                path = prefix;
                return true;
            }

            foreach (var child in Children)
            {
                if (child.Directory is { } nested
                    && nested.TryFind(sha, prefix.Length == 0 ? child.Name : prefix + "/" + child.Name, out directory, out path))
                {
                    return true;
                }
            }

            directory = this;
            path = prefix;
            return false;
        }

        private IReadOnlyList<StubNode>? _ordered;

        private StubNode? Find(string name) => _children.Find(node => string.Equals(node.Name, name, StringComparison.Ordinal));

        private void Replace(string name, StubNode node)
        {
            var existing = Find(name);
            if (existing is not null)
            {
                _children.Remove(existing);
            }

            _children.Add(node);
            Invalidate();
        }

        private void Invalidate()
        {
            _sha = null;
            _ordered = null;
            parent?.Invalidate();
        }
    }
}
