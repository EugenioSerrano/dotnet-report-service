using System.Text;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The scripts embedded in the assembly: the shipped baseline only, its
/// checksum equal to the file in the source tree, and the validation a bad name,
/// a number used twice or an empty script is refused by.
/// </summary>
public sealed class MigrationCatalogTests
{
    private const string Baseline = "0001_baseline.sql";

    [Fact]
    public void TheShippedSetIsTheBaselineOnly()
    {
        var script = Assert.Single(MigrationCatalog.Scripts);

        Assert.Equal(Baseline, script.Name);
        Assert.True(MigrationScriptName.IsValid(script.Name));
        Assert.Equal(Baseline, MigrationCatalog.ExpectedVersion);
    }

    [Fact]
    public void TheBaselineChecksumIsTheSha256OfTheFileInTheSourceTree()
    {
        var path = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", Baseline);
        var bytes = File.ReadAllBytes(path);
        var script = Assert.Single(MigrationCatalog.Scripts);

        Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
        Assert.Equal(Encoding.UTF8.GetString(bytes), script.Sql);
    }

    [Fact]
    public void TheBaselineHoldsNoBusinessTable()
    {
        var script = Assert.Single(MigrationCatalog.Scripts);

        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS report_service;", script.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecksumIsTheLowercaseHexSha256OfAKnownByteString()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            MigrationScript.ChecksumOf("abc"u8));
    }

    [Fact]
    public void ScriptsAreOrderedByTheirName()
    {
        var scripts = MigrationCatalog.Create(
        [
            (Name: "0010_ten.sql", Bytes: "SELECT 10"u8.ToArray()),
            (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
        ]);

        Assert.Equal(["0002_two.sql", "0010_ten.sql"], scripts.Select(script => script.Name));
        Assert.Equal("0010_ten.sql", MigrationCatalog.ExpectedVersionOf(scripts));
    }

    [Fact]
    public void AMalformedNameIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "1_baseline.sql", Bytes: "SELECT 1"u8.ToArray())]));

        Assert.Contains("does not follow the name rule", error.Message, StringComparison.Ordinal);
        Assert.Contains("1_baseline.sql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberUsedTwiceIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create(
            [
                (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
                (Name: "0002_other.sql", Bytes: "SELECT 3"u8.ToArray()),
            ]));

        Assert.Contains("migration script number 0002 is used twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyScriptIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "0001_baseline.sql", Bytes: []), (Name: "0002_blank.sql", Bytes: "  \n"u8.ToArray())]));

        Assert.Equal("migration script '0001_baseline.sql' is empty", error.Message);
    }

    [Fact]
    public void AnEmptySetIsCurrentAtNone()
    {
        var scripts = MigrationCatalog.Create([]);

        Assert.Empty(scripts);
        Assert.Equal(SchemaVersion.None, MigrationCatalog.ExpectedVersionOf(scripts));
    }
}
