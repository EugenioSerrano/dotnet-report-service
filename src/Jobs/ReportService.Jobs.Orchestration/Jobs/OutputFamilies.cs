using ReportService.Data;
using ReportService.Jobs;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>Why the partition step refused one requested output before anything was produced for it.</summary>
/// <param name="Code">The output's error code.</param>
/// <param name="Reason">The reason the step kept.</param>
public sealed record OutputRefusal(string Code, string Reason);

/// <summary>What one output's execution is started with.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the output.</param>
/// <param name="Dispatch">The job's dispatch class and aging wait, or none when the run read none.</param>
public sealed record OutputStart(string ReportJobId, string OutputId, JobDispatch? Dispatch);

/// <summary>Ends a run after a failure, with the code, the reason and the message it carries.</summary>
/// <param name="reportJobId">The job.</param>
/// <param name="errorCode">The code the job ends with.</param>
/// <param name="reason">The reason the run records.</param>
/// <param name="message">The diagnostic the failure carried, or none.</param>
/// <param name="options">The activity options for the metadata steps.</param>
/// <returns>The task that ends the run.</returns>
public delegate Task EndRunAfterFailure(
    string reportJobId,
    string errorCode,
    string reason,
    string? message,
    ActivityOptions options);

/// <summary>What the run recorded before multi-output jobs existed needs to replay its own single output.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Partition">The partition the data step answered.</param>
/// <param name="Options">The activity options for the metadata steps.</param>
/// <param name="Dispatch">The job's dispatch class and aging wait, or none when the run read none.</param>
/// <param name="EndRunAfterFailure">How the job workflow ends a run after a failure.</param>
public sealed record SingleOutputRun(
    string ReportJobId,
    PartitionResult Partition,
    ActivityOptions Options,
    JobDispatch? Dispatch,
    EndRunAfterFailure EndRunAfterFailure);

/// <summary>
/// One output family, as the job workflow sees it: the format it produces, its place in the fixed
/// start order, the refusal the partition step may have recorded for it, how its own execution is
/// started and how an unexpected fault of that execution is coded. Everything a family produces
/// lives in the family; the job workflow only orders, starts and isolates.
/// </summary>
public interface IOutputFamily
{
    /// <summary>Gets the format this family produces.</summary>
    OutputFormat Format { get; }

    /// <summary>Gets the family's place in the start order; a lower order starts first.</summary>
    int Order { get; }

    /// <summary>Gets a value indicating whether this family owns the single-output path of runs recorded before multi-output jobs.</summary>
    bool OwnsSingleOutputPath => false;

    /// <summary>Answers the refusal the partition step recorded for this family's output, or none.</summary>
    /// <param name="partition">The partition the data step answered.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    OutputRefusal? RefusalAtPartition(PartitionResult partition);

    /// <summary>Starts the family's own execution for one output, from workflow code; the task completes when it ends.</summary>
    /// <param name="start">The job, the output and the dispatch.</param>
    /// <returns>The execution's task.</returns>
    Task StartOutputAsync(OutputStart start);

    /// <summary>Maps an unexpected fault of the family's execution to the output's code.</summary>
    /// <param name="failure">The fault, or none.</param>
    /// <returns>The code.</returns>
    string CodeOf(Exception? failure);

    /// <summary>Replays the single-output path of a run recorded before multi-output jobs, from workflow code.</summary>
    /// <param name="run">What the run needs.</param>
    /// <returns><see langword="true"/> when a failure ended the run, so nothing follows it; otherwise <see langword="false"/>.</returns>
    Task<bool> RunSingleOutputAsync(SingleOutputRun run) =>
        throw new NotSupportedException($"the {Format} family does not own the single-output path.");
}

/// <summary>
/// The output families a deployment produces, in start order: an immutable set with exactly one
/// owner of the recorded single-output path. Two registries are equal when they hold the same
/// families, by format, order and type, in the same order.
/// </summary>
public sealed class OutputFamilyRegistry : IEquatable<OutputFamilyRegistry>
{
    /// <summary>Initializes a new instance of the <see cref="OutputFamilyRegistry"/> class.</summary>
    /// <param name="families">The families; at least one, with distinct formats and orders and one single-output owner.</param>
    public OutputFamilyRegistry(params IOutputFamily[] families)
    {
        ArgumentNullException.ThrowIfNull(families);
        if (families.Length == 0)
        {
            throw new ArgumentException("a registry holds at least one output family.", nameof(families));
        }

        if (families.Any(family => family is null))
        {
            throw new ArgumentException("a registry holds no missing family.", nameof(families));
        }

        if (families.Select(family => family.Format).Distinct().Count() != families.Length)
        {
            throw new ArgumentException("two output families produce the same format.", nameof(families));
        }

        if (families.Select(family => family.Order).Distinct().Count() != families.Length)
        {
            throw new ArgumentException("two output families share a start order.", nameof(families));
        }

        var owners = families.Where(family => family.OwnsSingleOutputPath).ToList();
        if (owners.Count != 1)
        {
            throw new ArgumentException("exactly one output family owns the single-output path.", nameof(families));
        }

        Families = [.. families.OrderBy(family => family.Order)];
        SingleOutputOwner = owners[0];
    }

    /// <summary>Gets the families, in ascending start order.</summary>
    public IReadOnlyList<IOutputFamily> Families { get; }

    /// <summary>Gets the family that owns the single-output path of runs recorded before multi-output jobs.</summary>
    public IOutputFamily SingleOutputOwner { get; }

    /// <inheritdoc />
    public bool Equals(OutputFamilyRegistry? other) =>
        other is not null
        && Families.Count == other.Families.Count
        && Families.Zip(other.Families).All(pair =>
            pair.First.Format == pair.Second.Format
            && pair.First.Order == pair.Second.Order
            && pair.First.GetType() == pair.Second.GetType());

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as OutputFamilyRegistry);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var family in Families)
        {
            hash.Add(family.Format);
            hash.Add(family.Order);
            hash.Add(family.GetType());
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Holds the registry a process uses, set once: setting a registry equal to the one held is a
/// no-op, so every caller that registers the same families - the composition, each replay test -
/// may do so in any order and concurrently; setting a different one is refused.
/// </summary>
public sealed class OutputFamilyHolder
{
    private OutputFamilyRegistry? _registry;

    /// <summary>Gets the registry held.</summary>
    /// <exception cref="InvalidOperationException">No registry was set.</exception>
    public OutputFamilyRegistry Current =>
        Volatile.Read(ref _registry)
        ?? throw new InvalidOperationException("no output family is registered: the composition registers the families before any worker starts.");

    /// <summary>Sets the registry, or confirms the equal one already held.</summary>
    /// <param name="registry">The registry.</param>
    /// <exception cref="InvalidOperationException">A different registry is already held.</exception>
    public void Use(OutputFamilyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var held = Interlocked.CompareExchange(ref _registry, registry, null);
        if (held is not null && !held.Equals(registry))
        {
            throw new InvalidOperationException("a different set of output families is already registered.");
        }
    }
}

/// <summary>The registry the job workflow reads, held for the process.</summary>
public static class OutputFamilies
{
    private static readonly OutputFamilyHolder Holder = new();

    /// <summary>Gets the registry held for the process.</summary>
    public static OutputFamilyRegistry Current => Holder.Current;

    /// <summary>Sets the process's registry, or confirms the equal one already held.</summary>
    /// <param name="registry">The registry.</param>
    public static void Use(OutputFamilyRegistry registry) => Holder.Use(registry);
}
