using System.Security.Cryptography;
using PdfSharp.Pdf;
using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The page entries the assembly must carry through: PDFsharp's import keeps only the
/// entries it models, so a source page's <c>/Tabs /S</c> - what Chromium writes on
/// every page - and any other direct entry it does not model are dropped by the batch
/// import and again by the final merge. The assembly restores them on the final
/// document's pages before its one save; the verifier's dictionary check requires every
/// source entry to be present and unchanged, so a missing one still fails.
/// </summary>
public sealed class PageDictionaryPreservationTests
{
    [Fact]
    public async Task EntriesTheImportDropsAreRestoredOnTheAssembledPage()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 1, decorate: Decorate),
            };
            var source = PdfDocumentReader.Read(parts[0].Path, AssemblyFixture.Frame.Margins);
            Assert.Equal("/S", source.Pages[0].Dictionary.Entries["Tabs"]);
            Assert.Equal("7", source.Pages[0].Dictionary.Entries["StructParents"]);

            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            var assembled = PdfDocumentReader.Read(output, AssemblyFixture.Frame.Margins);
            Assert.Equal("/S", assembled.Pages[0].Dictionary.Entries["Tabs"]);
            Assert.Equal("7", assembled.Pages[0].Dictionary.Entries["StructParents"]);
            Assert.Equal(
                source.Pages[0].Dictionary.Entries["ViewerPreferences"],
                assembled.Pages[0].Dictionary.Entries["ViewerPreferences"]);
            Assert.Equal(
                source.Pages[0].Dictionary.Entries["CustomTags"],
                assembled.Pages[0].Dictionary.Entries["CustomTags"]);

            var verdict = Verify(output, parts);
            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EachPageKeepsTheEntriesOfItsOwnPart()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            // One part per batch, so the entry crosses both imports: the batch's and
            // the final merge's. The parts carry different entries, so a restoration
            // applied to the wrong page fails the verifier.
            var parts = new[]
            {
                AssemblyFixture.CreatePart(
                    directory,
                    0,
                    1,
                    decorate: page => page.Elements["/Tabs"] = new PdfName("/S")),
                AssemblyFixture.CreatePart(
                    directory,
                    1,
                    1,
                    decorate: page => page.Elements["/StructParents"] = new PdfInteger(11)),
            };

            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            var assembled = PdfDocumentReader.Read(output, AssemblyFixture.Frame.Margins);
            Assert.Equal("/S", assembled.Pages[0].Dictionary.Entries["Tabs"]);
            Assert.False(assembled.Pages[0].Dictionary.Entries.ContainsKey("StructParents"));
            Assert.Equal("11", assembled.Pages[1].Dictionary.Entries["StructParents"]);
            Assert.False(assembled.Pages[1].Dictionary.Entries.ContainsKey("Tabs"));

            var verdict = Verify(output, parts);
            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnEntryTheAssemblyWouldDropStillFailsTheDictionaryCheck()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 1, decorate: Decorate),
            };
            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            // A plain PDFsharp copy is the same import the assembly uses, and it drops
            // the entries again: the restored document is the control that must fail.
            var doctored = AssemblyFixture.Rebuild(output, Path.Combine(directory, "dropped.pdf"), _ => true);

            var verdict = Verify(doctored, parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PageDictionary, verdict.Reason);
            Assert.Equal(0, verdict.PartIndex);
            Assert.Equal(1, verdict.PageNumber);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The entries a Chromium-shaped page carries beside the ones PDFsharp models.</summary>
    private static void Decorate(PdfPage page)
    {
        page.Elements["/Tabs"] = new PdfName("/S");
        page.Elements["/StructParents"] = new PdfInteger(7);
        var preferences = new PdfDictionary(page.Owner);
        preferences.Elements["/HideToolbar"] = new PdfBoolean(true);
        preferences.Elements["/Label"] = new PdfString("statement");
        page.Elements["/ViewerPreferences"] = preferences;
        var tags = new PdfArray(page.Owner);
        tags.Elements.Add(new PdfInteger(1));
        tags.Elements.Add(new PdfReal(1.5));
        page.Elements["/CustomTags"] = tags;
    }

    private static AssemblyVerdict Verify(string output, IReadOnlyList<AssemblyPart> parts)
    {
        var expectations = parts
            .Select(part => new AssemblyPartExpectation(
                part.PartIndex,
                part.Path,
                part.PageCount,
                new FileInfo(part.Path).Length,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(part.Path)))))
            .ToList();
        return AssemblyVerifier.Verify(new AssemblyVerificationRequest(
            output,
            expectations,
            AssemblyFixture.Frame,
            AssemblyFixture.Contract()));
    }
}

