namespace ReportService.TemplateSource.Source;

/// <summary>
/// The publication record of one version: the transport catalogue the publisher
/// writes beside its files. Its member names are the contract, its serialisation
/// is the publisher's; it declares the contract version, the template, the version
/// and one entry per file with the size and SHA-256 the take verifies against.
/// Owner, sharing, engine and schema stay in <c>template.yaml</c>.
/// </summary>
/// <param name="DescriptorVersion">The record's contract version.</param>
/// <param name="TemplateId">The template the record declares.</param>
/// <param name="Version">The version the record declares.</param>
/// <param name="Files">The files, each with its path, size and SHA-256.</param>
/// <param name="FileCount">The number of files the record declares.</param>
/// <param name="TotalSize">The total size the record declares.</param>
public sealed record PublicationDescriptor(
    string DescriptorVersion,
    string TemplateId,
    string Version,
    IReadOnlyList<SourceFile> Files,
    int FileCount,
    long TotalSize);
