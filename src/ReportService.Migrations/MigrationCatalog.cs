using System.Reflection;
using ReportService.ServiceDefaults;

namespace ReportService.Migrations;

/// <summary>
/// The scripts embedded in this assembly, validated and in name order, and the
/// version the code expects: the name of the highest script. The set is read
/// once and cached, so the migrator and the composition read the same value.
/// </summary>
public static class MigrationCatalog
{
    private const string ResourcePrefix = "Migrations/";

    private static readonly Lazy<IReadOnlyList<MigrationScript>> Loaded =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The embedded scripts, validated and in name order.</summary>
    public static IReadOnlyList<MigrationScript> Scripts => Loaded.Value;

    /// <summary>The name of the highest embedded script, or <c>none</c> for an empty set.</summary>
    public static string ExpectedVersion => ExpectedVersionOf(Scripts);

    /// <summary>The name of the highest script of <paramref name="scripts"/>, or <c>none</c>.</summary>
    public static string ExpectedVersionOf(IReadOnlyList<MigrationScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        return scripts.Count == 0 ? SchemaVersion.None : scripts[^1].Name;
    }

    /// <summary>
    /// Validates and orders a script set. A malformed name, a number used twice
    /// or an empty script is refused with a message naming the script, so a bad
    /// set fails before anything is applied.
    /// </summary>
    public static IReadOnlyList<MigrationScript> Create(IEnumerable<(string Name, byte[] Bytes)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var scripts = new List<MigrationScript>();
        foreach (var (name, bytes) in sources)
        {
            if (MigrationScriptName.Problem(name) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            var sql = MigrationScript.SqlOf(bytes);
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidOperationException($"migration script '{name}' is empty");
            }

            scripts.Add(new MigrationScript(name, sql, MigrationScript.ChecksumOf(bytes)));
        }

        scripts.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        for (var index = 1; index < scripts.Count; index++)
        {
            var number = MigrationScriptName.Number(scripts[index].Name);
            if (string.Equals(number, MigrationScriptName.Number(scripts[index - 1].Name), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"migration script number {number} is used twice ('{scripts[index - 1].Name}', '{scripts[index].Name}')");
            }
        }

        return scripts;
    }

    private static IReadOnlyList<MigrationScript> Load()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        return Create([.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => (Name: name[ResourcePrefix.Length..], Bytes: Read(assembly, name)))]);
    }

    private static byte[] Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded script '{resourceName}' cannot be read");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
