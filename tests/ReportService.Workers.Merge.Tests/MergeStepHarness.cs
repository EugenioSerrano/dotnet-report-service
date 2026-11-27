using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Merge.Assembly;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The assembly step over the doubles it reads: one accepted job with a running
/// output and a pinned template, a partition and one part commit per block written
/// to a filesystem store, and a taken package whose manifest declares the numbering
/// contract and carries the packaged font. The parts are real PDFs generated with
/// the vendored font; the package is a real tar the product's own reader extracts.
/// </summary>
internal sealed class MergeStepHarness : IDisposable
{
    /// <summary>The job every case assembles.</summary>
    public const string JobId = "job-merge";

    /// <summary>The output every case assembles.</summary>
    public const string OutputId = "output-merge";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "merge-step-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryJobStore _jobs = new(new InMemoryUploadStore());
    private readonly FilesystemObjectStore _store;
    private readonly TemplateVersionRecord _record;

    /// <summary>Creates a harness with <paramref name="blocks"/> committed parts of <paramref name="pagesEach"/> pages.</summary>
    /// <param name="blocks">How many blocks the partition and the commits hold.</param>
    /// <param name="pagesEach">How many pages each part holds.</param>
    /// <param name="withNumbering">Whether the package declares the numbering contract.</param>
    /// <param name="declaredPages">The page count a part's commit declares, when it differs from the pages written.</param>
    /// <param name="manifestJson">The manifest document the package and record carry, when the case needs an unusable one.</param>
    /// <param name="numbering">A transformation of the typed numbering contract, when the case needs an unusable one.</param>
    /// <param name="includeFont">Whether the package carries the font file the contract names.</param>
    /// <param name="pageCode">The per-page code the taken version declares, when the case needs one.</param>
    public MergeStepHarness(
        int blocks = 2,
        int pagesEach = 2,
        bool withNumbering = true,
        int? declaredPages = null,
        string? manifestJson = null,
        Func<TemplatePdfNumbering, TemplatePdfNumbering>? numbering = null,
        bool includeFont = true,
        TemplatePageCode? pageCode = null)
    {
        Directory.CreateDirectory(_root);
        _store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(_root, "store"), CreateIfMissing = true },
            _root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        var storedManifest = ManifestJsonOf(manifestJson, withNumbering);
        var font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"));
        var package = Package(storedManifest, font, includeFont);
        var packageKey = ObjectKey.FromSegments("templates", "merge-package-" + Guid.NewGuid().ToString("N"));
        var written = _store.WriteAsync(packageKey, new MemoryStream(package), CancellationToken.None).GetAwaiter().GetResult();
        if (!written.Succeeded)
        {
            throw new InvalidOperationException("the package could not be written: " + written.Outcome);
        }

        var manifest = new TemplateManifest
        {
            ManifestVersion = "1.0",
            Id = "merge-template",
            Version = "1.0.0",
            Engine = TemplateEngine.Scriban,
            DeclaredEngineVersion = "7.5.1",
            Outputs = new TemplateOutputs
            {
                Pdf = new TemplatePdfOutput
                {
                    Paper = "A4",
                    Orientation = "portrait",
                    MarginMm = new TemplateMargins { Top = 12, Right = 10, Bottom = 12, Left = 10 },
                    Numbering = withNumbering ? (numbering is null ? Numbering() : numbering(Numbering())) : null,
                    PageCode = pageCode,
                },
            },
        };

        _record = new TemplateVersionRecord(
            "merge-template",
            "1.0.0",
            "consumer-a",
            false,
            "1.0",
            TemplateEngine.Scriban,
            "7.5.1",
            "schema.json",
            null,
            null,
            written.Value!.Sha256,
            written.Value.Size,
            2,
            packageKey,
            manifest,
            storedManifest,
            DateTimeOffset.UnixEpoch);

        var pin = new TemplatePin(
            "merge-template",
            "1.0.0",
            new string('e', 40),
            null,
            null,
            written.Value.Sha256,
            TemplateEngine.Scriban,
            "7.5.1",
            "schema.json",
            DateTimeOffset.UnixEpoch);
        _jobs.SeedAccepted(JobId, OutputId, new TemplateReferenceRecord(JobId, pin.TemplateId, TemplateReferenceForm.Exact)
        {
            RequestedVersion = pin.ResolvedVersion,
            Pin = pin,
        });
        _jobs.CommitPartitionAsync(
            new PartitionRecord(JobId, 2, blocks, blocks * 500, blocks * 1024, "index", new string('a', 64), 1, DateTimeOffset.UnixEpoch),
            CancellationToken.None).GetAwaiter().GetResult();

        for (var index = 0; index < blocks; index++)
        {
            CommitPart(index, pagesEach, declaredPages ?? pagesEach);
        }
    }

    /// <summary>The in-memory job store the step reads and commits through.</summary>
    public InMemoryJobStore Jobs => _jobs;

    /// <summary>The harness's temporary root.</summary>
    public string Root => _root;

    /// <summary>The object store holding the parts, the package and the assembled output.</summary>
    public IObjectStore Store => _store;

    /// <summary>The taken version's record.</summary>
    public TemplateVersionRecord Record => _record;

    /// <summary>The step over the harness's stores and a real assembler.</summary>
    public AssemblePdfActivity Activity(
        AssemblyInterruption? interruption = null,
        ReportService.Pdf.Verification.PageCodes.PageCodeGeneratorRegistry? pageCodes = null,
        DocumentCapacity? capacity = null)
    {
        var activity = new AssemblePdfActivity(
            _jobs,
            new FakeVersionStore(_record),
            _store,
            new TakenTemplatePackageReader(_store),
            new PdfSharpAssembler(),
            new AssemblyJobMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName)),
            NullLoggerFactory.Instance,
            NullLogger<AssemblePdfActivity>.Instance,
            Path.Combine(_root, "scratch"),
            pageCodes,
            capacity)
        {
            Interruption = interruption,
        };
        return activity;
    }

    /// <summary>The commits the harness wrote, ordered by block index.</summary>
    public IReadOnlyList<PartCommit> Commits => _jobs.ListPartsAsync(OutputId, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private void CommitPart(int index, int pages, int declaredPages)
    {
        var path = Path.Combine(_root, $"seed-{index}.pdf");
        using (var document = new PdfDocument())
        {
            var font = new XFont(Face(), 10, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            for (var page = 1; page <= pages; page++)
            {
                var sheet = document.AddPage();
                sheet.Width = XUnit.FromPoint(595.276);
                sheet.Height = XUnit.FromPoint(841.89);
                using var graphics = XGraphics.FromPdfPage(sheet);
                graphics.DrawString(
                    $"block{index}page{page}",
                    font,
                    XBrushes.Black,
                    new XRect(40, 40, 400, 30),
                    XStringFormats.TopLeft);
            }

            document.Save(path);
        }

        var bytes = File.ReadAllBytes(path);
        var key = ObjectKey.FromSegments("parts", JobId, index.ToString(System.Globalization.CultureInfo.InvariantCulture), "a" + index);
        var write = _store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None).GetAwaiter().GetResult();
        if (!write.Succeeded)
        {
            throw new InvalidOperationException("a part could not be written: " + write.Outcome);
        }

        _jobs.CommitPartAsync(
            new PartCommit(
                OutputId,
                index,
                key.ToString(),
                write.Value!.Sha256,
                write.Value.Size,
                declaredPages,
                _record.PackageSha256,
                DateTimeOffset.UnixEpoch),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Face()
    {
        var resolver = PackagedFontResolver.Instance;
        try
        {
            GlobalFontSettings.FontResolver = resolver;
        }
        catch (InvalidOperationException)
        {
        }

        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"));
        return resolver.Register(new NumberingFont("Liberation Sans", "fonts/LiberationSans-Regular.ttf", 9, bytes));
    }

    private static TemplatePdfNumbering Numbering() => new()
    {
        Area = new TemplateNumberingArea { LeftMm = 120, TopMm = 268, WidthMm = 80, HeightMm = 8 },
        Font = new TemplateNumberingFont
        {
            Family = "Liberation Sans",
            File = "fonts/LiberationSans-Regular.ttf",
            SizePt = 9,
        },
        Format = "Page {X} of {Y}",
        Culture = "en-US",
    };

    private static string ManifestJson(bool withNumbering)
    {
        var pdf = new Dictionary<string, object?>
        {
            ["paper"] = "A4",
            ["orientation"] = "portrait",
            ["marginMm"] = new Dictionary<string, decimal> { ["top"] = 12, ["right"] = 10, ["bottom"] = 12, ["left"] = 10 },
        };
        if (withNumbering)
        {
            pdf["numbering"] = new Dictionary<string, object?>
            {
                ["area"] = new Dictionary<string, decimal> { ["leftMm"] = 120, ["topMm"] = 268, ["widthMm"] = 80, ["heightMm"] = 8 },
                ["font"] = new Dictionary<string, object?>
                {
                    ["family"] = "Liberation Sans",
                    ["file"] = "fonts/LiberationSans-Regular.ttf",
                    ["sizePt"] = 9,
                },
                ["format"] = "Page {X} of {Y}",
                ["culture"] = "en-US",
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["manifestVersion"] = "1.0",
            ["id"] = "merge-template",
            ["version"] = "1.0.0",
            ["outputs"] = new Dictionary<string, object?> { ["pdf"] = pdf },
        });
    }

    private static string ManifestJsonOf(string? manifestJson, bool withNumbering) =>
        manifestJson ?? ManifestJson(withNumbering);

    private static byte[] Package(string manifestJson, byte[] font, bool includeFont)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            Write(writer, "template.yaml", Encoding.UTF8.GetBytes(manifestJson));
            if (includeFont)
            {
                Write(writer, "fonts/LiberationSans-Regular.ttf", font);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(TarWriter writer, string name, byte[] content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
}

/// <summary>The version store the merge step reads: one record, or none.</summary>
internal sealed class FakeVersionStore(TemplateVersionRecord? record) : ITemplateVersionStore
{
    public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
        Task.FromResult(record);

    public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
