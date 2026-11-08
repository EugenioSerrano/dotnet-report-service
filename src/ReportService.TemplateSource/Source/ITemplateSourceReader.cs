using ReportService.Storage;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// The deployment's template source, read and listed only. It is a port over the
/// object store, not a reshaped <see cref="ITemplateSource"/>: the Git client keeps
/// its own interface until the change that retires it. The <c>template-source</c>
/// key prefix keeps the two classes apart, and no member of this port writes or
/// deletes.
/// </summary>
public interface ITemplateSourceReader
{
    /// <summary>Reads one exact version's publication, verified against its own record.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version, exactly as the reference names it.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>One of the read's states; never an exception for a package's own defect.</returns>
    Task<SourceVersionRead> ReadVersionAsync(string templateId, string version, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the publication records of one template, one entry per record, in
    /// the store's ordinal order. It is an enumeration, never an authority: grouping
    /// the entries and deciding what a repeated version means is the caller's rule.
    /// </summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The usable records; an entry outside the layout is skipped, never returned.</returns>
    /// <exception cref="TemplateSourceException">The class could not be listed.</exception>
    Task<IReadOnlyList<PublishedVersion>> ListPublishedVersionsAsync(string templateId, CancellationToken cancellationToken);

    /// <summary>
    /// Streams one file of a publication to <paramref name="consume"/>, verified
    /// against the size and SHA-256 the publication's record declares. Nothing is
    /// buffered whole.
    /// </summary>
    /// <param name="templateId">The template's identifier the publication was found under.</param>
    /// <param name="version">The version the publication was found under.</param>
    /// <param name="file">The file, as the publication's record declared it.</param>
    /// <param name="consume">Receives the verified bytes; the stream is valid only for the duration of the call.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="SourceReadException">The class does not hold the file, its bytes contradict the record, or the class cannot be read.</exception>
    Task OpenFileAsync(
        string templateId,
        string version,
        SourceFile file,
        Func<Stream, CancellationToken, Task> consume,
        CancellationToken cancellationToken);
}
