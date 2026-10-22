using System.Text.RegularExpressions;

namespace PdfAssemblySpike;

/// <summary>
/// The structural check of one file: the exit code of "qpdf --check", its
/// verdict and the counts that make each tolerated warning auditable.
/// </summary>
internal sealed record QpdfCheckRecord(
    int ExitCode,
    string Verdict,
    int WarningLines,
    int ExemptWarningLines,
    string FailReason,
    string FirstOffendingLine)
{
    public bool Pass => Verdict is QpdfCheck.PassClean or QpdfCheck.PassExempt;
}

/// <summary>
/// Classifies the output of "qpdf --check". Exit 0 passes when no line is a
/// warning or an error. Exit 3 (warnings) passes only when every warning is
/// exactly the zlib message PDFsharp's compressed streams produce — the data
/// decodes, but the stream has no proper end-of-stream marker — and nothing else
/// is reported. Every comparison is ordinal and exact; any other text fails.
/// </summary>
internal static partial class QpdfCheck
{
    /// <summary>The one tolerated warning text, compared ordinally with the whole text after the offset.</summary>
    public const string ExemptTail = "input stream is complete but output may still be valid";

    /// <summary>The line qpdf prints last when it finished with warnings only.</summary>
    public const string ClosingLine = "qpdf: operation succeeded with warnings";

    public const string PassClean = "pass-clean";
    public const string PassExempt = "pass-exempt";
    public const string Fail = "fail";

    public const string OtherWarning = "other-warning";
    public const string ErrorLine = "error-line";
    public const string Unparseable = "unparseable";
    public const string Exit2 = "exit-2";

    private const string WarningPrefix = "WARNING:";
    private const string ErrorPrefix = "ERROR:";
    private const string CheckingPrefix = "checking ";
    private const string ErrorWord = "error";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>Runs "qpdf --check" on a file and classifies its output.</summary>
    public static QpdfCheckRecord Run(string path)
    {
        ToolResult result;
        try
        {
            result = ExternalTool.Run(Verifier.Qpdf, ["--check", path], Timeout, null, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new QpdfCheckRecord(-1, Fail, 0, 0, Unparseable, "qpdf could not be started: " + e.Message);
        }

        if (result.TimedOut)
        {
            return new QpdfCheckRecord(result.ExitCode, Fail, 0, 0, Unparseable, "qpdf --check timed out");
        }

        return Classify(path, result.ExitCode, result.StandardOutput, result.StandardError);
    }

    /// <summary>Classifies one transcript: exit code, standard output and standard error, kept apart.</summary>
    public static QpdfCheckRecord Classify(string path, int exitCode, string standardOutput, string standardError)
    {
        var output = Lines(standardOutput);
        var error = Lines(standardError);
        int warnings = output.Count(IsWarning) + error.Count(IsWarning);

        switch (exitCode)
        {
            case 0:
                {
                    // qpdf's own exit-0 summary contains the word "errors", so it is not scanned for it.
                    string? offending = output.Concat(error).FirstOrDefault(l => IsWarning(l) || IsError(l));
                    return offending is null
                        ? new QpdfCheckRecord(exitCode, PassClean, warnings, 0, string.Empty, string.Empty)
                        : new QpdfCheckRecord(exitCode, Fail, warnings, 0, Unparseable, offending);
                }

            case 3:
                return ClassifyWarnings(path, output, error, warnings);
            case 2:
                {
                    string offending = error.FirstOrDefault(IsError)
                        ?? output.FirstOrDefault(IsError)
                        ?? error.FirstOrDefault()
                        ?? output.FirstOrDefault()
                        ?? string.Empty;
                    return new QpdfCheckRecord(exitCode, Fail, warnings, 0, Exit2, offending);
                }

            default:
                return new QpdfCheckRecord(exitCode, Fail, warnings, 0, Unparseable,
                    error.FirstOrDefault() ?? output.FirstOrDefault() ?? string.Empty);
        }
    }

    private static QpdfCheckRecord ClassifyWarnings(string path, List<string> output, List<string> error, int warnings)
    {
        int exempt = error.Count(l => IsExempt(l, path));
        QpdfCheckRecord Failed(string reason, string line) => new(3, Fail, warnings, exempt, reason, line);

        // (a) No line of either stream mentions an error.
        string? errorLine = output.Concat(error).FirstOrDefault(l => l.Contains(ErrorWord, StringComparison.OrdinalIgnoreCase));
        if (errorLine is not null)
        {
            return Failed(ErrorLine, errorLine);
        }

        // Warnings and errors belong on standard error; one on standard output is not the expected shape.
        string? misplaced = output.FirstOrDefault(l => IsWarning(l) || IsError(l));
        if (misplaced is not null)
        {
            return Failed(IsError(misplaced) ? ErrorLine : OtherWarning, misplaced);
        }

        // (c) Every warning is exactly the tolerated message about this file.
        string? otherWarning = error.FirstOrDefault(l => IsWarning(l) && !IsExempt(l, path));
        if (otherWarning is not null)
        {
            return Failed(OtherWarning, otherWarning);
        }

        // (b) Standard output starts with the line naming the checked file.
        string expectedFirst = CheckingPrefix + path;
        if (output.Count == 0 || !string.Equals(output[0], expectedFirst, StringComparison.Ordinal))
        {
            return Failed(Unparseable, output.Count == 0 ? "(no standard output)" : output[0]);
        }

        // (c) At least one warning.
        if (exempt == 0)
        {
            return Failed(Unparseable, "(no warning line)");
        }

        // (d) Apart from the warnings, exactly one more line: the closing line, last.
        var rest = error.Where(l => !IsWarning(l)).ToList();
        if (rest.Count != 1 || !string.Equals(rest[0], ClosingLine, StringComparison.Ordinal))
        {
            string first = rest.FirstOrDefault(l => !string.Equals(l, ClosingLine, StringComparison.Ordinal))
                ?? (rest.Count == 0 ? "(no closing line)" : rest[^1]);
            return Failed(Unparseable, first);
        }

        if (!string.Equals(error[^1], ClosingLine, StringComparison.Ordinal))
        {
            return Failed(Unparseable, "(closing line not last) " + error[^1]);
        }

        return new QpdfCheckRecord(3, PassExempt, warnings, exempt, string.Empty, string.Empty);
    }

    /// <summary>
    /// True when the line has the warning shape with an offset, names this exact
    /// file and carries exactly the tolerated text after the offset.
    /// </summary>
    private static bool IsExempt(string line, string path)
    {
        var match = WarningPattern().Match(line);
        return match.Success
            && string.Equals(match.Groups["file"].Value, path, StringComparison.Ordinal)
            && string.Equals(match.Groups["tail"].Value, ExemptTail, StringComparison.Ordinal);
    }

    private static bool IsWarning(string line) => line.StartsWith(WarningPrefix, StringComparison.Ordinal);

    private static bool IsError(string line) => line.StartsWith(ErrorPrefix, StringComparison.Ordinal);

    /// <summary>Lines split on LF, a trailing CR removed, empty lines dropped.</summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Length > 0 && raw[^1] == '\r' ? raw[..^1] : raw;
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    [GeneratedRegex(@"^WARNING: (?<file>.+?) \((?:object [0-9]+ [0-9]+, )?offset (?<offset>[0-9]+)\): (?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WarningPattern();
}
