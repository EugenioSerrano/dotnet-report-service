using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ReportService.Testing;
using ReportService.Testing.VerificationLoss;
using ReportService.TemplateSource.Manifest;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The verification-loss corpus: the four snapshots and the two packages a refused
/// print is evidenced with. The corpus is a function of its identifiers, the sums
/// file records the bytes the cases were measured on, every anchor path it declares
/// exists in the envelope, and the packages are the product format the take and the
/// rendering tier read.
/// </summary>
public sealed class VerificationLossCorpusTests
{
    private static string CorpusDirectory =>
        Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "VerificationLoss");

    private static string SnapshotsDirectory => Path.Combine(CorpusDirectory, "snapshots");

    private static string CommittedSums => Path.Combine(SnapshotsDirectory, VerificationLossCorpus.SumsFileName);

    [Fact]
    public void EverySnapshotIsAFunctionOfItsId()
    {
        foreach (var id in VerificationLossCorpus.SnapshotIds)
        {
            var first = VerificationLossCorpus.Snapshot(id);
            var second = VerificationLossCorpus.Snapshot(id);

            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(first.Bytes, second.Bytes);
            Assert.Equal(first.RecordCount, second.RecordCount);
        }
    }

    [Fact]
    public void TheCommittedSumsRecordTheBytesTheCorpusGenerates()
    {
        var folder = Directory.CreateTempSubdirectory("report-verification-loss-");
        try
        {
            VerificationLossCorpus.WriteSnapshots(folder.FullName);

            var generated = File.ReadAllLines(Path.Combine(folder.FullName, VerificationLossCorpus.SumsFileName));
            var committed = File.ReadAllLines(CommittedSums);

            Assert.Equal(VerificationLossCorpus.SnapshotIds.Count, generated.Length);
            Assert.Equal(committed, generated);
            Assert.Equal(
                VerificationLossCorpus.SnapshotIds.Select(id => id + ".json"),
                generated.Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..]));

            foreach (var snapshot in VerificationLossCorpus.Snapshots())
            {
                var path = Path.Combine(SnapshotsDirectory, snapshot.FileName);
                Assert.True(File.Exists(path), $"{snapshot.FileName} is not committed.");
                Assert.Equal(snapshot.Sha256, HashFile(path));
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void EveryAnchorPathThePackageDeclaresExistsInTheEnvelope()
    {
        foreach (var id in VerificationLossCorpus.SnapshotIds)
        {
            var root = JsonNode.Parse(VerificationLossCorpus.Snapshot(id).Bytes)!.AsObject();
            var items = root["items"]!.AsArray();

            Assert.False(string.IsNullOrEmpty(root["header"]!["reportTitle"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(root["header"]!["customer"]!["id"]!.GetValue<string>()));
            Assert.Equal(3, items.Count);
            Assert.All(items, item => Assert.NotNull(item!["description"]));

            // The record's number is a member every record holds, the empty token included:
            // the judge reads a record through its declared columns and a missing member is
            // not a record.
            Assert.All(items, item => Assert.NotNull(item!["sequence"]));
        }
    }

    [Fact]
    public void TheSnapshotsCarryTheDataTheDefectsNeed()
    {
        var text = Description(VerificationLossCorpus.TextLoss, 2);
        Assert.Equal(VerificationLossCorpus.PlantedTextLoss, text);
        Assert.Contains('\u00AD', text);
        Assert.Equal(1, text.Count(character => character == '\u00AD'));

        Assert.Equal(string.Empty, Description(VerificationLossCorpus.RowLoss, 2));
        Assert.Equal(string.Empty, Number(VerificationLossCorpus.RowLoss, 2));
        Assert.Equal(VerificationLossCorpus.PlantedHeader, TitleOf(VerificationLossCorpus.RowLoss));

        Assert.Equal(VerificationLossCorpus.PlantedTextLoss, Description(VerificationLossCorpus.ManyLoss, 1));
        Assert.Equal(string.Empty, Description(VerificationLossCorpus.ManyLoss, 3));
        Assert.Equal(string.Empty, Number(VerificationLossCorpus.ManyLoss, 3));

        foreach (var position in new[] { 1, 2, 3 })
        {
            var honest = Description(VerificationLossCorpus.Honest, position);
            Assert.False(string.IsNullOrWhiteSpace(honest));
            Assert.DoesNotContain('\u00AD', honest);
            Assert.Equal(position.ToString(System.Globalization.CultureInfo.InvariantCulture), Number(VerificationLossCorpus.Honest, position));
        }
    }

    [Fact]
    public async Task EveryPackageIsTheProductFormatAndArchivesIdenticallyTwice()
    {
        foreach (var template in new[] { VerificationLossCorpus.Template, VerificationLossCorpus.SupplementaryTemplate })
        {
            var files = VerificationLossCorpus.PackageFiles(template);
            Assert.Contains("template.yaml", files.Keys);
            Assert.Contains("schema.json", files.Keys);
            Assert.Contains("sample.json", files.Keys);

            var read = new TemplateManifestReader().Read(new MemoryStream(files["template.yaml"], writable: false));
            Assert.True(read.Succeeded, read.Reason);
            var manifest = read.Manifest!;
            Assert.Equal(template, manifest.Id);
            Assert.Equal(VerificationLossCorpus.Version, manifest.Version);
            Assert.Equal(["$.reportTitle", "$.customer.id"], manifest.Outputs!.Pdf!.Verification!.Anchors!.Header);
            Assert.Equal(["$.sequence", "$.description"], manifest.Outputs.Pdf.Verification.Anchors.Item);

            foreach (var part in new[]
                     {
                         manifest.Outputs.Html!.Parts!.DocumentStart,
                         manifest.Outputs.Html.Parts.Row,
                         manifest.Outputs.Html.Parts.DocumentEnd,
                     })
            {
                Assert.Contains(part!, files.Keys);
            }

            Assert.Equal(await ArchiveAsync(files), await ArchiveAsync(files));
        }
    }

    private static string TitleOf(string id) =>
        JsonNode.Parse(VerificationLossCorpus.Snapshot(id).Bytes)!["header"]!["reportTitle"]!.GetValue<string>();

    // A record is read by its position: the corpus's defect record holds the empty
    // number, so the number is not an identity the test can look a record up by.
    private static string Description(string id, int position) =>
        JsonNode.Parse(VerificationLossCorpus.Snapshot(id).Bytes)!["items"]!.AsArray()[position - 1]!
            ["description"]!.GetValue<string>();

    // The record's number as its token: an integer's own text, or the empty token.
    private static string Number(string id, int position) =>
        JsonNode.Parse(VerificationLossCorpus.Snapshot(id).Bytes)!["items"]!.AsArray()[position - 1]!
            ["sequence"]!.ToJsonString().Trim('"');

    private static async Task<string> ArchiveAsync(IReadOnlyDictionary<string, byte[]> files)
    {
        var sources = files
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new ReportService.TemplatePackages.Packages.PackageFileSource(
                file.Key,
                _ => Task.FromResult<Stream>(new MemoryStream(file.Value, writable: false))))
            .ToList();
        using var buffer = new MemoryStream();
        await ReportService.TemplatePackages.Packages.TemplatePackageArchive.WriteAsync(sources, buffer);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
