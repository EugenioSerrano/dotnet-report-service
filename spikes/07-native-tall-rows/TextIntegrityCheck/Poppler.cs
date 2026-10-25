using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;

namespace TextIntegrityCheck;

/// <summary>One word of a printed page, with its box in PDF points (origin top-left, y downwards).</summary>
internal sealed record PdfWord(int Page, double XMin, double YMin, double XMax, double YMax, string Text)
{
    public double XCentre => (XMin + XMax) / 2;

    public double YCentre => (YMin + YMax) / 2;
}

/// <summary>What poppler read from one PDF: the page count, every page's size and every word.</summary>
internal sealed class PdfText
{
    public required int Pages { get; init; }

    public required List<(double Width, double Height)> PageSizes { get; init; }

    public required List<PdfWord> Words { get; init; }
}

/// <summary>
/// Runs the poppler tools as child processes. Every call has a deadline: on
/// expiry or cancellation the child is killed. The word list is parsed from
/// pdftotext's standard output with a streaming XML reader, never buffered whole.
/// </summary>
internal static class Poppler
{
    private const int MaxCapturedBytes = 64 * 1024;

    /// <summary>Runs a tool with a short output (version, pdfinfo) and returns stdout + stderr.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        using var process = Start(tool, arguments);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw new TimeoutException($"{tool} did not finish within {timeout.TotalSeconds:0} s.");
        }

        return (process.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>The page count from pdfinfo.</summary>
    public static async Task<int> PageCountAsync(string pdf, TimeSpan timeout, CancellationToken cancellation)
    {
        var (exit, output) = await RunAsync("pdfinfo", [pdf], timeout, cancellation);
        if (exit != 0)
        {
            throw new InvalidDataException($"pdfinfo exited {exit}: {FirstLine(output)}");
        }

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("Pages:", StringComparison.Ordinal))
            {
                return int.Parse(line["Pages:".Length..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidDataException("pdfinfo printed no page count.");
    }

    /// <summary>Every word with its page and box, from pdftotext -bbox -enc UTF-8 (streamed).</summary>
    public static async Task<PdfText> ExtractAsync(string pdf, TimeSpan timeout, CancellationToken cancellation)
    {
        var started = Stopwatch.StartNew();
        var pages = await PageCountAsync(pdf, timeout, cancellation);
        var remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("No time left for pdftotext.");
        }

        using var process = Start("pdftotext", ["-bbox", "-enc", "UTF-8", pdf, "-"]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(remaining);
        await using var registration = deadline.Token.Register(() => Kill(process));
        var stderr = ReadBoundedAsync(process.StandardError);
        var sizes = new List<(double, double)>();
        var words = new List<PdfWord>();
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(process.StandardOutput.BaseStream, settings);
            var more = await reader.ReadAsync();
            while (more)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "word")
                {
                    var xMin = Number(reader, "xMin");
                    var yMin = Number(reader, "yMin");
                    var xMax = Number(reader, "xMax");
                    var yMax = Number(reader, "yMax");

                    // Reading the content leaves the reader on the node after the element: no extra Read.
                    var text = await reader.ReadElementContentAsStringAsync();
                    words.Add(new PdfWord(sizes.Count, xMin, yMin, xMax, yMax, text));
                    more = !reader.EOF;
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "page")
                {
                    sizes.Add((Number(reader, "width"), Number(reader, "height")));
                }

                more = await reader.ReadAsync();
            }

            await process.WaitForExitAsync(deadline.Token);
        }
        catch (Exception e) when (deadline.IsCancellationRequested && e is XmlException or IOException or OperationCanceledException)
        {
            Kill(process);
            throw new TimeoutException($"pdftotext did not finish within {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"pdftotext exited {process.ExitCode}: {FirstLine(await stderr)}");
        }

        return new PdfText { Pages = pages, PageSizes = sizes, Words = words };
    }

    /// <summary>Renders the given pages to PNG at 100 dpi with pdftoppm (report images only).</summary>
    public static async Task<List<string>> RenderAsync(string pdf, IEnumerable<int> pages, string outDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var written = new List<string>();
        var name = Path.GetFileNameWithoutExtension(pdf);
        foreach (var page in pages)
        {
            var prefix = Path.Combine(outDirectory, $"{name}-page{page:D3}");
            var p = page.ToString(CultureInfo.InvariantCulture);
            var (exit, output) = await RunAsync("pdftoppm", ["-f", p, "-l", p, "-r", "100", "-png", "-singlefile", pdf, prefix], timeout, cancellation);
            if (exit != 0)
            {
                throw new InvalidDataException($"pdftoppm exited {exit}: {FirstLine(output)}");
            }

            written.Add(prefix + ".png");
        }

        return written;
    }

    private static Process Start(string tool, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
    }

    /// <summary>Reads a stream to its end, keeping at most the first 64 KiB.</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (kept.Length < MaxCapturedBytes)
            {
                kept.Append(buffer, 0, Math.Min(read, MaxCapturedBytes - kept.Length));
            }
        }

        return kept.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static double Number(XmlReader reader, string name) =>
        double.Parse(reader.GetAttribute(name) ?? throw new InvalidDataException($"Missing attribute {name}."), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
