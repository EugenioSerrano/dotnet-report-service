namespace ReportService.Testing.Fixtures;

/// <summary>One generated snapshot: its id, its exact bytes, its hash and its record count.</summary>
/// <param name="Id">The fixture id.</param>
/// <param name="Bytes">The snapshot's bytes, as they are written to a file.</param>
/// <param name="Sha256">The SHA-256 of <paramref name="Bytes"/>, lower-case hexadecimal.</param>
/// <param name="RecordCount">The number of detail records the snapshot holds.</param>
public sealed record SnapshotFixture(string Id, byte[] Bytes, string Sha256, int RecordCount)
{
    /// <summary>The file name of the snapshot.</summary>
    public string FileName => Id + ".json";

    /// <summary>The snapshot's text.</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Bytes);

    /// <summary>Writes the snapshot into <paramref name="directory"/>.</summary>
    public string WriteTo(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        File.WriteAllBytes(path, Bytes);
        return path;
    }
}
