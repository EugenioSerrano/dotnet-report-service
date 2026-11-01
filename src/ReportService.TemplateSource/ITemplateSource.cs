namespace ReportService.TemplateSource;

/// <summary>
/// The configured template source, read as a whole at one commit. Every call
/// after <see cref="ResolveBranchAsync"/> names a hash, never a branch, so a
/// branch that moves under a take cannot make the take read two commits: what
/// it read is what it recorded.
/// </summary>
public interface ITemplateSource
{
    /// <summary>The commit the branch points at now, and the tree of the repository at it.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken);

    /// <summary>The folder of one version — <c>templates/&lt;templateId&gt;/&lt;version&gt;/</c> — at one commit.</summary>
    /// <param name="commit">The commit to read at.</param>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The folder, or <see langword="null"/> when it does not exist.</returns>
    Task<SourceFolder?> FindVersionFolderAsync(SourceCommit commit, string templateId, string version, CancellationToken cancellationToken);

    /// <summary>Every file of a version's folder, from the folder's recursive tree.</summary>
    /// <param name="folder">The folder to list.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken);

    /// <summary>Streams one file's bytes to <paramref name="consume"/>, verified against its git hash.</summary>
    /// <param name="file">The file to read.</param>
    /// <param name="consume">Receives the bytes; the stream is valid only for the duration of the call.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task OpenFileAsync(SourceFile file, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken);
}

/// <summary>One commit of the source, and the tree of the repository at it.</summary>
/// <param name="CommitSha">The commit's SHA.</param>
/// <param name="RootTreeSha">The git tree of the repository's root at that commit.</param>
public sealed record SourceCommit(string CommitSha, string RootTreeSha);

/// <summary>One version's folder at one commit.</summary>
/// <param name="TreeSha">The git tree of the folder.</param>
public sealed record SourceFolder(string TreeSha);

/// <summary>One file of a version's folder.</summary>
/// <param name="Path">The path relative to the version's folder, with <c>/</c> separators.</param>
/// <param name="BlobSha">The git object name of the file's bytes (a SHA-1 over the blob header and content).</param>
/// <param name="Size">The number of bytes.</param>
public sealed record SourceFile(string Path, string BlobSha, long Size);
