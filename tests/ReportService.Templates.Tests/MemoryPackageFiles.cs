using System.Text;

namespace ReportService.Templates.Tests;

/// <summary>A package held in memory, so a case composes without a folder.</summary>
internal sealed class MemoryPackageFiles(IReadOnlyDictionary<string, string> files) : ITemplatePackageFiles
{
    public bool Exists(string path) => files.ContainsKey(path);

    public Stream OpenRead(string path) =>
        files.TryGetValue(path, out var text)
            ? new MemoryStream(Encoding.UTF8.GetBytes(text))
            : throw new TemplatePackageException($"The package holds no file named '{path}'.");

    public string ReadAllText(string path) =>
        files.TryGetValue(path, out var text) ? text : throw new TemplatePackageException($"The package holds no file named '{path}'.");
}
