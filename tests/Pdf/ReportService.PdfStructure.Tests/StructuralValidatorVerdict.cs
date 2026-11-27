using System.Text.RegularExpressions;

namespace ReportService.PdfStructure.Tests;

/// <summary>The verdict of the strict structural validator (<c>qpdf --check</c>) over one assembled document.</summary>
/// <param name="ExitCode">The validator's exit code.</param>
/// <param name="Passed">Whether the document passes.</param>
/// <param name="ExemptWarnings">The warnings that carry the one tolerated message.</param>
/// <param name="Reason">Why it fails, when it fails.</param>
public sealed record StructuralValidatorVerdict(int ExitCode, bool Passed, int ExemptWarnings, string? Reason);

/// <summary>
/// The strict structural validator's rule for documents the PDF writing library
/// assembled. Every stream that library compresses makes <c>qpdf --check</c> exit 3
/// with one zlib warning per stream: "input stream is complete but output may still
/// be valid". The data decodes; the stream lacks a proper end marker. The rule
/// accepts <b>exactly that message and nothing else</b>: exit 0 with no warning or
/// error line; or exit 3 when every <c>WARNING:</c> line names the checked file, has
/// an offset and ends with exactly that text, the only other line on standard error
/// is the closing line, last, standard output starts with the line naming the file,
/// and no line mentions an error. Exit 2, any other warning, an error line or any
/// other shape fails. The exemption is never proof of stream integrity: it only
/// keeps the validator usable on this library's output.
/// </summary>
public static partial class StructuralValidator
{
    /// <summary>The one tolerated warning text.</summary>
    public const string ExemptTail = "input stream is complete but output may still be valid";

    /// <summary>The validator's closing line after warnings.</summary>
    public const string ClosingLine = "qpdf: operation succeeded with warnings";

    /// <summary>Classifies one transcript; standard output and standard error kept apart.</summary>
    /// <param name="path">The checked file, as given to the validator.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="standardOutput">Standard output.</param>
    /// <param name="standardError">Standard error.</param>
    /// <returns>The verdict.</returns>
    public static StructuralValidatorVerdict Classify(string path, int exitCode, string standardOutput, string standardError)
    {
        var output = Lines(standardOutput);
        var error = Lines(standardError);
        switch (exitCode)
        {
            case 0:
                // The exit-0 summary itself says "No syntax or stream encoding errors", so
                // it is not scanned for the word; a warning or error line still fails.
                return output.Concat(error).FirstOrDefault(line => IsWarning(line) || IsError(line)) is { } offending
                    ? new StructuralValidatorVerdict(0, false, 0, "unexpected line: " + offending)
                    : new StructuralValidatorVerdict(0, true, 0, null);
            case 3:
                return Warnings(path, output, error);
            default:
                return new StructuralValidatorVerdict(exitCode, false, 0, "exit " + exitCode);
        }
    }

    private static StructuralValidatorVerdict Warnings(string path, List<string> output, List<string> error)
    {
        var exempt = error.Count(line => IsExempt(line, path));
        StructuralValidatorVerdict Failed(string reason) => new(3, false, exempt, reason);

        if (output.Concat(error).FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) is { } errorLine)
        {
            return Failed("error line: " + errorLine);
        }

        if (output.FirstOrDefault(line => IsWarning(line) || IsError(line)) is { } misplaced)
        {
            return Failed("warning on standard output: " + misplaced);
        }

        if (error.FirstOrDefault(line => IsWarning(line) && !IsExempt(line, path)) is { } other)
        {
            return Failed("other warning: " + other);
        }

        if (output.Count == 0 || !string.Equals(output[0], "checking " + path, StringComparison.Ordinal))
        {
            return Failed("standard output does not name the checked file");
        }

        if (exempt == 0)
        {
            return Failed("exit 3 with no warning line");
        }

        var rest = error.Where(line => !IsWarning(line)).ToList();
        if (rest.Count != 1 || !string.Equals(rest[0], ClosingLine, StringComparison.Ordinal)
            || !string.Equals(error[^1], ClosingLine, StringComparison.Ordinal))
        {
            return Failed("the closing line is missing, repeated or not last");
        }

        return new StructuralValidatorVerdict(3, true, exempt, null);
    }

    private static bool IsExempt(string line, string path)
    {
        var match = WarningPattern().Match(line);
        return match.Success
            && string.Equals(match.Groups["file"].Value, path, StringComparison.Ordinal)
            && string.Equals(match.Groups["tail"].Value, ExemptTail, StringComparison.Ordinal);
    }

    private static bool IsWarning(string line) => line.StartsWith("WARNING:", StringComparison.Ordinal);

    private static bool IsError(string line) => line.StartsWith("ERROR:", StringComparison.Ordinal);

    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    [GeneratedRegex(@"^WARNING: (?<file>.+?) \((?:object [0-9]+ [0-9]+, )?offset (?<offset>[0-9]+)\): (?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WarningPattern();
}
