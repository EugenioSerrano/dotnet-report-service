using System.Globalization;

namespace ReportService.Jobs;

/// <summary>
/// The single-document capacity a deployment admits a PDF output against: the most
/// pages and the most bytes of committed parts one output may reach, and the name the
/// deployment gives the profile those values came from. The product imposes no value
/// of its own: the values are the deployment's configuration. When a deployment
/// configures none, <see cref="Example"/> applies - a starting point carrying the last
/// published reference measurement, which a deployment replaces with its own.
/// </summary>
/// <param name="MaximumPages">The most pages one output's committed parts may reach.</param>
/// <param name="MaximumPartBytes">The most bytes one output's committed parts may reach.</param>
/// <param name="ProfileName">The deployment's name for this capacity profile; written to logs, never to a metric tag.</param>
public sealed record DocumentCapacity(int MaximumPages, long MaximumPartBytes, string ProfileName)
{
    /// <summary>The pages of the example profile: the last published reference measurement.</summary>
    public const int ExamplePages = 10_000;

    /// <summary>The parts bytes of the example profile: the last published reference measurement (36 MiB).</summary>
    public const long ExamplePartBytes = 37_748_736L;

    /// <summary>The name of the example profile.</summary>
    public const string ExampleProfileName = "example-reference-10k";

    /// <summary>The limit name a pages refusal carries.</summary>
    public const string PagesLimit = "document-pages";

    /// <summary>The limit name a bytes refusal carries.</summary>
    public const string BytesLimit = "document-bytes";

    /// <summary>The example profile, used when the deployment configures no capacity.</summary>
    public static DocumentCapacity Example { get; } = new(ExamplePages, ExamplePartBytes, ExampleProfileName);

    /// <summary>
    /// The capacity a deployment's settings describe: each value it configured, and the
    /// example's value for each it did not. A deployment that configures a value names its
    /// own profile; one that configures nothing runs the example under the example's name.
    /// </summary>
    /// <param name="maximumPages">The configured page capacity, if any.</param>
    /// <param name="maximumPartBytes">The configured parts-bytes capacity, if any.</param>
    /// <param name="profileName">The configured profile name, if any.</param>
    public static DocumentCapacity From(int? maximumPages, long? maximumPartBytes, string? profileName) =>
        new(
            maximumPages ?? ExamplePages,
            maximumPartBytes ?? ExamplePartBytes,
            profileName ?? ExampleProfileName);

    /// <summary>
    /// Judges running sums against this capacity: <see langword="null"/> when they fit, or
    /// the refusal - the limit, its configured value and the reached value - for the first
    /// dimension they pass, pages before bytes.
    /// </summary>
    /// <param name="reachedPages">The pages the output's committed parts would reach.</param>
    /// <param name="reachedBytes">The bytes the output's committed parts would reach.</param>
    public CapacityRefusal? Judge(long reachedPages, long reachedBytes)
    {
        if (reachedPages > MaximumPages)
        {
            return new CapacityRefusal(PagesLimit, MaximumPages, reachedPages);
        }

        return reachedBytes > MaximumPartBytes
            ? new CapacityRefusal(BytesLimit, MaximumPartBytes, reachedBytes)
            : null;
    }
}

/// <summary>A refusal for capacity: the limit passed, its configured value and the value reached.</summary>
/// <param name="Limit">The limit's name.</param>
/// <param name="LimitValue">The configured value of the limit.</param>
/// <param name="ReachedValue">The value the output would reach.</param>
public sealed record CapacityRefusal(string Limit, long LimitValue, long ReachedValue)
{
    /// <summary>The stored reason: the limit, its value and the reached value - never a data value.</summary>
    public string Reason =>
        "limit=" + Limit
        + " limitValue=" + LimitValue.ToString(CultureInfo.InvariantCulture)
        + " reached=" + ReachedValue.ToString(CultureInfo.InvariantCulture);
}
