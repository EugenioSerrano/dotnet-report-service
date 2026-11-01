namespace ReportService.Storage.Filesystem;

/// <summary>
/// The temporary files of the Filesystem provider and their start-up sweep.
/// A write's temporary file is named <c>.&lt;file&gt;.tmp-&lt;32 hex&gt;</c>
/// in the target directory; a writer that crashed leaves it behind. At start
/// the provider deletes those older than the stale age. The sweep never
/// touches a file that is not a temporary file, and never follows a link.
/// </summary>
internal static class CleanupTemporaryFiles
{
    /// <summary>The marker between a temporary file's name and its random suffix.</summary>
    public const string Marker = ".tmp-";

    private const int SuffixLength = 32;

    /// <summary>The name of a new temporary file for an object file named <paramref name="fileName"/>.</summary>
    public static string NameFor(string fileName, string randomHex) => "." + fileName + Marker + randomHex;

    /// <summary>True when <paramref name="name"/> has the shape of a temporary file name.</summary>
    public static bool IsTemporaryName(string name)
    {
        if (name.Length < 1 + 1 + Marker.Length + SuffixLength || name[0] != '.')
        {
            return false;
        }

        var markerAt = name.Length - SuffixLength - Marker.Length;
        if (string.CompareOrdinal(name, markerAt, Marker, 0, Marker.Length) != 0)
        {
            return false;
        }

        for (var i = name.Length - SuffixLength; i < name.Length; i++)
        {
            if (name[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Deletes the temporary files under <paramref name="root"/> older than <paramref name="age"/>; returns how many.</summary>
    public static int Sweep(string root, DateTimeOffset now, TimeSpan age)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            // Hidden files are the ones to find (their names start with a dot); links are never followed.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var removed = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (!IsTemporaryName(file.Name) || now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) <= age)
            {
                continue;
            }

            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next start; a temporary file is never visible as an object.
            }
        }

        return removed;
    }
}
