using System.IO.Compression;

namespace StreamingDataSpike.Parsing;

/// <summary>Opens an input file for one sequential pass: plain, or gzip when the name ends in <c>.gz</c>.</summary>
public static class InputFiles
{
    /// <summary>Opens <paramref name="path"/> for reading.</summary>
    public static Stream Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
            : file;
    }
}
