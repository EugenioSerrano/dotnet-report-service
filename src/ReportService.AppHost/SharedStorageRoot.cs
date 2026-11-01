using System.ComponentModel;
using System.Diagnostics;

namespace ReportService.AppHost;

/// <summary>
/// The storage root of the local composition: <c>&lt;repository&gt;/.storage</c>,
/// outside <c>src/</c> and ignored by version control. One physical directory,
/// two logical roots: the API and the orchestration worker use the host path,
/// the PDF worker sees the same directory mounted at <see cref="ContainerPath"/>
/// and writes there as its non-root user.
/// </summary>
/// <param name="HostPath">The root as the host processes see it.</param>
/// <param name="MountSource">The host directory as the container engine must be given it for the mount.</param>
internal sealed record SharedStorageRoot(string HostPath, string MountSource)
{
    /// <summary>The provider every process of the composition selects.</summary>
    public const string Provider = "filesystem";

    /// <summary>The root as the PDF worker sees it.</summary>
    public const string ContainerPath = "/data/storage";

    /// <summary>The directory name under the repository root.</summary>
    public const string DirectoryName = ".storage";

    /// <summary>
    /// Describes the root of the repository at <paramref name="repositoryRoot"/>.
    /// On a Windows host whose container engine runs in a WSL distribution
    /// (for example Rancher Desktop), the engine accepts only its own form of
    /// a Windows path — drive <c>D:</c> is <c>/mnt/d</c> — and the composition
    /// passes the path unchanged, so the mount source is given in that form.
    /// Docker Desktop translates Windows paths itself and gets the path as is.
    /// </summary>
    public static async Task<SharedStorageRoot> DescribeAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var hostPath = Path.Combine(repositoryRoot, DirectoryName);
        var mountSource = OperatingSystem.IsWindows() && !await IsDockerDesktopAsync(cancellationToken)
            ? ToEnginePath(hostPath)
            : hostPath;
        return new SharedStorageRoot(hostPath, mountSource);
    }

    /// <summary>A Windows drive path in the form a WSL-hosted engine mounts it (<c>D:\a\b</c> → <c>/mnt/d/a/b</c>); other paths unchanged.</summary>
    public static string ToEnginePath(string windowsPath)
    {
        if (windowsPath.Length >= 3 && char.IsAsciiLetter(windowsPath[0]) && windowsPath[1] == ':' && windowsPath[2] is '\\' or '/')
        {
            return "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
        }

        return windowsPath;
    }

    // Asks the engine what it is; only reads, never changes anything.
    private static async Task<bool> IsDockerDesktopAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "info", "--format", "{{.OperatingSystem}}" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && output.Trim() == "Docker Desktop";
        }
        catch (Win32Exception)
        {
            // No container engine: the composition cannot start its containers either way.
            return false;
        }
    }
}
