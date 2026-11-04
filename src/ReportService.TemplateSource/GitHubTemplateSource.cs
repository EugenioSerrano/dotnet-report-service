using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReportService.TemplateSource;

/// <summary>
/// The configured repository and branch, read over the source's HTTP API. The
/// branch is resolved to a commit once, and every later request names a hash,
/// so the whole read sees one commit even if the branch moves under it.
/// Requests are shaped by the settings and by nothing a template carries; a
/// response is bounded, a blob is verified against its git hash as it streams,
/// and every failure is classified as unreachable, misconfigured, too large or
/// unsupported, never as "does not exist".
/// </summary>
public sealed class GitHubTemplateSource : ITemplateSource
{
    /// <summary>The most a JSON answer may hold.</summary>
    public const int MaxJsonBodyBytes = 1024 * 1024;

    private const int CopyBufferSize = 80 * 1024;

    private static readonly string Agent =
        "dotnet-report-service/" + (typeof(GitHubTemplateSource).Assembly.GetName().Version?.ToString() ?? "unknown");

    private readonly HttpClient _client;
    private readonly TemplateSourceOptions _options;
    private readonly Uri _baseUri;

    /// <summary>Reads through <paramref name="client"/>, using <paramref name="options"/>.</summary>
    /// <param name="client">The HTTP client; its handler decides redirects, which this class never follows across hosts.</param>
    /// <param name="options">The template source settings, already validated.</param>
    public GitHubTemplateSource(HttpClient client, TemplateSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate().Any())
        {
            throw new ArgumentException("the template source settings are not valid", nameof(options));
        }

        _client = client;
        _options = options;
        var baseUri = new Uri(options.ApiBaseUrl, UriKind.Absolute);
        _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken)
    {
        var uri = RepositoryUri("commits/" + EscapeSegments(_options.Branch!));
        using var document = await GetJsonAsync(uri, commitRequest: true, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var commitSha = ReadHash(root, "sha");
        if (!root.TryGetProperty("commit", out var commit)
            || !commit.TryGetProperty("tree", out var tree))
        {
            throw Malformed();
        }

        return new SourceCommit(commitSha, ReadHash(tree, "sha"));
    }

    /// <inheritdoc />
    public async Task<SourceFolder?> FindVersionFolderAsync(
        SourceCommit commit,
        string templateId,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var current = commit.RootTreeSha;
        foreach (var segment in new[] { "templates", templateId, version })
        {
            var next = await FindTreeEntryAsync(current, segment, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return new SourceFolder(current);
    }

    /// <inheritdoc />
    public async Task<SourceTemplateVersions?> ListVersionsAsync(
        SourceCommit commit,
        string templateId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentException.ThrowIfNullOrEmpty(templateId);

        var templates = await FindTreeEntryAsync(commit.RootTreeSha, "templates", cancellationToken).ConfigureAwait(false);
        if (templates is null)
        {
            return null;
        }

        var template = await FindTreeEntryAsync(templates, templateId, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return null;
        }

        var uri = new Uri(_baseUri, "repos/" + _options.Owner + "/" + _options.RepositoryName + "/git/trees/" + template);
        using var document = await GetJsonAsync(uri, commitRequest: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (IsTruncated(root))
        {
            throw TooLarge($"the template folder 'templates/{templateId}/' holds more entries than the source will list in one answer");
        }

        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            throw Malformed();
        }

        var names = new List<string>();
        foreach (var entry in tree.EnumerateArray())
        {
            // Only folders are versions. A file, a symbolic link or a
            // submodule beside them is not an error and not a version.
            if (string.Equals(ReadString(entry, "type"), "tree", StringComparison.Ordinal))
            {
                var name = ReadString(entry, "path");
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return new SourceTemplateVersions(names);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var uri = new Uri(_baseUri, "repos/" + _options.Owner + "/" + _options.RepositoryName + "/git/trees/" + folder.TreeSha + "?recursive=1");
        using var document = await GetJsonAsync(uri, commitRequest: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (IsTruncated(root))
        {
            throw TooLarge("the version's folder holds more entries than the source will list in one answer");
        }

        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            throw Malformed();
        }

        var files = new List<SourceFile>();
        foreach (var entry in tree.EnumerateArray())
        {
            var path = ReadString(entry, "path");
            var type = ReadString(entry, "type");
            switch (type)
            {
                case "tree":
                    continue;
                case "commit":
                    throw new TemplateSourceException(
                        TemplateSourceFailure.UnsupportedEntry,
                        $"The package holds a submodule at '{path}'. Remove the submodule and commit the files themselves.");
                case "blob":
                    var mode = ReadString(entry, "mode");
                    if (mode is "120000")
                    {
                        throw new TemplateSourceException(
                            TemplateSourceFailure.UnsupportedEntry,
                            $"The package holds a symbolic link at '{path}'. Replace it with the file it points to.");
                    }

                    if (mode is not ("100644" or "100755"))
                    {
                        throw new TemplateSourceException(
                            TemplateSourceFailure.UnsupportedEntry,
                            $"The package holds '{path}' with a mode this service does not store.");
                    }

                    var size = entry.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                    if (size > _options.MaxFileBytes)
                    {
                        throw TooLarge($"'{path}' is larger than the {_options.MaxFileBytes}-byte limit for one file");
                    }

                    files.Add(new SourceFile(path, ReadHash(entry, "sha"), size));
                    if (files.Count > _options.MaxFiles)
                    {
                        throw TooLarge($"the package holds more than the {_options.MaxFiles} files one package accepts");
                    }

                    continue;
                default:
                    throw Malformed();
            }
        }

        return files;
    }

    /// <inheritdoc />
    public async Task OpenFileAsync(
        SourceFile file,
        Func<Stream, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(consume);
        var uri = new Uri(_baseUri, "repos/" + _options.Owner + "/" + _options.RepositoryName + "/git/blobs/" + file.BlobSha);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddHeaders(request, raw: true);
        using var timeout = RequestTimeout(cancellationToken);
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response, commitRequest: false);
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var verifying = new GitBlobVerifyingStream(content, file.BlobSha, file.Size);
        await using (verifying.ConfigureAwait(false))
        {
            await consume(verifying, cancellationToken).ConfigureAwait(false);
            verifying.Complete();
        }
    }

    private async Task<string?> FindTreeEntryAsync(string treeSha, string name, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, "repos/" + _options.Owner + "/" + _options.RepositoryName + "/git/trees/" + treeSha);
        using var document = await GetJsonAsync(uri, commitRequest: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (IsTruncated(root))
        {
            throw TooLarge("the repository's tree holds more entries than the source will list in one answer");
        }

        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            throw Malformed();
        }

        foreach (var entry in tree.EnumerateArray())
        {
            if (string.Equals(ReadString(entry, "path"), name, StringComparison.Ordinal)
                && string.Equals(ReadString(entry, "type"), "tree", StringComparison.Ordinal))
            {
                return ReadHash(entry, "sha");
            }
        }

        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, bool commitRequest, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddHeaders(request, raw: false);
        using var timeout = RequestTimeout(cancellationToken);
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response, commitRequest);
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var body = await ReadBoundedAsync(content, timeout.Token).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                "The template source answered with a document this service could not read. Retry later.",
                exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                $"The template source did not answer within {_options.RequestTimeout.TotalSeconds:0} seconds. Retry later.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                "The template source could not be reached. Retry later.",
                exception);
        }

        var answeredBy = response.RequestMessage?.RequestUri;
        if (answeredBy is not null && !string.Equals(answeredBy.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                "The template source redirected the request to another host, which is never followed. Retry later.");
        }

        return response;
    }

    private void EnsureSuccess(HttpResponseMessage response, bool commitRequest)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                "The template source redirected the request, which this service never follows. Retry later.");
        }

        if (status == 429 || status >= 500 || IsRateLimited(response))
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Unreachable,
                "The template source is rate limited or unavailable. Retry later.");
        }

        if (status is 401 or 403)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Misconfigured,
                "The template source refused the credential. Check the token, the repository and the branch the service is configured with.");
        }

        if (commitRequest && status is 404 or 409 or 422)
        {
            throw new TemplateSourceException(
                TemplateSourceFailure.Misconfigured,
                "The template source does not serve the configured repository and branch. Check the repository, the branch and the credential.");
        }

        throw new TemplateSourceException(
            TemplateSourceFailure.Unreachable,
            "The template source answered with an outcome this service could not use. Retry later.");
    }

    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferSize];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxJsonBodyBytes)
            {
                throw new TemplateSourceException(
                    TemplateSourceFailure.Unreachable,
                    "The template source answered with a document larger than this service reads. Retry later.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private void AddHeaders(HttpRequestMessage request, bool raw)
    {
        request.Headers.UserAgent.ParseAdd(Agent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(raw ? "application/vnd.github.raw+json" : "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", TemplateSourceOptions.ApiVersion);
        if (_options.HasToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }
    }

    private Uri RepositoryUri(string suffix) =>
        new(_baseUri, "repos/" + _options.Owner + "/" + _options.RepositoryName + "/" + suffix);

    private static string EscapeSegments(string branch) =>
        string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

    private CancellationTokenSource RequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        return timeout;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && string.Equals(remaining.FirstOrDefault(), "0", StringComparison.Ordinal))
        || response.Headers.TryGetValues("retry-after", out _);

    private static bool IsTruncated(JsonElement root) =>
        root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True;

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadHash(JsonElement element, string property)
    {
        var hash = ReadString(element, property);
        if (!IsGitHash(hash))
        {
            throw Malformed();
        }

        return hash;
    }

    private static bool IsGitHash(string value) =>
        value.Length == 40 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static TemplateSourceException Malformed() =>
        new(TemplateSourceFailure.Unreachable, "The template source answered with a document this service could not read. Retry later.");

    private static TemplateSourceException TooLarge(string what) =>
        new(TemplateSourceFailure.PackageTooLarge, char.ToUpperInvariant(what[0]) + what[1..] + ". Publish a smaller package.");

    /// <summary>A file's bytes, hashed as a git blob while they are consumed, and refused when they do not match.</summary>
    private sealed class GitBlobVerifyingStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _expectedSha;
        private readonly long _expectedSize;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private long _read;
        private bool _completed;

        public GitBlobVerifyingStream(Stream inner, string expectedSha, long expectedSize)
        {
            _inner = inner;
            _expectedSha = expectedSha;
            _expectedSize = expectedSize;
            _hash.AppendData(Encoding.ASCII.GetBytes("blob " + expectedSize.ToString(CultureInfo.InvariantCulture) + "\0"));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _expectedSize;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Account(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Account(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Account(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>Checks that every byte was delivered and that the git hash matches the source's.</summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_read != _expectedSize)
            {
                throw new TemplateSourceException(
                    TemplateSourceFailure.Unreachable,
                    "The template source delivered fewer bytes than it declared. Retry later.");
            }

            var digest = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (!string.Equals(digest, _expectedSha, StringComparison.Ordinal))
            {
                throw new TemplateSourceException(
                    TemplateSourceFailure.Unreachable,
                    "The bytes the template source delivered do not match the hash it declared. Retry later.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Account(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            _read += data.Length;
            if (_read > _expectedSize)
            {
                throw new TemplateSourceException(
                    TemplateSourceFailure.Unreachable,
                    "The template source delivered more bytes than it declared. Retry later.");
            }

            _hash.AppendData(data);
        }
    }
}
