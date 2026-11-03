using System.Diagnostics;
using System.Globalization;
using System.Text;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing;

namespace ReportService.TemplateSource.Tests.Manifest;

/// <summary>
/// The one reader of <c>template.yaml</c>, held to the subset, the limits, the
/// JSON form and the error model the manifest format fixes: the three pilot
/// manifests map to their checked-in JSON and to a typed view; the same bytes
/// give the same JSON; every refused construct is refused with the file, a line,
/// a column and the rule, and never with the library's own message; and each
/// limit has a case just inside it that is accepted.
/// </summary>
public sealed class TemplateManifestReaderTests
{
    private static readonly TemplateManifestReader Reader = new();

    /// <summary>The three pilot packages, in the order this suite reports them.</summary>
    public static TheoryData<string> Pilots => new() { "pilot-simple", "pilot-grouped", "pilot-volume" };

    [Theory]
    [MemberData(nameof(Pilots))]
    public void APilotManifestMapsToItsCheckedInJson(string pilot)
    {
        var read = Reader.Read(File.OpenRead(PilotManifest(pilot)));

        Assert.True(read.Succeeded, read.Reason);
        Assert.Equal(ExpectedJson(pilot), read.CanonicalJson);
    }

    [Theory]
    [MemberData(nameof(Pilots))]
    public void APilotManifestTypesAsTheManifestThisBuildReads(string pilot)
    {
        var read = Reader.Read(File.OpenRead(PilotManifest(pilot)));

        var manifest = Assert.IsType<TemplateManifest>(read.Manifest);
        Assert.Equal("1.0", manifest.ManifestVersion);
        Assert.Equal(pilot, manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("dev-consumer", manifest.Owner);
        Assert.False(manifest.Shared);
        Assert.Equal(TemplateEngine.Scriban, manifest.Engine);
        Assert.Equal(TemplateEngineCatalog.ScribanVersion, manifest.DeclaredEngineVersion);
        Assert.Equal("schema.json", manifest.DataContract?.Schema);
        Assert.NotNull(manifest.Outputs?.Html?.Parts?.DocumentStart);
        Assert.Equal(15m, manifest.Outputs?.Pdf?.MarginMm?.Top);
        Assert.NotEmpty(Assert.IsType<TemplatePdfAnchors>(manifest.Outputs?.Pdf?.Verification?.Anchors).Header!);
    }

    [Theory]
    [MemberData(nameof(Pilots))]
    public void ReadingTheSameBytesTwiceGivesTheSameJson(string pilot)
    {
        var bytes = File.ReadAllBytes(PilotManifest(pilot));

        var first = Reader.Read(new MemoryStream(bytes, writable: false));
        var second = Reader.Read(new MemoryStream(bytes, writable: false));

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.CanonicalJson, TemplateManifestJson.Write(TemplateManifestJson.Parse(first.CanonicalJson!)));
    }

    [Fact]
    public void TheQuotedAndPlainScalarsOfAManifestResolveAsTheFormatSays()
    {
        var read = Reader.Read(Utf8(
            """
            a: "1.0"
            b: 1.50
            c: yes
            d: on
            e: ~
            f: true
            g: es-AR
            h: 1.0.0
            i: 007x
            """));

        Assert.True(read.Succeeded, read.Reason);
        Assert.Equal(
            """{"a":"1.0","b":1.50,"c":"yes","d":"on","e":null,"f":true,"g":"es-AR","h":"1.0.0","i":"007x"}""",
            read.CanonicalJson);
    }

    [Fact]
    public void ABlockScalarIsAlwaysAString()
    {
        var read = Reader.Read(Utf8("a: |\n  one\n  two\nb: >\n  folded\n"));

        Assert.True(read.Succeeded, read.Reason);
        Assert.Equal("""{"a":"one\ntwo\n","b":"folded\n"}""", read.CanonicalJson);
    }

    [Fact]
    public void ACommentIsDiscarded()
    {
        var read = Reader.Read(Utf8("# the manifest\nid: a # trailing\n"));

        Assert.True(read.Succeeded, read.Reason);
        Assert.Equal("""{"id":"a"}""", read.CanonicalJson);
    }

    [Fact]
    public void OneLeadingByteOrderMarkIsRemoved()
    {
        var read = Reader.Read(Utf8("\uFEFFid: a\n"));

        Assert.True(read.Succeeded, read.Reason);
        Assert.Equal("""{"id":"a"}""", read.CanonicalJson);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusedManifestNamesTheFileTheLineTheColumnAndTheRule(string name, byte[] manifest)
    {
        var read = Reader.Read(new MemoryStream(manifest, writable: false));

        Assert.False(read.Succeeded, name);
        Assert.Null(read.CanonicalJson);
        var reason = Assert.IsType<string>(read.Reason);
        Assert.StartsWith("template.yaml", reason, StringComparison.Ordinal);
        Assert.Contains("line ", reason, StringComparison.Ordinal);
        Assert.Contains("column ", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("YamlDotNet", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Scanner", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Parser", reason, StringComparison.Ordinal);
    }

    /// <summary>One refused construct per row of the format's subset and limits.</summary>
    public static TheoryData<string, byte[]> Refusals => new()
    {
        { "over 64 KiB", Bytes("id: a\n" + "#" + new string('x', TemplateManifestReader.MaxBytes - 7) + "\n") },
        { "depth 33", Bytes(Nested(33)) },
        { "10 001 nodes", Bytes(Values(9_998)) },
        { "an anchor", Bytes("a: &x 1\n") },
        { "an alias", Bytes("a: 1\nb: *a\n") },
        { "a billion laughs", Bytes("a: &a [1, 1]\nb: &b [*a, *a]\nc: &c [*b, *b]\n") },
        { "a standard tag", Bytes("id: !!str a\n") },
        { "a local tag", Bytes("id: !foo a\n") },
        { "the non-specific tag", Bytes("id: ! a\n") },
        { "a %YAML directive", Bytes("%YAML 1.2\n---\nid: a\n") },
        { "a %TAG directive", Bytes("%TAG !e! tag:example.com,2000:\n---\nid: a\n") },
        { "two documents", Bytes("---\na: 1\n---\nb: 2\n") },
        { "a duplicate key at the root", Bytes("a: 1\na: 2\n") },
        { "a duplicate key nested", Bytes("a:\n  b: 1\n  b: 2\n") },
        { "a complex key", Bytes("? [a, b]\n: 1\n") },
        { "a merge key", Bytes("a:\n  <<: 1\n") },
        { "a leading plus", Bytes("a: +1\n") },
        { "a leading zero", Bytes("a: 007\n") },
        { "a fraction without a leading zero", Bytes("a: .5\n") },
        { "a fraction without a trailing digit", Bytes("a: 1.\n") },
        { "a hexadecimal number", Bytes("a: 0x1F\n") },
        { "an octal number", Bytes("a: 0o17\n") },
        { "an infinite number", Bytes("a: .inf\n") },
        { "not a number", Bytes("a: .nan\n") },
        { "invalid UTF-8", [.. "id: "u8, 0xC3, 0x28, (byte)'\n'] },
        { "UTF-16", [0xFF, 0xFE, (byte)'i', 0x00, (byte)'d', 0x00] },
        { "UTF-32", [0xFF, 0xFE, 0x00, 0x00, (byte)'i', 0x00, 0x00, 0x00] },
        { "a NUL character", Bytes("a: x\u0000y\n") },
        { "a bell character", Bytes("a: x\u0007y\n") },
        { "a next-line character", Bytes("a: x\u0085y\n") },
        { "a byte order mark inside the text", Bytes("a: x\uFEFFy\n") },
        { "a NUL written as an escape", Bytes("a: \"\\0\"\n") },
        { "a lone surrogate", Bytes("a: \"\\ud800\"\n") },
        { "a root that is a sequence", Bytes("- a\n") },
        { "a root that is a scalar", Bytes("a\n") },
        { "an empty file", [] },
        { "a bad indentation", Bytes("a:\n b: 1\n  c: 2\n") },
        { "an unterminated quoted scalar", Bytes("a: \"open\n") },
    };

    [Fact]
    public void AManifestOfExactlyTheSizeLimitIsAcceptedAndOneByteMoreIsRefused()
    {
        var inside = Bytes("id: a\n" + "#" + new string('x', TemplateManifestReader.MaxBytes - 8) + "\n");
        Assert.Equal(TemplateManifestReader.MaxBytes, inside.Length);

        var accepted = Reader.Read(new MemoryStream(inside, writable: false));

        Assert.True(accepted.Succeeded, accepted.Reason);
        Assert.Equal("""{"id":"a"}""", accepted.CanonicalJson);
    }

    [Fact]
    public void ThirtyTwoLevelsOfNestingAreAcceptedAndThirtyThreeAreRefused()
    {
        var accepted = Reader.Read(Utf8(Nested(TemplateManifestReader.MaxDepth)));

        Assert.True(accepted.Succeeded, accepted.Reason);
    }

    [Fact]
    public void TenThousandNodesAreAcceptedAndTenThousandAndOneAreRefused()
    {
        var accepted = Reader.Read(Utf8(Values(9_997)));

        Assert.True(accepted.Succeeded, accepted.Reason);
    }

    [Fact]
    public void EveryWorstCaseInputStaysInsideTheParseCost()
    {
        var worstCases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["one 64 KiB plain scalar"] = "a: " + new string('x', 65_000) + "\n",
            ["32 768 quoted escapes"] = "a: " + string.Concat(Enumerable.Repeat("\"\"", 16_384)) + "\n",
            ["a 64 KiB flow sequence"] = "a: [" + string.Concat(Enumerable.Repeat("1,", 21_000)) + "1]\n",
            ["9 000 short keys"] = string.Concat(Enumerable.Range(0, 9_000).Select(index => $"k{index}: 1\n")),
            ["64 KiB of comments"] = "#" + new string('x', 65_000) + "\n",
            ["an unclosed nested flow"] = "a: " + new string('[', 20_000) + "\n",
        };

        foreach (var (name, text) in worstCases)
        {
            var stopwatch = Stopwatch.StartNew();
            var read = Reader.Read(Utf8(text));
            stopwatch.Stop();

            Assert.True(
                stopwatch.ElapsedMilliseconds <= 500,
                $"'{name}' took {stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms, over the 500 ms the manifest read is given");
            Assert.True(read.Succeeded || read.Reason!.StartsWith("template.yaml", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void AStreamThatCannotBeReadIsRefusedAndNotThrown()
    {
        var read = Reader.Read(new UnreadableStream());

        Assert.False(read.Succeeded);
        Assert.StartsWith("template.yaml", read.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReaderHasOnePublicMemberAndItTakesAStream()
    {
        var members = typeof(TemplateManifestReader).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

        var member = Assert.Single(members);
        Assert.Equal("Read", member.Name);
        Assert.Equal(typeof(ManifestReadResult), member.ReturnType);
        Assert.Equal([typeof(Stream)], member.GetParameters().Select(parameter => parameter.ParameterType));
    }

    /// <summary>A document whose deepest collection sits at <paramref name="depth"/> levels, the root mapping being one.</summary>
    internal static string Nested(int depth) =>
        "a" + string.Concat(Enumerable.Repeat(": {a", depth - 1)) + ": 1" + string.Concat(Enumerable.Repeat("}", depth - 1)) + "\n";

    /// <summary>A document holding exactly <paramref name="scalars"/> scalar items, beside its root mapping, its key and its sequence.</summary>
    internal static string Values(int scalars) => "values:\n" + string.Concat(Enumerable.Repeat("  - 1\n", scalars));

    internal static string PilotManifest(string pilot) =>
        Path.Combine(RepositoryPaths.Root, "samples", "templates", pilot, "template.yaml");

    private static string ExpectedJson(string pilot) =>
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "ReportService.TemplateSource.Tests", "Manifest", "Expected", pilot + ".json")).TrimEnd('\n');

    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A stream that fails on the first read, as a broken upload or a closed file does.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the stream is unreadable");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
