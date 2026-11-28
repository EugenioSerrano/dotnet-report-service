using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ReportService.Rendering;
using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The faster authoring loop: watching a package re-renders on change and keeps watching
/// across failures, and a render can use a named data file, which the take checks and
/// renders exactly as it does the sample. The printer replays a committed golden, so a
/// case either keeps the printed text the golden's or is refused before anything prints.
/// </summary>
public sealed class AuthoringLoopTests
{
    private const string Package = "pilot-handlebars";

    private const string Marker = "/* authoring-loop-marker */";

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A saved change to a package file renders again and the preview files hold it.</summary>
    [Fact]
    public async Task ASavedChangeRendersAgainAndThePreviewHoldsIt()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var outputDirectory = Path.Combine(root, "out");
        var printer = Printer();
        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, outputDirectory), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.Equal(1, printer.Calls);
        Assert.DoesNotContain(Marker, ReadPreviewStylesheet(outputDirectory), StringComparison.Ordinal);

        var stylesheet = Path.Combine(package, "html", "styles.css");
        File.AppendAllText(stylesheet, "\n" + Marker + "\n", Utf8NoBom);
        changes.Writer.TryWrite(stylesheet);

        await Until(() => printer.Calls == 2 && Count(output.Text, "rendered " + Package) == 2).ConfigureAwait(true);
        Assert.Contains(Marker, ReadPreviewStylesheet(outputDirectory), StringComparison.Ordinal);
        Assert.False(watch.IsCompleted);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>The same loop through the file system: saving the file is the only signal.</summary>
    [Fact]
    public async Task SavingAFileIsNoticedByTheFileSystemWatch()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var outputDirectory = Path.Combine(root, "out");
        var printer = Printer();
        var output = new SynchronizedWriter();
        using var stop = new CancellationTokenSource();

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, outputDirectory), output, stop.Token);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);

        File.AppendAllText(Path.Combine(package, "html", "styles.css"), "\n" + Marker + "\n", Utf8NoBom);

        await Until(() => ReadPreviewStylesheet(outputDirectory).Contains(Marker, StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.True(printer.Calls >= 2);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>A change the checks refuse prints the refusal and keeps watching; restoring the file renders again.</summary>
    [Fact]
    public async Task ARefusedChangeKeepsWatchingAndTheNextChangeRenders()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var outputDirectory = Path.Combine(root, "out");
        var printer = Printer();
        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, outputDirectory), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);

        var rowPath = Path.Combine(package, "html", "row.html.hbs");
        var row = File.ReadAllText(rowPath);
        var broken = row.Replace("<td class=\"num\">{{item.sequence}}</td>", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(row, broken);
        File.WriteAllText(rowPath, broken, Utf8NoBom);

        // The refusal a single render of the same state prints.
        var single = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(1, await Tool(Printer()).RenderAsync(new RenderRequest(package, Path.Combine(root, "single")), single, TestContext.Current.CancellationToken).ConfigureAwait(true));

        changes.Writer.TryWrite(rowPath);
        await Until(() => output.Text.Contains(single.ToString(), StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.Equal(1, printer.Calls);
        Assert.False(watch.IsCompleted);

        File.WriteAllText(rowPath, row, Utf8NoBom);
        changes.Writer.TryWrite(rowPath);
        await Until(() => Count(output.Text, "rendered " + Package) == 2).ConfigureAwait(true);
        Assert.Equal(2, printer.Calls);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>A burst of changes renders once; a change during a render renders exactly once more.</summary>
    [Fact]
    public async Task ABurstRendersOnceAndAChangeDuringARenderRendersOnceMore()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var printer = new GatedPrinter(Printer());
        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();
        var stylesheet = Path.Combine(package, "html", "styles.css");

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, Path.Combine(root, "out")), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);

        for (var index = 0; index < 5; index++)
        {
            changes.Writer.TryWrite(stylesheet);
        }

        await Until(() => Count(output.Text, "rendered " + Package) == 2).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, printer.Calls);

        // Hold the next print open, change the package while it runs, then let it finish.
        printer.Hold();
        changes.Writer.TryWrite(stylesheet);
        await Until(() => printer.Waiting).ConfigureAwait(true);
        changes.Writer.TryWrite(stylesheet);
        changes.Writer.TryWrite(stylesheet);
        printer.Release();

        await Until(() => Count(output.Text, "rendered " + Package) == 4).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(4, printer.Calls);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>
    /// Watching from inside the package without --out puts the preview in the watched tree;
    /// writing it is never a reason to render, through the real file-system watch.
    /// </summary>
    [Fact]
    public async Task ThePreviewWrittenInsideTheWatchedPackageDoesNotRenderAgain()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var printer = Printer();
        var output = new SynchronizedWriter();
        using var stop = new CancellationTokenSource();
        var request = new RenderRequest(package, WorkingDirectory: package);
        var preview = TemplateAuthoringTool.PreviewDirectoryOf(request)!;
        Assert.StartsWith(package, preview, StringComparison.OrdinalIgnoreCase);

        var watch = Watcher(printer).RunAsync(request, output, stop.Token);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.True(File.Exists(Path.Combine(preview, Package + "-1.0.0.html")));

        File.AppendAllText(Path.Combine(package, "html", "styles.css"), "\n" + Marker + "\n", Utf8NoBom);
        await Until(() => printer.Calls == 2).ConfigureAwait(true);

        // The second render rewrote the preview inside the package: no third render follows.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, printer.Calls);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>The output location is resolved again when the manifest's version changes.</summary>
    [Fact]
    public void ThePreviewLocationFollowsTheManifest()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var request = new RenderRequest(package, WorkingDirectory: root);
        Assert.Equal(Path.Combine(root, "artifacts", "template-preview", Package, "1.0.0"), TemplateAuthoringTool.PreviewDirectoryOf(request));

        var manifest = Path.Combine(package, "template.yaml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("1.0.0", "1.0.1", StringComparison.Ordinal), Utf8NoBom);
        Assert.Equal(Path.Combine(root, "artifacts", "template-preview", Package, "1.0.1"), TemplateAuthoringTool.PreviewDirectoryOf(request));

        Assert.Equal(Path.Combine(root, "named"), TemplateAuthoringTool.PreviewDirectoryOf(new RenderRequest(package, Path.Combine(root, "named"))));
    }

    /// <summary>
    /// A watched render that throws - here the manifest held open by another writer, as an
    /// editor can - is that render's failure: its type is printed, the watch goes on and the
    /// next change renders.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedExceptionInAWatchedRenderIsThatRendersFailure()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var printer = Printer();
        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();
        var manifest = Path.Combine(package, "template.yaml");

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, Path.Combine(root, "out")), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);

        using (new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            changes.Writer.TryWrite(manifest);
            await Until(() => output.Text.Contains("internal-error: IOException in watch", StringComparison.Ordinal)).ConfigureAwait(true);
            Assert.False(watch.IsCompleted);
        }

        Assert.DoesNotContain(manifest, output.Text, StringComparison.OrdinalIgnoreCase);
        changes.Writer.TryWrite(manifest);
        await Until(() => Count(output.Text, "rendered " + Package) == 2).ConfigureAwait(true);
        Assert.Equal(2, printer.Calls);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>A save made while the first render runs is seen: the file-system watch is already on.</summary>
    [Fact]
    public async Task ASaveDuringTheFirstRenderIsSeen()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var outputDirectory = Path.Combine(root, "out");
        var printer = new GatedPrinter(Printer());
        var output = new SynchronizedWriter();
        using var stop = new CancellationTokenSource();
        printer.Hold();

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, outputDirectory), output, stop.Token);
        await Until(() => printer.Waiting).ConfigureAwait(true);
        File.AppendAllText(Path.Combine(package, "html", "styles.css"), "\n" + Marker + "\n", Utf8NoBom);
        printer.Release();

        await Until(() => printer.Calls >= 2 && ReadPreviewStylesheet(outputDirectory).Contains(Marker, StringComparison.Ordinal)).ConfigureAwait(true);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>A first render that is a usage error is reported and nothing is watched.</summary>
    [Fact]
    public async Task AFirstRenderThatIsAUsageErrorNeverStartsWatching()
    {
        var root = TestPaths.Temp();
        var printer = Printer();
        var output = new SynchronizedWriter();
        var usage = new StringWriter(CultureInfo.InvariantCulture);
        var changes = Channel.CreateUnbounded<string>();

        var code = await Watcher(printer)
            .RunAsync(new RenderRequest(Path.Combine(root, "missing")), output, TestContext.Current.CancellationToken, changes.Reader, usage)
            .ConfigureAwait(true);

        Assert.Equal(2, code);
        Assert.Contains("does not exist", usage.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.Text);
        Assert.Equal(0, printer.Calls);
    }

    /// <summary>Each render's lines are written before the next change, and cancelling stops with 0.</summary>
    [Fact]
    public async Task EachRenderIsWrittenWhenItEndsAndCancellingStopsWithZero()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();

        var watch = Watcher(Printer()).RunAsync(new RenderRequest(package, Path.Combine(root, "out")), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.Contains("rendered " + Package + " 1.0.0", output.Text, StringComparison.Ordinal);
        Assert.True(output.Flushes >= 1);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>The named data file, not the sample, is what reaches composition and the print.</summary>
    [Fact]
    public async Task TheNamedDataFileIsWhatIsComposedAndPrinted()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var sampleBefore = File.ReadAllBytes(Path.Combine(package, "sample.json"));
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "sample.json")))!;
        data["items"]!.AsArray()[0]!["description"] = "Distinct named data value";
        var dataFile = Path.Combine(root, "other.json");
        File.WriteAllText(dataFile, data.ToJsonString(), Utf8NoBom);

        var printer = Printer();
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, Path.Combine(root, "out"), dataFile), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // The printed document carries the named file's value; the golden page then does not.
        Assert.NotNull(printer.Document);
        Assert.Contains("Distinct named data value", printer.Document, StringComparison.Ordinal);
        var sampleRender = Printer();
        await Tool(sampleRender).RenderAsync(new RenderRequest(package, Path.Combine(root, "sample-out")), new StringWriter(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain("Distinct named data value", sampleRender.Document, StringComparison.Ordinal);
        Assert.NotEqual(sampleRender.Document, printer.Document);
        Assert.Equal(1, code);
        Assert.Contains("does not carry", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Distinct named data value", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(sampleBefore, File.ReadAllBytes(Path.Combine(package, "sample.json")));
    }

    /// <summary>A valid named data file renders and the package is left as it was.</summary>
    [Fact]
    public async Task AValidNamedDataFileRenders()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var samplePath = Path.Combine(package, "sample.json");
        var sampleBefore = File.ReadAllBytes(samplePath);
        var dataFile = Path.Combine(root, "same-values.json");
        File.WriteAllText(dataFile, JsonNode.Parse(File.ReadAllText(samplePath))!.ToJsonString(), Utf8NoBom);
        Assert.NotEqual(sampleBefore, File.ReadAllBytes(dataFile));

        var outputDirectory = Path.Combine(root, "out");
        var printer = Printer();
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory, dataFile), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, code);
        Assert.Contains("rendered " + Package + " 1.0.0", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(printer.Document, File.ReadAllText(Path.Combine(outputDirectory, Package + "-1.0.0.html")));
        Assert.Equal(sampleBefore, File.ReadAllBytes(samplePath));
        Assert.DoesNotContain(Path.Combine("package", "sample.json"), writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A named data file the schema refuses gets the sample check's sentence, before anything prints.</summary>
    [Fact]
    public async Task ANamedDataFileTheSchemaRefusesIsRefusedByTheSampleCheck()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "sample.json")))!;
        data["items"]!.AsArray()[0]!["quantity"] = "ten";
        var dataFile = Path.Combine(root, "invalid.json");
        File.WriteAllText(dataFile, data.ToJsonString(), Utf8NoBom);

        var (code, text, printer, outputDirectory) = await RenderWithData(package, root, dataFile).ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.StartsWith("TEMPLATE_INVALID: sample.json:", text, StringComparison.Ordinal);
        Assert.Contains("does not satisfy the schema", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ten", text, StringComparison.Ordinal);
        Assert.Equal(0, printer.Calls);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A named data file that is not in the data format is refused by the sample check.</summary>
    [Fact]
    public async Task ANamedDataFileOutsideTheDataFormatIsRefused()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var dataFile = Path.Combine(root, "not-the-format.json");
        File.WriteAllText(dataFile, "{\"contractVersion\":\"1.0\",\"items\":[]}", Utf8NoBom);

        var (code, text, printer, outputDirectory) = await RenderWithData(package, root, dataFile).ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.StartsWith("TEMPLATE_INVALID: sample.json:", text, StringComparison.Ordinal);
        Assert.Equal(0, printer.Calls);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A named data file over the block bound is refused before anything prints.</summary>
    [Fact]
    public async Task ANamedDataFileOverTheBoundIsRefused()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "sample.json")))!;
        var items = new JsonArray();
        for (var sequence = 1; sequence <= 2001; sequence++)
        {
            items.Add(new JsonObject
            {
                ["sequence"] = sequence,
                ["description"] = "Item " + sequence.ToString(CultureInfo.InvariantCulture),
                ["quantity"] = 1,
                ["unitPrice"] = 100.00m,
                ["amount"] = 100.00m,
            });
        }

        data["items"] = items;
        data["header"]!["totals"]!["recordCount"] = 2001;
        data["header"]!["totals"]!["amount"] = 200100.00m;
        var dataFile = Path.Combine(root, "over-bound.json");
        File.WriteAllText(dataFile, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var (code, text, printer, outputDirectory) = await RenderWithData(package, root, dataFile).ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Contains("one block holds at most", text, StringComparison.Ordinal);
        Assert.Equal(0, printer.Calls);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A named data file that does not exist is a usage error.</summary>
    [Fact]
    public async Task AMissingNamedDataFileIsAUsageError()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);

        var (code, text, printer, _) = await RenderWithData(package, root, Path.Combine(root, "absent.json")).ConfigureAwait(true);

        Assert.Equal(2, code);
        Assert.Contains("the data file", text, StringComparison.Ordinal);
        Assert.Equal(0, printer.Calls);
    }

    /// <summary>Watching with a named data file renders again when that file changes.</summary>
    [Fact]
    public async Task WatchingWithANamedDataFileRendersWhenItChanges()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var dataFolder = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var dataFile = Path.Combine(dataFolder, "same-values.json");
        var content = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "sample.json")))!.ToJsonString();
        File.WriteAllText(dataFile, content, Utf8NoBom);
        var printer = Printer();
        var output = new SynchronizedWriter();
        using var stop = new CancellationTokenSource();

        var watch = Watcher(printer).RunAsync(new RenderRequest(package, Path.Combine(root, "out"), dataFile), output, stop.Token);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.Equal(1, printer.Calls);

        File.WriteAllText(dataFile, content + "\n", Utf8NoBom);
        await Until(() => printer.Calls >= 2 && Count(output.Text, "rendered " + Package) >= 2).ConfigureAwait(true);

        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));
    }

    /// <summary>The built tool refuses an invalid watch or --data command line with exit 2 and the usage block.</summary>
    /// <param name="arguments">The command line after the tool's name, separated by '|'.</param>
    [Theory]
    [InlineData("watch")]
    [InlineData("watch|{missing}")]
    [InlineData("watch|{package}|--unknown|x")]
    [InlineData("watch|{package}|--data")]
    [InlineData("render|{package}|--data|{missing}")]
    [InlineData("watch|{package}|--data|{missing}")]
    public async Task TheBuiltToolRefusesAnInvalidCommandLine(string arguments)
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(Package, root);
        var tool = Path.Combine(AppContext.BaseDirectory, "ReportService.Samples.TemplateAuthoring.dll");
        Assert.True(File.Exists(tool));

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add(tool);
        foreach (var argument in arguments.Split('|'))
        {
            start.ArgumentList.Add(argument
                .Replace("{package}", package, StringComparison.Ordinal)
                .Replace("{missing}", Path.Combine(root, "missing"), StringComparison.Ordinal));
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standard = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Usage:", await error.ConfigureAwait(true), StringComparison.Ordinal);
        Assert.Contains("watch <package-folder>", await error.ConfigureAwait(true), StringComparison.Ordinal);
        Assert.DoesNotContain(PackageWatcher.WatchingLine, await standard.ConfigureAwait(true), StringComparison.Ordinal);
    }

    /// <summary>A render of a named data file prints the same warnings, right before its result line, as the sample's render.</summary>
    [Fact]
    public async Task ANamedDataRenderPrintsItsWarningsBeforeTheResultLine()
    {
        var root = TestPaths.Temp();
        var package = TrappedPackage(root);
        var dataFile = Path.Combine(root, "same-values.json");
        File.WriteAllText(dataFile, JsonNode.Parse(File.ReadAllText(Path.Combine(package, "sample.json")))!.ToJsonString(), Utf8NoBom);

        var sample = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await Tool(Printer()).RenderAsync(new RenderRequest(package, Path.Combine(root, "sample-out")), sample, TestContext.Current.CancellationToken).ConfigureAwait(true));
        var (code, text, _, _) = await RenderWithData(package, root, dataFile).ConfigureAwait(true);

        Assert.Equal(0, code);
        var lines = Lines(text);
        var rendered = Array.FindIndex(lines, line => line.StartsWith("rendered ", StringComparison.Ordinal));
        Assert.Equal(lines.Length - 1, rendered);
        Assert.True(rendered > 0);
        Assert.All(lines[..rendered], line => Assert.StartsWith("warning: ", line, StringComparison.Ordinal));
        Assert.StartsWith("warning: culture-number-in-attribute ", lines[0], StringComparison.Ordinal);
        Assert.Equal(Lines(sample.ToString()).Where(IsWarning), lines.Where(IsWarning));
    }

    /// <summary>Each render of a watch prints its own one set of warnings, exactly as a single render does.</summary>
    [Fact]
    public async Task EachWatchedRenderPrintsOneSetOfWarnings()
    {
        var root = TestPaths.Temp();
        var package = TrappedPackage(root);
        var outputDirectory = Path.Combine(root, "out");
        var single = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await Tool(Printer()).RenderAsync(new RenderRequest(package, outputDirectory), single, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Contains(Lines(single.ToString()), IsWarning);

        var output = new SynchronizedWriter();
        var changes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();
        var watch = Watcher(Printer()).RunAsync(new RenderRequest(package, outputDirectory), output, stop.Token, changes.Reader);
        await Until(() => output.Text.Contains(PackageWatcher.WatchingLine, StringComparison.Ordinal)).ConfigureAwait(true);
        changes.Writer.TryWrite(Path.Combine(package, "html", "styles.css"));
        await Until(() => Count(output.Text, "rendered " + Package) == 2).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        Assert.Equal(0, await watch.ConfigureAwait(true));

        Assert.Equal(single + PackageWatcher.WatchingLine + Environment.NewLine + single, output.Text);
    }

    private static string TrappedPackage(string root)
    {
        var package = CopyPackage(Package, root);
        var rowPath = Path.Combine(package, "html", "row.html.hbs");
        var row = File.ReadAllText(rowPath);
        var trapped = row.Replace("</tr>", "<td><svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{format_number item.unitPrice \"F2\"}}\"/></svg></td></tr>", StringComparison.Ordinal);
        Assert.NotEqual(row, trapped);
        File.WriteAllText(rowPath, trapped, Utf8NoBom);
        return package;
    }

    private static string[] Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    private static bool IsWarning(string line) => line.StartsWith("warning:", StringComparison.Ordinal);

    private static async Task<(int Code, string Text, ReplayPrinter Printer, string OutputDirectory)> RenderWithData(string package, string root, string dataFile)
    {
        var outputDirectory = Path.Combine(root, "out");
        var printer = Printer();
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory, dataFile), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return (code, writer.ToString(), printer, outputDirectory);
    }

    private static ReplayPrinter Printer() => new(Path.Combine(TestPaths.Goldens, Package + ".pdf"));

    private static TemplateAuthoringTool Tool(IPdfPrinter printer) =>
        new(new FixedTimeProvider(FixedInstant), () => printer);

    private static PackageWatcher Watcher(IPdfPrinter printer) =>
        new(Tool(printer), TimeSpan.FromMilliseconds(100));

    private static string ReadPreviewStylesheet(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, "html", "styles.css");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Patience, "The condition was not met within " + Patience.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
            await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private static string CopyPackage(string id, string root)
    {
        var source = TestPaths.Example(id);
        var target = Path.Combine(root, id);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    /// <summary>A writer the watch writes to from its own task while the case reads it.</summary>
    private sealed class SynchronizedWriter : StringWriter
    {
        private readonly Lock _gate = new();

        private int _flushes;

        public SynchronizedWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return ToString();
                }
            }
        }

        public int Flushes => Volatile.Read(ref _flushes);

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                base.Write(buffer, index, count);
            }
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            Write(value + NewLine);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushes);
            return Task.CompletedTask;
        }
    }

    /// <summary>A replaying printer whose next print can be held open.</summary>
    private sealed class GatedPrinter(ReplayPrinter inner) : IPdfPrinter
    {
        private TaskCompletionSource? _gate;

        private int _calls;

        private volatile bool _waiting;

        public int Calls => Volatile.Read(ref _calls);

        public bool Waiting => _waiting;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public async Task<PrintOutcome> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (_gate is { } gate)
            {
                _waiting = true;
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _waiting = false;
                _gate = null;
            }

            return await inner.PrintAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<PrintOutcome> PrintAsync(
            string document,
            PageRule pageRule,
            ITemplatePackageFiles files,
            string pdfPath,
            CancellationToken cancellationToken = default) =>
            PrintAsync(
                new PdfPrintRequest(document, pageRule, files, PdfPrintMode.Native, Accounting: null, pdfPath),
                cancellationToken);
    }
}
