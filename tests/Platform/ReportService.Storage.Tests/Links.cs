using System.Diagnostics;

namespace ReportService.Storage.Tests;

/// <summary>Creates directory links for the path-safety tests, on any machine the suite runs on.</summary>
internal static class Links
{
    /// <summary>
    /// Creates a link at <paramref name="link"/> to the directory <paramref name="target"/>:
    /// a symbolic link where the user may create one, otherwise (Windows without
    /// that privilege) a junction, which needs none.
    /// </summary>
    public static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            // No symbolic-link privilege: fall back to a junction below.
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "The junction could not be created: " + output);
    }

    /// <summary>Tries to create a file symbolic link; false when the user may not (Windows without the privilege).</summary>
    public static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
