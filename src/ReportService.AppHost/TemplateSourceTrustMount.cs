namespace ReportService.AppHost;

/// <summary>
/// The source's trust anchor as the composition forwards it: one file, mounted
/// read-only into the worker at <see cref="ContainerPath"/>, and the worker given
/// that path as its <see cref="SettingName"/>. It holds a rule, no resource and no
/// secret. A deployment that configures no anchor has nothing to mount, and the
/// worker it starts holds no anchor and reads the platform's own store.
/// </summary>
/// <param name="MountSource">The anchor file in the form the container engine accepts for a mount.</param>
public sealed record TemplateSourceTrustMount(string MountSource)
{
    /// <summary>Where the worker finds the anchor inside its container.</summary>
    public const string ContainerPath = "/etc/report-service/template-source/trust-anchor.pem";

    /// <summary>The worker's setting that names that path.</summary>
    public const string SettingName = "TemplateSource__TrustAnchor";

    /// <summary>
    /// Describes the mount of the anchor at <paramref name="hostPath"/>; <c>null</c>
    /// when the deployment configured none — absent, empty or blank — because then
    /// there is nothing to mount and no setting to give.
    /// </summary>
    /// <param name="hostPath">The anchor's path on the host, as the deployment configured it.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public static async Task<TemplateSourceTrustMount?> DescribeAsync(string? hostPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return null;
        }

        return new TemplateSourceTrustMount(await SharedStorageRoot.ToMountSourceAsync(hostPath, cancellationToken));
    }
}
