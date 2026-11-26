using System.Security.Cryptography;
using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly;

/// <summary>What one structural verification is given: the assembled document, the parts' commits and the page frame.</summary>
/// <param name="OutputPath">The assembled document's temporary file.</param>
/// <param name="Parts">The parts, in manifest order, with what their commits recorded.</param>
/// <param name="Frame">The template's page frame, in points.</param>
/// <param name="Numbering">The numbering contract the pages were stamped with.</param>
public sealed record AssemblyVerificationRequest(
    string OutputPath,
    IReadOnlyList<AssemblyPartExpectation> Parts,
    PageFrame Frame,
    PageNumbering Numbering);

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
    private const double TolerancePt = 0.05d;

    // The changes a stamped page may carry beside its source: the stamp's content
    // stream, the resources it needs, the transparency group some writers add, the
    // page box written to two decimals, and the page tree's own parent reference.
    private static readonly HashSet<string> AllowedChangedValues = new(StringComparer.Ordinal)
    {
        "Contents",
        "Resources",
        "Group",
        "MediaBox",
        "CropBox",
        "Parent",
    };

    private static readonly HashSet<string> AllowedAddedKeys = new(StringComparer.Ordinal)
    {
        "Group",
        "MediaBox",
        "CropBox",
    };

    /// <summary>Verifies one assembled document.</summary>
    /// <param name="request">The document, the parts and the contract.</param>
    /// <returns>The verdict, with the failed check and the page or part when it failed.</returns>
    public static AssemblyVerdict Verify(AssemblyVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parts);
        ArgumentNullException.ThrowIfNull(request.Numbering);
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

                if (!SameTextOutsideArea(outputPage, sourcePage, request.Numbering.Area))
                {
                    return AssemblyVerdict.Failed(AssemblyFailureReasons.Order, part.PartIndex, pageNumber);
                }

                if (StampVerdict(outputPage, request.Numbering, pageNumber, total, part.PartIndex) is { Passed: false } stamp)
                {
                    return stamp;
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

        return Math.Abs(width - frame.WidthPt) <= TolerancePt
            && Math.Abs(height - frame.HeightPt) <= TolerancePt;
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

    private static bool SameDictionary(PageDictionary output, PageDictionary source)
    {
        foreach (var (key, value) in source.Entries)
        {
            if (!output.Entries.TryGetValue(key, out var written))
            {
                return false;
            }

            if (!AllowedChangedValues.Contains(key) && !string.Equals(value, written, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var key in output.Entries.Keys)
        {
            if (!source.Entries.ContainsKey(key) && !AllowedAddedKeys.Contains(key))
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
        return x >= area.LeftPt - TolerancePt
            && x <= area.LeftPt + area.WidthPt + TolerancePt
            && y >= area.TopPt - TolerancePt
            && y <= area.TopPt + area.HeightPt + TolerancePt;
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
