using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReportService.Data;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Manifest;
using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The takeable family's composition evidence: the three <c>sample-gate</c> versions, one
/// engine each, compose the report to <b>byte-identical HTML</b> under the package's own
/// locale and under the invariant culture, and that HTML carries the sample's rows and
/// values. The manifest is read from each package's own <c>template.yaml</c> through the
/// product's mapping - the same read the take and the rendering host use - so no declared
/// copy can stand beside the package.
/// </summary>
public sealed class SampleGatePackagesTests
{
    private static readonly (string Version, string Engine)[] Versions =
    [
        ("1.0.0", ScribanTemplateAdapter.EngineName),
        ("1.1.0", HandlebarsTemplateAdapter.EngineName),
        ("1.2.0", FluidTemplateAdapter.EngineName),
    ];

    /// <summary>The two ambient cultures AC-3 compares: the process culture, never the report's.</summary>
    public static TheoryData<string> Cultures => new() { "es-AR", "invariant" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TheThreeTakeableVersionsComposeTheSameHtml(string culture)
    {
        // The report's presentation is the package's own (es-AR/Buenos Aires); what changes
        // between the two cases is the culture the composing process itself runs under - the
        // ambient-culture control the core tier renders through the host.
        var previousCulture = CultureInfo.CurrentCulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        CultureInfo.CurrentCulture = ambient(culture);
        CultureInfo.DefaultThreadCurrentCulture = ambient(culture);
        CultureInfo.CurrentUICulture = ambient(culture);
        CultureInfo.DefaultThreadCurrentUICulture = ambient(culture);
        try
        {
            var settings = EngineCases.BuenosAires;
            var htmls = new List<byte[]>();
            var hashes = new List<string>();
            foreach (var (version, engine) in Versions)
            {
                var html = Compose(version, engine, settings);
                htmls.Add(html);
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(html)));
            }

        Assert.All(htmls, html => Assert.Equal(htmls[0], html));
        Assert.All(hashes, hash => Assert.Equal(hashes[0], hash));

        var text = Encoding.UTF8.GetString(htmls[0]);
        foreach (var description in new[] { "Consulting", "Support plan", "Hosting" })
        {
            Assert.Contains(description, text, StringComparison.Ordinal);
        }

        foreach (var amount in new[] { "1.000,00", "-0,50", "0,00", "500.00", "-0.50" })
        {
            Assert.Contains(amount, text, StringComparison.Ordinal);
        }

        Assert.Contains("Records: 3", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
        }
    }

    private static CultureInfo ambient(string culture) =>
        culture == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("es-AR");

    [Fact]
    public void EachVersionDeclaresItsOwnEngineAndTheSameAnchors()
    {
        foreach (var (version, engine) in Versions)
        {
            var manifest = ManifestOf(version);

            Assert.Equal(engine, TemplateEngineNames.ToManifestName(manifest.Engine!.Value));
            Assert.Equal(version, manifest.Version);
            Assert.Equal("billing-system", manifest.Owner);
            Assert.False(manifest.Shared);
            Assert.Equal(["$.sequence", "$.description"], manifest.Outputs!.Pdf!.Verification!.Anchors!.Item);
            Assert.Equal("es-AR", manifest.LocaleDefaults!.Culture);
        }
    }

    private static byte[] Compose(string version, string engine, PresentationSettings settings)
    {
        var folder = FolderOf(version);
        var manifest = RenderManifestMapping.From(ManifestOf(version), DeclaredEngine(version), EngineCases.EngineVersionOf(engine));
        var adapter = EngineCases.AdapterOf(engine);
        var prepared = adapter.Prepare(manifest, new FolderTemplatePackageFiles(folder));
        var sample = SamplePartition.ReadAsync(File.ReadAllBytes(Path.Combine(folder, "sample.json"))).GetAwaiter().GetResult();
        var partitioned = Assert.IsType<SampleRead.Partitioned>(sample).Sample;

        return new BlockComposer(new ManualTimeProvider(settings.Clock))
            .Compose(
                new CompositionRequest(
                    prepared,
                    partitioned.Block,
                    partitioned.Header,
                    partitioned.Records,
                    new CompositionRender(settings.Culture.Name, settings.TimeZone.Id, settings.Clock),
                    GroupKey: null),
                adapter,
                settings)
            .Html;
    }

    private static TemplateManifest ManifestOf(string version)
    {
        using var stream = File.OpenRead(Path.Combine(FolderOf(version), "template.yaml"));
        var read = new TemplateManifestReader().Read(stream);
        Assert.True(read.Succeeded, read.Reason);
        return read.Manifest!;
    }

    private static TemplateEngine DeclaredEngine(string version) => ManifestOf(version).Engine!.Value;

    private static string FolderOf(string version) =>
        Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", "sample-gate", version);
}
