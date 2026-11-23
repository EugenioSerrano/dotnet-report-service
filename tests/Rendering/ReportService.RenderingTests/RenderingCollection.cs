namespace ReportService.RenderingTests;

/// <summary>
/// The rendering suite shares one run: the images are built once, the fixtures are
/// written once, and every case prints into the same folder. The classes below run
/// one after another inside that collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RenderingCollection : ICollectionFixture<RenderingSuite>
{
    /// <summary>The collection's name.</summary>
    public const string Name = "rendering";
}
