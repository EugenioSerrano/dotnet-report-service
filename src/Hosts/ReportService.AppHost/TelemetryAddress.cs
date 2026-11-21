namespace ReportService.AppHost;

/// <summary>
/// How a container of this composition reaches a receiver running on the host.
/// A container does not see the host's loopback interface, so an address the
/// host reaches on <c>localhost</c> is rewritten to the name the local
/// container runtime resolves to the host — the reference environment,
/// Docker Desktop, is the one this repository documents.
/// </summary>
internal static class TelemetryAddress
{
    /// <summary>The name a container uses for the machine the composition runs on.</summary>
    public const string HostNameInContainers = "host.docker.internal";

    /// <summary>
    /// The address a container uses for <paramref name="endpoint"/>: unchanged
    /// when it is not a loopback address, rewritten to
    /// <see cref="HostNameInContainers"/> when it is, and
    /// <see langword="null"/> when no absolute address is configured.
    /// </summary>
    public static string? ContainerReachable(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            return endpoint;
        }

        var reachable = new UriBuilder(uri) { Host = HostNameInContainers }.Uri;
        return reachable.GetLeftPart(UriPartial.Authority);
    }
}
