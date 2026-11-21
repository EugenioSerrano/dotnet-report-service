using System.Text;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks;

/// <summary>
/// One candidate's package, read once: the file list, the decoded text of the
/// files the checks judge, and the manifest document exactly as the take recorded
/// it. A file's text is decoded strictly — a leading byte-order mark skipped, an
/// invalid sequence reported — because a template whose bytes are not text is
/// refused, never half-read. No file is written; the directory is read only.
/// </summary>
public sealed class PackageView
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _root;
    private readonly HashSet<string> _files;
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>Reads the candidate's directory once.</summary>
    /// <param name="candidate">The version being taken.</param>
    public PackageView(TemplateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        _root = Path.GetFullPath(candidate.PackageDirectory);
        _files = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every package-relative path the package holds, with <c>/</c> separators.</summary>
    public IReadOnlyCollection<string> Files => _files;

    /// <summary>Whether the package holds <paramref name="path"/>.</summary>
    /// <param name="path">A package-relative path.</param>
    public bool Exists(string path) => _files.Contains(path);

    /// <summary>The bytes of <paramref name="path"/>.</summary>
    /// <param name="path">A package-relative path the package holds.</param>
    public byte[] ReadBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.ReadAllBytes(FullPath(path));
    }

    /// <summary>
    /// The text of <paramref name="path"/>, decoded strictly as UTF-8 with a
    /// leading byte-order mark skipped. An invalid sequence raises
    /// <see cref="DecoderFallbackException"/>, which the caller reports as a
    /// refusal.
    /// </summary>
    /// <param name="path">A package-relative path the package holds.</param>
    public string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_texts.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var bytes = ReadBytes(path);
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        var text = StrictUtf8.GetString(span);
        _texts[path] = text;
        return text;
    }

    private string FullPath(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal)
            ? full
            : throw new InvalidOperationException("the path is outside the package");
    }
}
