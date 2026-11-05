using System.Diagnostics;
using System.Globalization;
using System.Xml;

namespace ReportService.PdfOracle;

/// <summary>
/// Reads the word boxes of a printed document from the output of the text tool:
/// one page element per page, one word element per word with the four edges of its
/// box. The output is read as it streams, so a large document is never held twice.
/// </summary>
internal static class BboxText
{
    /// <summary>Reads the text tool's word output; <paramref name="reportedPages"/> comes from the file's own information.</summary>
    public static PdfExtraction Parse(Stream stream, int reportedPages)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            CloseInput = false,
        };

        var sizes = new List<(double Width, double Height)>();
        var words = new List<PdfWord>();
        var page = 0;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "page":
                        page++;
                        sizes.Add((Edge(reader, "width"), Edge(reader, "height")));
                        break;
                    case "word":
                        if (page == 0)
                        {
                            throw new OracleInstrumentException("A word is reported before any page.");
                        }

                        var xMin = Edge(reader, "xMin");
                        var yMin = Edge(reader, "yMin");
                        var xMax = Edge(reader, "xMax");
                        var yMax = Edge(reader, "yMax");
                        var text = reader.ReadElementContentAsString();
                        words.Add(new PdfWord(page, text, xMin, yMin, xMax, yMax));
                        break;
                }
            }
        }
        catch (XmlException exception)
        {
            throw new OracleInstrumentException("The text tool's output could not be read.", exception);
        }

        if (sizes.Count == 0)
        {
            throw new OracleInstrumentException("The text tool reported no page.");
        }

        return new PdfExtraction(sizes.Count, reportedPages, sizes, words);
    }

    private static double Edge(XmlReader reader, string name)
    {
        var text = reader.GetAttribute(name)
            ?? throw new OracleInstrumentException($"A box edge '{name}' is missing from the text tool's output.");
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Reads a printed PDF with the text tools of the instrument's own image: the file's
/// page count from its information, every word with its box from the text output.
/// Nothing of the product and no PDF library takes part: the reading is the tool's.
/// No tool is allowed to decide when the read ends: one that does not answer within
/// <see cref="ToolTimeout"/> is stopped and the read is refused, and the caller's
/// cancellation token stops it the same way, so a read that stops is never mistaken
/// for a document that passed.
/// </summary>
internal static class PopplerText
{
    /// <summary>How long one text tool may run before the read is refused.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Reads <paramref name="pdfPath"/>.</summary>
    public static Task<PdfExtraction> ReadAsync(string pdfPath, CancellationToken cancellationToken = default) =>
        ReadAsync(pdfPath, ToolTimeout, cancellationToken);

    /// <summary>Reads <paramref name="pdfPath"/>, giving each tool <paramref name="timeout"/> to answer.</summary>
    internal static async Task<PdfExtraction> ReadAsync(string pdfPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        cancellationToken.ThrowIfCancellationRequested();

        var reportedPages = await PageCountAsync(pdfPath, timeout, cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo("pdftotext")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-bbox", "-enc", "UTF-8", pdfPath, "-" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Start(startInfo, "The text tool");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var stop = limit.Token.Register(() => Stop(process));
        try
        {
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            var extraction = BboxText.Parse(process.StandardOutput.BaseStream, reportedPages);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new OracleInstrumentException(
                    $"The text tool ended with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {Truncate(await error.ConfigureAwait(false))}");
            }

            return extraction;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            throw Ended(cancellationToken, timeout, "The text tool");
        }
        catch (OracleInstrumentException) when (limit.IsCancellationRequested)
        {
            throw Ended(cancellationToken, timeout, "The text tool");
        }
        finally
        {
            Stop(process);
        }
    }

    private static async Task<int> PageCountAsync(string pdfPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("pdfinfo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(pdfPath);

        using var process = Start(startInfo, "The file information tool");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var stop = limit.Token.Register(() => Stop(process));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new OracleInstrumentException(
                    $"The file information tool ended with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {Truncate(await error.ConfigureAwait(false))}");
            }

            return PageCount(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            throw Ended(cancellationToken, timeout, "The file information tool");
        }
        catch (OracleInstrumentException) when (limit.IsCancellationRequested)
        {
            throw Ended(cancellationToken, timeout, "The file information tool");
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>The page count the file's own information reports.</summary>
    internal static int PageCount(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Pages:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = trimmed["Pages:".Length..].Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) && pages > 0)
            {
                return pages;
            }
        }

        throw new OracleInstrumentException("The file information tool reported no page count.");
    }

    /// <summary>Starts one tool by its fixed name; a tool the image does not hold is an instrument error.</summary>
    private static Process Start(ProcessStartInfo startInfo, string tool)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new OracleInstrumentException($"{tool} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new OracleInstrumentException($"{tool} could not be started.", exception);
        }
    }

    /// <summary>Stops a tool that outlived its time, and one that outlived a failed read.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The tool ended between the check and the kill: there is nothing left to stop.
        }
    }

    /// <summary>Why a read stopped: the caller's cancellation, or the tool's own time running out.</summary>
    internal static Exception Ended(CancellationToken cancellationToken, TimeSpan timeout, string tool) =>
        cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new OracleInstrumentException($"{tool} did not answer within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.");

    private static string Truncate(string message) => message.Length <= 400 ? message.Trim() : message[..400].Trim();
}
