using System.Text;

namespace ReportService.Templates.Tests;

/// <summary>
/// A package folder: a name is resolved inside it, and a name that points anywhere
/// else is refused before the file system is asked.
/// </summary>
public sealed class FolderTemplatePackageFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "report-service-package-" + Guid.NewGuid().ToString("N"));

    public FolderTemplatePackageFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "html"));
        File.WriteAllText(Path.Combine(folder, "html", "row.html.sbn"), "<tr>{{ item.sequence }}</tr>", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "..", Path.GetFileName(folder) + "-sibling.txt"), "outside", new UTF8Encoding(false));
    }

    public void Dispose()
    {
        var sibling = Path.Combine(folder, "..", Path.GetFileName(folder) + "-sibling.txt");
        if (File.Exists(sibling))
        {
            File.Delete(sibling);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFileOfThePackageIsRead()
    {
        var files = new FolderTemplatePackageFiles(folder);

        Assert.True(files.Exists("html/row.html.sbn"));
        Assert.Equal("<tr>{{ item.sequence }}</tr>", files.ReadAllText("html/row.html.sbn"));
    }

    [Theory]
    [InlineData("../" + "sibling.txt")]
    [InlineData("html/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("html\\row.html.sbn")]
    [InlineData("html//row.html.sbn")]
    [InlineData("./html/row.html.sbn")]
    [InlineData("")]
    public void ANameOutsideThePackageIsRefused(string path)
    {
        var files = new FolderTemplatePackageFiles(folder);

        Assert.Throws<TemplatePackageException>(() => files.ReadAllText(path));
        Assert.Throws<TemplatePackageException>(() => files.Exists(path));
    }

    [Fact]
    public void AFileThePackageDoesNotHoldIsRefusedWhenItIsOpened()
    {
        var files = new FolderTemplatePackageFiles(folder);

        Assert.False(files.Exists("html/missing.sbn"));
        Assert.Throws<TemplatePackageException>(() => files.OpenRead("html/missing.sbn"));
    }
}
