using System.Text;
using System.Text.Json;

namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The command the rendering suite runs: its one verb, its three options, the
/// verdict it writes and the three exit codes. The reading of the PDF is given to
/// the command here, so the cases hold the command itself and need no PDF tool and
/// no printed file — the tools of the instrument belong to its own image.
/// </summary>
public sealed class ProgramTests : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "report-service-pdf-oracle", Guid.NewGuid().ToString("N"));

    public ProgramTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task TheCommandWithoutAVerdictIsAUsageError()
    {
        var (exitCode, _, error) = await Run([]);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: ReportService.PdfOracle verdict", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerbTheCommandDoesNotKnowIsAUsageError()
    {
        var (exitCode, _, error) = await Run(["judge", "--pdf", "document.pdf"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: ReportService.PdfOracle verdict", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOptionWithoutAValueIsAUsageError()
    {
        var (exitCode, _, error) = await Run(["verdict", "--pdf"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("The option '--pdf' holds no value.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingOptionIsAUsageError()
    {
        var (exitCode, _, error) = await Run(["verdict", "--pdf", "document.pdf", "--fixture", Fixture()]);

        Assert.Equal(2, exitCode);
        Assert.Contains("The option '--expectation' is required.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrailingOptionWithoutAValueIsAUsageError()
    {
        var (exitCode, _, error) = await Run(["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation(), "--out"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("The option '--out' holds no value.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentTheReadingCannotUseExitsOne()
    {
        var (exitCode, output, error) = await Run(
            ["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation()],
            read: (_, _) => throw new OracleInstrumentException("The text tool could not be started."));

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("The text tool could not be started.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASnapshotTheJudgeCannotReadExitsOne()
    {
        var (exitCode, _, error) = await Run(["verdict", "--pdf", "document.pdf", "--fixture", Write("snapshot.json", "not a snapshot"), "--expectation", Expectation()]);

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task ADeclarationThatIsNotADeclarationExitsOne()
    {
        var (exitCode, _, error) = await Run(["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Write("declaration.json", "[]")]);

        Assert.Equal(1, exitCode);
        Assert.Contains("The declaration is not a JSON object.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileTheVerdictCannotBeWrittenToExitsOne()
    {
        var destination = Path.Combine(directory, "no-such-directory", "verdict.json");

        var (exitCode, _, _) = await Run(
            ["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation(), "--out", destination],
            read: (_, _) => Task.FromResult(SyntheticDocument.Extraction()));

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AReadingVerdictExitsZeroAndPrintsOneVerdict()
    {
        var (exitCode, output, error) = await Run(
            ["verdict", "--pdf", "C:\\printed\\document.pdf", "--fixture", Fixture(), "--expectation", Expectation()],
            read: (_, _) => Task.FromResult(SyntheticDocument.Extraction()));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        using var verdict = JsonDocument.Parse(output);
        Assert.True(verdict.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("document.pdf", verdict.RootElement.GetProperty("file").GetString());
    }

    [Fact]
    public async Task ADocumentThatFailsARuleIsAVerdictAndStillExitsZero()
    {
        var words = SyntheticDocument.Extraction().Words
            .Select(word => string.Equals(word.Text, "100.00", StringComparison.Ordinal) ? word with { Text = "190.00" } : word)
            .ToList();

        var (exitCode, output, _) = await Run(
            ["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation()],
            read: (_, _) => Task.FromResult(SyntheticDocument.Extraction().WithWords(words)));

        Assert.Equal(0, exitCode);
        using var verdict = JsonDocument.Parse(output);
        Assert.False(verdict.RootElement.GetProperty("passed").GetBoolean());
        var failure = Assert.Single(verdict.RootElement.GetProperty("failures").EnumerateArray());
        Assert.Equal("TI-4", failure.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task TheVerdictIsWrittenToTheNamedFileWithoutAByteOrderMark()
    {
        var destination = Path.Combine(directory, "verdict.json");

        var (exitCode, output, _) = await Run(
            ["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation(), "--out", destination],
            read: (_, _) => Task.FromResult(SyntheticDocument.Extraction()));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
        var bytes = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);

        // A full round trip: strict decoding refuses an invalid sequence, re-encoding
        // gives back every byte, and the text begins with the document itself — not
        // with a byte order mark, which would be a character before it.
        var text = StrictUtf8.GetString(bytes);
        Assert.Equal(bytes, Utf8.GetBytes(text));
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        using var verdict = JsonDocument.Parse(bytes);
        Assert.True(verdict.RootElement.GetProperty("passed").GetBoolean());
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadingTheCallerCancelsIsNoVerdictAndNoExitCodeOfTheCommand()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var output = new StringWriter();
        var error = new StringWriter();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Program.RunAsync(
            ["verdict", "--pdf", "document.pdf", "--fixture", Fixture(), "--expectation", Expectation()],
            (_, _) => throw new OperationCanceledException(),
            output,
            error,
            cancellation.Token));

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> Run(
        string[] args,
        Func<string, CancellationToken, Task<PdfExtraction>>? read = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await Program.RunAsync(
            args,
            read ?? ((_, _) => throw new OracleInstrumentException("The text tool could not be started.")),
            output,
            error,
            TestContext.Current.CancellationToken);

        return (exitCode, output.ToString(), error.ToString());
    }

    private string Fixture() => Write("snapshot.json", SyntheticDocument.FixtureJson);

    private string Expectation() => Write("expectation.json", SyntheticDocument.ExpectationJson);

    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
