using System.Security.AccessControl;
using System.Security.Principal;

namespace ReportService.Testing;

/// <summary>
/// Makes an existing directory unwritable for the current user until
/// disposed: on Windows a deny rule for creating, writing and deleting
/// (inherited by its contents), elsewhere the mode bits <c>r-x</c>. Restores
/// the directory when disposed. A test running as a Unix superuser is not
/// restricted by mode bits.
/// </summary>
public sealed class UnwritableDirectory : IDisposable
{
    private readonly string _path;
    private bool _restored;

    /// <summary>Makes <paramref name="path"/> unwritable.</summary>
    public UnwritableDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            security.AddAccessRule(DenyRule());
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Makes the directory writable again; calling it more than once has no further effect.</summary>
    public void Restore()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(_path);
            var security = directory.GetAccessControl();
            security.RemoveAccessRule(DenyRule());
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Restore();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSystemAccessRule DenyRule() => new(
        WindowsIdentity.GetCurrent().User!,
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
            | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Deny);
}
