using System.Security.Cryptography;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Assembly;

/// <summary>What one structural verification is given: the assembled document, the parts' commits and the page frame.</summary>
/// <param name="OutputPath">The assembled document's temporary file.</param>
/// <param name="Parts">The parts, in manifest order, with what their commits recorded.</param>
/// <param name="Frame">The template's page frame, in points.</param>
/// <param name="Numbering">The numbering contract the pages were stamped with; absent only for a one-part code-only assembly.</param>
/// <param name="PageCode">The per-page code the pages carry, when the template declares one.</param>
public sealed record AssemblyVerificationRequest(
    string OutputPath,
    IReadOnlyList<AssemblyPartExpectation> Parts,
    PageFrame Frame,
    PageNumbering? Numbering,
    AssemblyPageCode? PageCode = null);

/// <summary>
/// The structural verification of an assembled document, against values that come
/// from the parts' commits and the parts' own pages, never from the assembler's
/// output: every part's bytes and page count match its commit, every page's box is
/// the template's page size, the output's page count is the parts' sum, every
/// output page maps to its source page by manifest order and carries the same text
/// outside the numbering area, the numbering area holds exactly the expected
/// stamp, and each page dictionary differs from its source only by the changes the
/// architecture allows. A document that fails any check is never published.
/// </summary>
public static class AssemblyVerifier
{
    // A part's page box is the box its producer printed, not the assembler's own
    // output: Chromium writes A4 as 594.95996 x 841.91998 while the declared frame is
    // 595.27559 x 841.88976, a 0.31563 pt width delta. The render step already treats
    // that page as the declared page (PageGeometryCheck, one point each side), and the
    // assembly input compares the same fact with the same window - one shared constant.
    private const double PageBoxTolerancePt = PageGeometryCheck.TolerancePt;

    // The numbering area window is a different fact: the stamp is the assembler's own
    // output, drawn inside the contract's area, so a twentieth of a point of rounding
    // is enough and a misplaced or wrong-area stamp still falls outside.
    private const double StampAreaTolerancePt = 0.05d;

    // The changes a stamped page may carry beside its source are the assembly's
    // page-dictionary contract: the entries in PageDictionaryPolicy.AllowedChangedValues
    // and .AllowedAddedKeys. Every other source entry must be present and unchanged,
    // and PageDictionaryPolicy.Restore is what puts it back when the import drops it.

    /// <summary>Verifies one assembled document.</summary>
    /// <param name="request">The document, the parts and the contract.</param>
    /// <returns>The verdict, with the failed check and the page or part when it failed.</returns>
    public static AssemblyVerdict Verify(AssemblyVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parts);
        if (request.Numbering is null && (request.Parts.Count != 1 || request.PageCode is null))
        {
            throw new ArgumentException(
                "a verification without numbering is only for a one-part assembly that draws a page code",
                nameof(request));
        }

        if (request.Parts.Count == 0)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.PartBytes);
        }

        var sources = new List<PdfDocumentReading>(request.Parts.Count);
        foreach (var part in request.Parts)
        {
            if (BytesVerdict(part) is { Passed: false } bytes)
            {
                return bytes;
            }

            PdfDocumentReading source;
            try
            {
                source = PdfWordReader.ReadDocument(part.Path, request.Frame.Margins);
            }
            catch (PdfUnreadableException)
            {
                return AssemblyVerdict.Failed(AssemblyFailureReasons.Unreadable, part.PartIndex);
            }

            if (source.Pages.Count != part.PageCount)
            {
                return AssemblyVerdict.Failed(AssemblyFailureReasons.PartPages, part.PartIndex);
            }

            foreach (var page in source.Pages)
            {
                if (!FitsFrame(page.Box, request.Frame))
                {
                    return AssemblyVerdict.Failed(AssemblyFailureReasons.PartPageBox, part.PartIndex, page.Number);
                }
            }

            sources.Add(source);
        }

        PdfDocumentReading output;
        try
        {
            output = PdfWordReader.ReadDocument(request.OutputPath, request.Frame.Margins);
        }
        catch (PdfUnreadableException)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.Unreadable);
        }

        var total = request.Parts.Sum(part => part.PageCount);
        if (output.Pages.Count != total)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.PageCount);
        }

        foreach (var page in output.Pages)
        {
            if (!FitsFrame(page.Box, request.Frame))
            {
                return AssemblyVerdict.Failed(AssemblyFailureReasons.PartPageBox, pageNumber: page.Number);
            }
        }

        var start = 0;
        for (var index = 0; index < request.Parts.Count; index++)
        {
            var part = request.Parts[index];
            var source = sources[index];
            for (var offset = 0; offset < part.PageCount; offset++)
            {
                var outputPage = output.Pages[start + offset];
                var sourcePage = source.Pages[offset];
                var pageNumber = start + offset + 1;

                var numberingRegion = request.Numbering is { } numbering
                    ? new PageCodeText.Region(numbering.Area.LeftPt, numbering.Area.TopPt, numbering.Area.WidthPt, numbering.Area.HeightPt)
                    : null;
                var codeRegion = request.PageCode?.CodeRegion;
                if (!SameTextOutside(outputPage, sourcePage, numberingRegion, codeRegion))
                {
                    return AssemblyVerdict.Failed(AssemblyFailureReasons.Order, part.PartIndex, pageNumber);
                }

                if (request.Numbering is { } stamped
                    && StampVerdict(outputPage, stamped, pageNumber, total, part.PartIndex) is { Passed: false } stamp)
                {
                    return stamp;
                }

                if (request.Numbering is null && !SameNames(outputPage, sourcePage, "Font"))
                {
                    return AssemblyVerdict.Failed(AssemblyFailureReasons.Resources, part.PartIndex, pageNumber);
                }

                if (request.PageCode is { } code
                    && CodeVerdict(outputPage, sourcePage, code, pageNumber, total, part.PartIndex) is { Passed: false } coded)
                {
                    return coded;
                }

                if (!SameDictionary(outputPage.Dictionary, sourcePage.Dictionary))
                {
                    return AssemblyVerdict.Failed(AssemblyFailureReasons.PageDictionary, part.PartIndex, pageNumber);
                }
            }

            start += part.PageCount;
        }

        return AssemblyVerdict.Good;
    }

    private static AssemblyVerdict? BytesVerdict(AssemblyPartExpectation part)
    {
        var info = new FileInfo(part.Path);
        if (!info.Exists || info.Length != part.SizeBytes)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.PartBytes, part.PartIndex);
        }

        using var stream = File.OpenRead(part.Path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, part.Sha256, StringComparison.Ordinal)
            ? null
            : AssemblyVerdict.Failed(AssemblyFailureReasons.PartBytes, part.PartIndex);
    }

    private static bool FitsFrame(PageBox box, PageFrame frame)
    {
        var width = Math.Abs(box.Width);
        var height = Math.Abs(box.Height);
        var rotation = ((box.Rotation % 360) + 360) % 360;
        if (rotation is 90 or 270)
        {
            (width, height) = (height, width);
        }

        return Math.Abs(width - frame.WidthPt) <= PageBoxTolerancePt
            && Math.Abs(height - frame.HeightPt) <= PageBoxTolerancePt;
    }

    private static AssemblyVerdict? StampVerdict(
        DocumentPage page,
        PageNumbering numbering,
        int pageNumber,
        int total,
        int partIndex)
    {
        var words = page.Page.Words
            .Where(word => Inside(word, numbering.Area))
            .Select(word => word.Text)
            .ToList();
        var expected = Normalize(numbering.TextFor(pageNumber, total));
        var actual = Normalize(string.Join(' ', words));
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null
            : AssemblyVerdict.Failed(AssemblyFailureReasons.Stamp, partIndex, pageNumber);
    }

    private static bool SameTextOutsideArea(DocumentPage output, DocumentPage source, NumberingArea area)
    {
        var written = output.Page.Words
            .Where(word => !Inside(word, area))
            .Select(word => word.Text)
            .ToList();
        var composed = source.Page.Words
            .Where(word => !Inside(word, area))
            .Select(word => word.Text)
            .ToList();
        return written.SequenceEqual(composed, StringComparer.Ordinal);
    }

    private static bool SameTextOutside(
        DocumentPage output,
        DocumentPage source,
        PageCodeText.Region? numbering,
        PageCodeText.Region? code)
    {
        static IEnumerable<string> Outside(DocumentPage page, PageCodeText.Region? first, PageCodeText.Region? second) =>
            page.Page.Words
                .Where(word => !(first?.Contains(word) ?? false) && !(second?.Contains(word) ?? false))
                .Select(word => word.Text);
        return Outside(output, numbering, code).SequenceEqual(Outside(source, numbering, code), StringComparer.Ordinal);
    }

    private static bool SameNames(DocumentPage output, DocumentPage source, string category)
    {
        var written = output.Graphics?.ResourceNames.GetValueOrDefault(category) ?? [];
        var composed = source.Graphics?.ResourceNames.GetValueOrDefault(category) ?? [];
        return written.SequenceEqual(composed, StringComparer.Ordinal);
    }

    // The code row: the code is the one form the assembly added to the page whose
    // bounding box is the declared area, drawn once with a translation onto the
    // area; nothing else is added to the page's objects; the text inside the area
    // is unchanged; and the form's decoded content is, byte for byte, what the
    // declared generator draws for the value the declared source yields on the
    // assembled page. The code is never decoded.
    private static AssemblyVerdict? CodeVerdict(
        DocumentPage output,
        DocumentPage source,
        AssemblyPageCode code,
        int pageNumber,
        int total,
        int partIndex)
    {
        const double tolerance = 0.05d;
        var region = code.CodeRegion;
        if (region.LeftPt + region.WidthPt > output.Page.Width + tolerance
            || region.TopPt + region.HeightPt > output.Page.Height + tolerance
            || (code.NumberingRegion is { } numbering && region.Overlaps(numbering)))
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodePlacement, partIndex, pageNumber);
        }

        if (!PageCodeText.Inside(output.Page, region).SequenceEqual(PageCodeText.Inside(source.Page, region), StringComparer.Ordinal))
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodeText, partIndex, pageNumber);
        }

        var outputGraphics = output.Graphics ?? PageGraphics.Empty;
        var sourceGraphics = source.Graphics ?? PageGraphics.Empty;
        var sourceObjects = sourceGraphics.ResourceNames.GetValueOrDefault("XObject") ?? [];
        var added = (outputGraphics.ResourceNames.GetValueOrDefault("XObject") ?? [])
            .Where(name => !sourceObjects.Contains(name, StringComparer.Ordinal))
            .ToList();
        var candidates = outputGraphics.Forms
            .Where(form => added.Contains(form.Name, StringComparer.Ordinal)
                && form.BoundingBox is { Count: 4 } box
                && Math.Abs(box[0]) <= tolerance
                && Math.Abs(box[1]) <= tolerance
                && Math.Abs(box[2] - region.WidthPt) <= tolerance
                && Math.Abs(box[3] - region.HeightPt) <= tolerance)
            .ToList();
        if (candidates.Count == 0)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodeMissing, partIndex, pageNumber);
        }

        if (candidates.Count > 1 || added.Count != 1)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodeUnexpected, partIndex, pageNumber);
        }

        var form = candidates[0];
        var drawings = outputGraphics.Drawings.Where(drawing => drawing.Name == form.Name).ToList();
        if (drawings.Count != 1)
        {
            return AssemblyVerdict.Failed(
                drawings.Count == 0 ? AssemblyFailureReasons.CodeMissing : AssemblyFailureReasons.CodeUnexpected,
                partIndex,
                pageNumber);
        }

        var matrix = drawings[0].Matrix;
        var expectedBottom = output.Page.Height - region.TopPt - region.HeightPt;
        if (Math.Abs(matrix[0] - 1) > 1e-6 || Math.Abs(matrix[1]) > 1e-6 || Math.Abs(matrix[2]) > 1e-6
            || Math.Abs(matrix[3] - 1) > 1e-6
            || Math.Abs(matrix[4] - region.LeftPt) > tolerance
            || Math.Abs(matrix[5] - expectedBottom) > tolerance)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodePlacement, partIndex, pageNumber);
        }

        var expected = code.ContentFor(output.Page, pageNumber, total, out _);
        if (expected is null)
        {
            return AssemblyVerdict.Failed(AssemblyFailureReasons.CodeValue, partIndex, pageNumber);
        }

        return form.Content.AsSpan().SequenceEqual(expected)
            ? null
            : AssemblyVerdict.Failed(AssemblyFailureReasons.CodeObject, partIndex, pageNumber);
    }

    private static bool SameDictionary(PageDictionary output, PageDictionary source)
    {
        foreach (var (key, value) in source.Entries)
        {
            if (!output.Entries.TryGetValue(key, out var written))
            {
                return false;
            }

            if (!PageDictionaryPolicy.AllowedChangedValues.Contains(key) && !string.Equals(value, written, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var key in output.Entries.Keys)
        {
            if (!source.Entries.ContainsKey(key) && !PageDictionaryPolicy.AllowedAddedKeys.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Inside(PrintedWord word, NumberingArea area)
    {
        var x = (word.Left + word.Right) / 2d;
        var y = (word.Top + word.Bottom) / 2d;
        return x >= area.LeftPt - StampAreaTolerancePt
            && x <= area.LeftPt + area.WidthPt + StampAreaTolerancePt
            && y >= area.TopPt - StampAreaTolerancePt
            && y <= area.TopPt + area.HeightPt + StampAreaTolerancePt;
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
