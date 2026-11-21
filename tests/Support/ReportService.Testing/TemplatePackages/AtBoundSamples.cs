using System.Text;
using System.Text.Json.Nodes;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;

namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// One control package's at-bound document, derived by the product's own derivation: the
/// version's manifest, its schema and its own sample are read from the package's files,
/// the contract is compiled by the delivered compiler, and <see cref="AtBoundSample"/>
/// answers the document the take would print - the version's header and one record, one
/// string position of which carries exactly the row bound. A case that prints the
/// at-bound sample uses this instead of building a value of its own, so the document it
/// renders is the document the check renders.
/// </summary>
public static class AtBoundSamples
{
    /// <summary>The hash the derived candidate carries: the derivation reads none of it.</summary>
    private static readonly string Descriptor = "sha256:" + new string('a', 64);

    /// <summary>The derivation's answer over a package's files, with the package it read.</summary>
    /// <param name="Directory">The package's folder, with the files written as they were given.</param>
    /// <param name="Result">What the derivation answered: the document, or the not-applicable verdict.</param>
    /// <param name="Candidate">The version the derivation ran over.</param>
    public sealed record Derived(string Directory, AtBoundSampleResult Result, TemplateCandidate Candidate);

    /// <summary>Derives the at-bound document of <paramref name="files"/>.</summary>
    /// <param name="files">The package's files, by package-relative path.</param>
    /// <param name="templateId">The template the package declares.</param>
    /// <param name="version">The version the package declares.</param>
    /// <returns>The derivation's answer and the folder it read.</returns>
    /// <exception cref="InvalidDataException">The package holds no readable manifest or schema.</exception>
    public static Derived Derive(IReadOnlyDictionary<string, byte[]> files, string templateId, string version)
    {
        ArgumentNullException.ThrowIfNull(files);

        var directory = Path.Combine(
            Path.GetTempPath(),
            "report-service-at-bound",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        TemplateManifest manifest;
        string canonicalJson;
        using (var stream = File.OpenRead(Path.Combine(directory, "template.yaml")))
        {
            var read = new TemplateManifestReader().Read(stream);
            if (!read.Succeeded || read.Manifest is null)
            {
                throw new InvalidDataException(read.Reason ?? "the package holds no readable template.yaml.");
            }

            manifest = read.Manifest;
            canonicalJson = read.CanonicalJson!;
        }

        var schemaPath = manifest.DataContract?.Schema
            ?? throw new InvalidDataException("the package declares no schema.");
        var schemaBytes = files[schemaPath];
        var compilation = DataContractCompiler.Compile(schemaBytes, Descriptor, schemaPath);
        var contract = compilation.Contract
            ?? throw new InvalidDataException("the package's schema cannot be compiled.");

        var candidate = new TemplateCandidate(templateId, version, manifest, canonicalJson, directory, Descriptor);
        try
        {
            var schemaRoot = JsonNode.Parse(Encoding.UTF8.GetString(schemaBytes))
                ?? throw new InvalidDataException("the package's schema is not a document.");
            var result = AtBoundSample.Build(new AtBoundSampleInput(
                candidate,
                contract,
                schemaRoot,
                files["sample.json"],
                PdfRowBound.Bytes,
                manifest.Outputs?.Pdf?.Verification?.Anchors?.Item ?? []));
            return new Derived(directory, result, candidate);
        }
        finally
        {
            contract.Dispose();
        }
    }

    /// <summary>The package's files with another sample document.</summary>
    /// <param name="files">The package's files, by package-relative path.</param>
    /// <param name="sample">The sample's bytes.</param>
    /// <returns>A copy of the files whose <c>sample.json</c> is <paramref name="sample"/>.</returns>
    public static Dictionary<string, byte[]> WithSample(IReadOnlyDictionary<string, byte[]> files, byte[] sample)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sample);

        var copy = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            ["sample.json"] = sample,
        };
        return copy;
    }
}
