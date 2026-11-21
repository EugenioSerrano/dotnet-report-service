using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ReportService.Storage.Filesystem;

/// <summary>
/// Publishes a complete temporary file under its final name only when no file
/// has that name, atomically: two writers racing for one name never both
/// succeed, and the published file is always one writer's whole content.
/// On Windows a move without overwrite is that operation. On Linux the .NET
/// move without overwrite is a check followed by a rename, which a concurrent
/// writer can slip between, so a hard link is created instead: creating a
/// link fails atomically when the name exists.
/// </summary>
internal static class CreateOnlyPublish
{
    /// <summary>
    /// Publishes <paramref name="temporaryPath"/> as <paramref name="destinationPath"/>;
    /// false when the destination already exists. The caller removes the
    /// temporary name afterwards in every case (on Linux it remains as a second
    /// name of the published file). Throws <see cref="StorageException"/> when
    /// the file system cannot publish without risking an overwrite.
    /// </summary>
    public static bool TryPublish(string temporaryPath, string destinationPath) =>
        OperatingSystem.IsWindows()
            ? TryMove(temporaryPath, destinationPath)
            : UnixLink.TryPublish(temporaryPath, destinationPath);

    private static bool TryMove(string temporaryPath, string destinationPath)
    {
        try
        {
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return true;
        }
        catch (IOException) when (Path.Exists(destinationPath))
        {
            return false;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Runs only on Linux and other Unix systems; the coverage run is on the Windows host, and this path is proven by the same suite run on Linux.")]
    private static class UnixLink
    {
        private const int AlreadyExists = 17;
        private const int PermissionDenied = 13;
        private static readonly int[] Unsupported = [1, 18, 31, 38, 95];

        public static bool TryPublish(string temporaryPath, string destinationPath)
        {
            // The temporary name stays linked to the published file; the caller removes it in every case.
            if (Link(temporaryPath, destinationPath) == 0)
            {
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == AlreadyExists)
            {
                return false;
            }

            if (error == PermissionDenied)
            {
                throw new StorageException(StorageOutcome.Permanent, new StorageError(
                    StorageErrorCodes.PermissionDenied, "The storage root does not allow a new object to be published."));
            }

            if (Array.IndexOf(Unsupported, error) >= 0)
            {
                throw new StorageException(StorageOutcome.Permanent, new StorageError(
                    StorageErrorCodes.CreateOnlyUnsupported, "The file system of the storage root cannot publish an object without risking an overwrite (no hard links)."));
            }

            throw new StorageException(StorageOutcome.Transient, new StorageError(
                StorageErrorCodes.IoError, $"Publishing an object failed with system error {error}."));
        }

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
    }
}
