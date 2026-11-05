using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The agreement between the two readers on the documents the product itself publishes.
/// Every PDF the worker-level run prints — the 25-record job, N = 1, N = 1 000, the row
/// taller than a page and the empty snapshot — is read by the independent judge in its own
/// image, against the declaration of the sample template and the snapshot that job used,
/// and must pass; the production verification that produced it passed by construction,
/// because nothing is published before it does. The four native mutations are printed through
/// the rendering host image with the integrity package and must fail **both** readers with
/// the class the mapping names.
/// <para>
/// The class is the integration level's and runs in the integration test cycle; this change
/// writes it and checks that it builds.
/// </para>
/// </summary>
public sealed class ProductPdfAgreementTests : IClassFixture<PostgresFixture>
{
    private const string OracleImage = "report-service-pdf-oracle:0.2.0-dev";

    private const string HostImage = "report-service-rendering-host:0.2.0-dev";

    private const string IntegrityPackage = "integrity-table";

    private const string IntegrityFixture = "B100";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    /// <summary>The control and the class the product's verification reports for it (§2.9.5).</summary>
    public static TheoryData<string, string> Mutations { get; } = new()
    {
        { "NC-W", "text-dropped" },
        { "NC-L", "text-repeated" },
        { "NC-R", "row-lost" },
        { "NC-F", "text-dropped" },
    };

    private readonly PostgresFixture _fixture;

    private readonly string _working = Path.Combine(
        Path.GetTempPath(),
        "rs13-agreement",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public ProductPdfAgreementTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EveryPublishedPdfPassesTheJudgeAndTheProductionVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_working);
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var documents = new (string Name, int Records, bool Tall)[]
        {
            ("pilot-simple-25", 25, false),
            ("pilot-simple-1", 1, false),
            ("pilot-simple-1000", 1_000, false),
            ("pilot-simple-tall", 25, true),
        };

        foreach (var (name, records, tall) in documents)
        {
            var snapshot = PrintingRig.Snapshot(records, tall);
            var reportJobId = await rig.SubmitAsync(
                await rig.ReadyUploadAsync(snapshot, cancellationToken),
                PrintingRig.First,
                cancellationToken);
            var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
            Assert.Equal("SUCCEEDED", job.Status);
            Assert.NotNull(job.ArtifactId);

            var pdf = await rig.ReadArtifactAsync(job, cancellationToken);
            var verdict = await JudgeAsync(name, pdf, rig.SnapshotOf(reportJobId), "pilot-simple", cancellationToken);
            Assert.True(verdict.Passed, $"{name} failed the judge: {verdict.Describe()}");
        }

        // The empty report: the rig's own empty snapshot, published by the same path.
        var emptyJob = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.EmptySnapshot(), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var empty = await rig.WaitForTerminalAsync(emptyJob, Deadline, cancellationToken);
        Assert.Equal("SUCCEEDED", empty.Status);
        var emptyVerdict = await JudgeAsync("pilot-simple-empty", await rig.ReadArtifactAsync(empty, cancellationToken), rig.SnapshotOf(emptyJob), "pilot-simple", cancellationToken);
        Assert.True(emptyVerdict.Passed, $"the empty report failed the judge: {emptyVerdict.Describe()}");
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task EveryNativeMutationFailsBothReadersWithItsClass(string mutation, string productClass)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_working);
        var fixtures = Path.Combine(_working, "fixtures");
        WriteFixture(fixtures, IntegrityFixture);
        var templates = Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");
        var output = Path.Combine(_working, "out", mutation);
        Directory.CreateDirectory(output);

        await RunAsync(
            HostImage,
            [
                "render", "--fixture", $"/fixtures/{IntegrityFixture}.json",
                "--template", $"/packages/{IntegrityPackage}",
                "--mutation", mutation,
                "--out", "/out",
            ],
            [(fixtures, "/fixtures"), (templates, "/packages"), (output, "/out")],
            cancellationToken);

        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(output, IntegrityFixture + ".json"), cancellationToken));
        // The product's verification refuses the controlled document, with the class the
        // mapping names: nothing of this control may reach a consumer.
        Assert.False(result.RootElement.GetProperty("verificationPassed").GetBoolean());
        var reason = result.RootElement.GetProperty("verificationReason").GetString();
        Assert.Equal(productClass, reason, ignoreCase: true);

        var pdf = Path.Combine(output, result.RootElement.GetProperty("pdf").GetString()!);
        var verdict = await JudgeAsync(
            IntegrityFixture + "-" + mutation,
            await File.ReadAllBytesAsync(pdf, cancellationToken),
            await File.ReadAllBytesAsync(Path.Combine(fixtures, IntegrityFixture + ".json"), cancellationToken),
            IntegrityPackage,
            cancellationToken);
        Assert.False(verdict.Passed, $"{mutation} passed the judge, and the product refused it");
        Assert.Contains(verdict.Classes, failure => failure.Contains("text-", StringComparison.Ordinal) || failure.Contains("row-", StringComparison.Ordinal) || failure.Contains("structure-lost", StringComparison.Ordinal));
    }

    // The judge in its own image: the document, the snapshot it was printed from and the
    // declaration of the package it was printed with.
    private async Task<Verdict> JudgeAsync(string name, byte[] pdf, byte[] snapshot, string declaration, CancellationToken cancellationToken)
    {
        var input = Path.Combine(_working, "judged", name);
        Directory.CreateDirectory(input);
        var pdfPath = Path.Combine(input, name + ".pdf");
        await File.WriteAllBytesAsync(pdfPath, pdf, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(input, name + ".snapshot.json"), snapshot, cancellationToken);

        await RunAsync(
            OracleImage,
            [
                "verdict", "--pdf", $"/in/{name}.pdf",
                "--fixture", $"/in/{name}.snapshot.json",
                "--expectation", $"/expectations/{declaration}.json",
                "--out", $"/in/{name}.verdict.json",
                "--reading", $"/in/{name}.reading.json",
            ],
            [(input, "/in"), (Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "OracleExpectations"), "/expectations")],
            cancellationToken);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(input, name + ".verdict.json"), cancellationToken));
        var failures = document.RootElement.GetProperty("failures").EnumerateArray()
            .Select(failure => string.Join(
                " | ",
                new[] { "rule", "class", "field", "detail" }
                    .Select(field => failure.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(part => !string.IsNullOrEmpty(part))))
            .ToList();

        // The verdict stays with the run: the class that fails is what a person inspects.
        RunEnvironment.WriteEvidence(name + ".verdict.json", await File.ReadAllBytesAsync(Path.Combine(input, name + ".verdict.json"), cancellationToken));

        return new Verdict(document.RootElement.GetProperty("passed").GetBoolean(), failures);
    }

    private static async Task RunAsync(
        string image,
        IReadOnlyList<string> command,
        IReadOnlyList<(string Source, string Target)> mounts,
        CancellationToken cancellationToken)
    {
        var builder = new ContainerBuilder(image)
            .WithName($"report-service-agreement-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}")
            .WithCommand([.. command]);
        foreach (var (source, target) in mounts)
        {
            builder = builder.WithBindMount(source, target, AccessMode.ReadWrite);
        }

        await using var container = builder.Build();
        await container.StartAsync(cancellationToken);
        var exit = await container.GetExitCodeAsync(cancellationToken);
        Assert.True(exit == 0, $"{image} exited {exit} for {string.Join(' ', command)}");
    }

    private static void WriteFixture(string folder, string fixture)
    {
        Directory.CreateDirectory(folder);
        var datasets = Path.Combine(RepositoryPaths.Root, "samples", "datasets");
        var generator = new ReportService.Testing.Fixtures.FixtureGenerator(
            ReportService.Testing.Fixtures.FixtureDataset.Load(datasetPath: Path.Combine(datasets, "header-items-v1.grouped.10k.json"), sumsPath: Path.Combine(datasets, ReportService.Testing.Fixtures.FixtureGenerator.SumsFileName)));
        generator.WriteFolder(folder);
    }

    /// <summary>What the judge said about one document.</summary>
    /// <param name="Passed">Whether every rule passed.</param>
    /// <param name="Classes">The failures, as rule, class, field and detail.</param>
    public sealed record Verdict(bool Passed, IReadOnlyList<string> Classes)
    {
        /// <summary>The failures as one line, for a failure message.</summary>
        public string Describe() => Classes.Count == 0 ? "no failure" : string.Join("; ", Classes);
    }
}
