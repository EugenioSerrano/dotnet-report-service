using System.Text.RegularExpressions;
using ReportService.Testing;

namespace ReportService.TemplatePackages.Tests.Architecture;

/// <summary>
/// The version store's insert and the table it writes, held as a test rather than as a
/// habit: the table declares exactly two ways a row is refused as a duplicate, no script
/// of the migration set takes either away, the insert names neither of them — so a
/// duplicate on either is answered instead of raised — and the read that answers it looks
/// the row up by the identity the insert commits.
/// </summary>
public sealed class TemplateVersionInsertStatementTests
{
    private const string Store = "PostgresTemplateVersionStore.cs";

    private const string StoreDirectory = "Records";

    private const string Table = "report_service.template_versions";

    private const string CreateScript = "0007_template-versions.sql";

    private const string InsertAnchor = "private const string InsertSql =";

    private const string FindAnchor = "private const string FindSql =";

    [Fact]
    public void TheTableDeclaresTwoUniqueArbitersAndNoMigrationChangesThem()
    {
        var scripts = ScriptsThatTouchTheTable();
        Assert.NotEmpty(scripts);

        var create = scripts.Single(script => string.Equals(script.Name, CreateScript, StringComparison.Ordinal));
        Assert.True(
            Regex.IsMatch(create.Text, @"object_key\s+text\s+NOT NULL UNIQUE"),
            $"{CreateScript} no longer declares the object key unique; the insert answers a duplicate on it, and the key is the guard that one object cannot be named twice.");
        Assert.True(
            Regex.IsMatch(create.Text, @"PRIMARY KEY \(template_id,\s*version\)"),
            $"{CreateScript} no longer declares the identity (template_id, version) as the primary key; that is the row a take commits and the row a loser reads.");

        var uniques = scripts.Sum(script => Occurrences(script.Text, "UNIQUE"));
        var primaries = scripts.Sum(script => Occurrences(script.Text, "PRIMARY KEY"));
        Assert.True(
            uniques == 1,
            $"The migration set declares {uniques} unique declarations over {Table}; this table is expected to carry exactly one, the object key. "
            + "A second unique index is a second way a duplicate is refused, and the insert has to answer it.");
        Assert.True(
            primaries == 1,
            $"The migration set declares {primaries} primary keys over {Table}; this table is expected to carry exactly one, its identity. "
            + "A different identity changes the row a take commits and a loser reads.");
        Assert.Equal(2, uniques + primaries);

        foreach (var script in scripts)
        {
            foreach (var statement in script.Text.Split(';'))
            {
                if (!statement.Contains(Table, StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(
                    statement.Contains("DROP CONSTRAINT", StringComparison.Ordinal) || statement.Contains("DROP INDEX", StringComparison.Ordinal),
                    $"{script.Name} drops a constraint or an index of {Table}; the insert's arbitration is read against the table's declarations, "
                    + "so a script that takes one away has to be read with the insert.");
            }
        }
    }

    [Fact]
    public void TheInsertNamesNoSingleArbiterWhileTheTableDeclaresTwo()
    {
        var insert = Literal(PathOfStore(), InsertAnchor, Store);

        var named = Regex.Match(insert, @"ON CONFLICT \(([^)]*)\)");
        Assert.True(
            !named.Success,
            $"{Store}'s insert names the arbiter '{named.Groups[1].Value}' while the table declares two unique ones — the identity and the object key. "
            + "A duplicate on the arbiter it does not name is raised as an error instead of being answered, and the loser of that race cannot read the winner. "
            + "The clause answers every unique index by naming none.");

        Assert.True(
            insert.Contains("ON CONFLICT DO NOTHING", StringComparison.Ordinal),
            $"{Store}'s insert no longer answers a duplicate with DO NOTHING; a duplicate on either unique index would be raised instead of answered.");
        Assert.True(
            insert.Contains("RETURNING template_id", StringComparison.Ordinal),
            $"{Store}'s insert no longer tells an inserted row from an existing one by a returned row; the callers depend on that answer.");
    }

    [Fact]
    public void TheLoserIsReadByTheIdentityTheInsertCommits()
    {
        var find = Literal(PathOfStore(), FindAnchor, Store);

        Assert.True(
            find.Contains("template_id = @TemplateId", StringComparison.Ordinal),
            $"{Store}'s read no longer looks a row up by its template id; the read is what answers a duplicate the insert met.");
        Assert.True(
            find.Contains("version = @Version", StringComparison.Ordinal),
            $"{Store}'s read no longer looks a row up by its version; the read is what answers a duplicate the insert met.");
    }

    private static string PathOfStore() =>
        Path.Combine(RepositoryPaths.Root, "src", "Templates", "ReportService.TemplatePackages", StoreDirectory, Store);

    private static string Literal(string path, string anchor, string fileName)
    {
        Assert.True(File.Exists(path), $"{fileName} was not found at {path}.");
        var source = File.ReadAllText(path);
        var at = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(
            at >= 0,
            $"{fileName} no longer carries '{anchor}'. This test reads the statement from that anchor; re-point it before the statement's wording changes.");
        var open = source.IndexOf("\"\"\"", at, StringComparison.Ordinal);
        var close = open < 0 ? -1 : source.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
        Assert.True(
            open >= 0 && close > open,
            $"{fileName}'s '{anchor}' is no longer followed by one raw string literal. This test reads the statement from that literal.");
        var literal = source[(open + 3)..close];
        Assert.False(string.IsNullOrWhiteSpace(literal), $"{fileName}'s statement under '{anchor}' is empty.");
        return literal;
    }

    private static IReadOnlyList<(string Name, string Text)> ScriptsThatTouchTheTable()
    {
        var directory = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations");
        return
        [
            .. Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Select(file => (Name: Path.GetFileName(file), Text: File.ReadAllText(file)))
                .Where(script => script.Text.Contains(Table, StringComparison.Ordinal)),
        ];
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
