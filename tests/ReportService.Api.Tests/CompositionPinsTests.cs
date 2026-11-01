using ReportService.Testing;

namespace ReportService.Api.Tests;

/// <summary>Reading the composition's pinned images, as the integration suite does.</summary>
public sealed class CompositionPinsTests : IDisposable
{
    private const string CompletePins = """
        {
          "database": { "image": "db-image", "tag": "1.0" },
          "cache": { "image": "cache-image", "tag": "2.0" },
          "workflowEngine": { "image": "engine-image", "tag": "3.0" }
        }
        """;

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void RepositoryPinsNameAnImageAndATagForEveryDependency()
    {
        var pins = CompositionPins.Load();

        foreach (var image in new[] { pins.Database, pins.Cache, pins.WorkflowEngine })
        {
            Assert.False(string.IsNullOrWhiteSpace(image.Image));
            Assert.False(string.IsNullOrWhiteSpace(image.Tag));
            Assert.Equal($"{image.Image}:{image.Tag}", image.Reference);
        }
    }

    [Fact]
    public void CompleteFileIsReadEntryByEntry()
    {
        var path = _directory.Write("composition-pins.json", CompletePins);

        var pins = CompositionPins.Read(path);

        Assert.Equal(new PinnedImage("db-image", "1.0"), pins.Database);
        Assert.Equal(new PinnedImage("cache-image", "2.0"), pins.Cache);
        Assert.Equal("engine-image:3.0", pins.WorkflowEngine.Reference);
    }

    [Fact]
    public void MissingFileFailsNamingTheFile()
    {
        var path = Path.Combine(_directory.Path, "composition-pins.json");

        var failure = Assert.Throws<InvalidOperationException>(() => CompositionPins.Read(path));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyTagFailsNamingTheFileAndTheField()
    {
        var path = _directory.Write("composition-pins.json", CompletePins.Replace("\"2.0\"", "\" \"", StringComparison.Ordinal));

        var failure = Assert.Throws<InvalidOperationException>(() => CompositionPins.Read(path));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        Assert.Contains("cache.tag", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEntryFailsNamingTheEntry()
    {
        var path = _directory.Write("composition-pins.json", """{ "database": { "image": "a", "tag": "1" }, "cache": { "image": "b", "tag": "2" } }""");

        var failure = Assert.Throws<InvalidOperationException>(() => CompositionPins.Read(path));

        Assert.Contains("'workflowEngine'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedFileFailsNamingTheFile()
    {
        var path = _directory.Write("composition-pins.json", "{ not json");

        var failure = Assert.Throws<InvalidOperationException>(() => CompositionPins.Read(path));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
    }
}
