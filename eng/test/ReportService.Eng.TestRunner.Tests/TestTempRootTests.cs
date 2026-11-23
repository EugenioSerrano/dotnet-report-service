using ReportService.Eng.TestRunner;

namespace ReportService.Eng.TestRunner.Tests;

/// <summary>
/// The temp-root resolver (US-000.BOLT-096 / BUG-052): the environment variable wins when
/// usable, else the Dev-Drive default when usable, else the system temp — and it never
/// throws. The environment lookup and the usability probe are injected, so these cases are
/// deterministic even on a machine that really has <c>D:\Temp</c>.
/// </summary>
public sealed class TestTempRootTests
{
    // AC-1 — the configured env path is used when it is usable.
    [Fact]
    public void ConfiguredPathIsUsedWhenUsable()
    {
        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => @"D:\FastTemp",
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: path => path == @"D:\FastTemp");

        Assert.Equal(TestTempRoot.Source.Configured, resolution.Source);
        Assert.Equal(@"D:\FastTemp", resolution.Root);
    }

    // AC-2 — variable unset and the default absent: safe fallback to the system temp.
    [Fact]
    public void UnsetAndNoDefaultFallsBackToSystemTemp()
    {
        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => null,
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: _ => false);

        Assert.Equal(TestTempRoot.Source.SystemFallback, resolution.Source);
        Assert.Equal(Path.GetTempPath(), resolution.Root);
    }

    // AC-3 — an invalid/unwritable configured path: safe fallback (here the default is also
    // unusable, so it falls through to the system temp). No failure.
    [Fact]
    public void InvalidConfiguredPathFallsBackSafely()
    {
        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => @"Z:\does-not-exist",
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: _ => false);

        Assert.Equal(TestTempRoot.Source.SystemFallback, resolution.Source);
        Assert.Equal(Path.GetTempPath(), resolution.Root);
    }

    // AC-3 variant — an invalid configured path but a usable default: the default is used.
    [Fact]
    public void InvalidConfiguredPathUsesTheUsableDefault()
    {
        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => @"Z:\does-not-exist",
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: path => path == TestTempRoot.DefaultRoot);

        Assert.Equal(TestTempRoot.Source.Default, resolution.Source);
        Assert.Equal(TestTempRoot.DefaultRoot, resolution.Root);
    }

    // An over-long configured root is rejected up front (path-length budget), even if "usable".
    [Fact]
    public void OverLongConfiguredRootIsRejected()
    {
        var tooLong = @"D:\" + new string('x', TestTempRoot.MaxRootLength + 1);

        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => tooLong,
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: _ => true); // everything "usable": only the budget rejects the env root

        Assert.Equal(TestTempRoot.Source.Default, resolution.Source);
        Assert.Equal(TestTempRoot.DefaultRoot, resolution.Root);
    }

    // Never throws, even when the environment lookup throws.
    [Fact]
    public void ThrowingEnvironmentLookupDoesNotThrow()
    {
        var resolution = TestTempRoot.Resolve(
            getEnvironmentVariable: _ => throw new InvalidOperationException("boom"),
            defaultRoot: TestTempRoot.DefaultRoot,
            isUsable: path => path == TestTempRoot.DefaultRoot);

        Assert.Equal(TestTempRoot.Source.Default, resolution.Source);
    }

    // The real write-probe proves a write and always deletes the probe file.
    [Fact]
    public void RealProbeSucceedsAndLeavesNoFile()
    {
        var dir = Directory.CreateTempSubdirectory("resolver-probe-").FullName;
        try
        {
            Assert.True(TestTempRoot.IsUsable(dir));
            Assert.Empty(Directory.GetFiles(dir)); // the probe file was deleted
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RealProbeReturnsFalseForAnInvalidRoot() =>
        Assert.False(TestTempRoot.IsUsable("\0:/invalid*root"));

    // The injection the three spawn sites use: SpawnEnvironment carries TMP and TEMP (same
    // value), and ApplyTo MERGES them into an existing dictionary without dropping its entries.
    // Both helpers read the one resolved root, so every spawn site gets the same value.
    [Fact]
    public void SpawnEnvironmentCarriesTmpAndTempEqual()
    {
        var environment = TestTempRoot.SpawnEnvironment();

        Assert.True(environment.ContainsKey("TMP"));
        Assert.True(environment.ContainsKey("TEMP"));
        Assert.Equal(environment["TMP"], environment["TEMP"]);
        Assert.False(string.IsNullOrEmpty(environment["TMP"]));
    }

    // The AppHost `dotnet run` launch must carry the CLI-run-hook suppression so the run is
    // not rerouted through the CLI (which would drop -c Release/--no-launch-profile and start
    // the Debug host). It also keeps the TMP/TEMP routing the other spawns get.
    [Fact]
    public void AppHostRunSpawnEnvironmentSuppressesTheCliRunHook()
    {
        var environment = TestTempRoot.AppHostRunSpawnEnvironment();

        Assert.True(environment.ContainsKey(TestTempRoot.SuppressCliRunHookName));
        Assert.Equal("true", environment[TestTempRoot.SuppressCliRunHookName]);
        Assert.Equal("ASPIRE_SUPPRESS_CLI_RUN_HOOK", TestTempRoot.SuppressCliRunHookName);
        // still routes temp like every other spawn
        Assert.True(environment.ContainsKey("TMP"));
        Assert.Equal(environment["TMP"], environment["TEMP"]);
        Assert.Equal(TestTempRoot.SpawnEnvironment()["TMP"], environment["TMP"]);
    }

    [Fact]
    public void ApplyToMergesAndKeepsExistingEntries()
    {
        var existing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REPORTSERVICE_TEST_RUN_DIRECTORY"] = @"X:\runs\42",
            ["TESTCONTAINERS_RYUK_DISABLED"] = "true",
        };

        TestTempRoot.ApplyTo(existing);

        // existing entries preserved (site Program.cs:612 merges into a non-empty dict)
        Assert.Equal(@"X:\runs\42", existing["REPORTSERVICE_TEST_RUN_DIRECTORY"]);
        Assert.Equal("true", existing["TESTCONTAINERS_RYUK_DISABLED"]);
        // TMP/TEMP injected, and equal to the value the other spawn sites get
        Assert.True(existing.ContainsKey("TMP"));
        Assert.Equal(existing["TMP"], existing["TEMP"]);
        Assert.Equal(TestTempRoot.SpawnEnvironment()["TMP"], existing["TMP"]);
    }
}
