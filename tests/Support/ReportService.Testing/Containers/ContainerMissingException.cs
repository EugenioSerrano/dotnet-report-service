namespace ReportService.Testing.Containers;

/// <summary>
/// The runtime answered that a container it was asked about does not exist. It is an
/// <see cref="InvalidOperationException"/>, so a bounded wait records it exactly as it
/// records any other failed probe, and its type distinguishes a missing container from
/// a runtime CLI that failed for another reason.
/// </summary>
public sealed class ContainerMissingException : InvalidOperationException
{
    /// <summary>Creates the failure for the container the runtime did not hold.</summary>
    /// <param name="containerName">The name the runtime did not hold.</param>
    /// <param name="message">The runtime's own failure text.</param>
    /// <param name="innerException">The failure that reported the missing container, if any.</param>
    public ContainerMissingException(string containerName, string message, Exception? innerException = null)
        : base(message, innerException) => ContainerName = containerName;

    /// <summary>The name the runtime did not hold.</summary>
    public string ContainerName { get; }
}
