using System.Diagnostics;
using System.Globalization;

namespace ReportService.Testing.Containers;

/// <summary>
/// The container runtime a harness talks to: the local container runtime's own CLI,
/// with the arguments, output parsing and failure text the restart harness used when
/// this surface lived in the case itself. A container the runtime does not hold raises
/// <see cref="ContainerMissingException"/>; any other non-zero exit is a broken call.
/// </summary>
public sealed class DockerContainerRuntime : IContainerRuntime
{
    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(string containerName, CancellationToken cancellationToken)
    {
        var state = await RunAsync(
            ["inspect", "--format", "{{.State.Running}}", containerName],
            cancellationToken,
            containerName);
        return state.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<int> RestartCountAsync(string containerName, CancellationToken cancellationToken)
    {
        var count = await RunAsync(
            ["inspect", "--format", "{{.RestartCount}}", containerName],
            cancellationToken,
            containerName);
        return int.TryParse(count.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <inheritdoc />
    public async Task StartAsync(string containerName, CancellationToken cancellationToken) =>
        await RunAsync(["start", containerName], cancellationToken, containerName);

    private static async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? containerName = null)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("docker could not be started");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var message = $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}";
            if (containerName is not null && IsMissingContainer(error))
            {
                throw new ContainerMissingException(containerName, message);
            }

            throw new InvalidOperationException(message);
        }

        return output;
    }

    // The daemon answers a missing container as `no such object` for an inspect and
    // `No such container` for a start; both mean the name is not held.
    private static bool IsMissingContainer(string error) =>
        error.Contains("no such object", StringComparison.OrdinalIgnoreCase)
        || error.Contains("no such container", StringComparison.OrdinalIgnoreCase);
}
