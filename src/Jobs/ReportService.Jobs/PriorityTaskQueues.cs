namespace ReportService.Jobs;

/// <summary>
/// The task queue of each priority class for a kind of work. The <see cref="JobPriority.Normal"/>
/// class keeps the kind's own queue name, so every execution recorded before classes existed,
/// and every deployment that never sets a priority, dispatches exactly as before; the other two
/// classes add a suffix.
/// </summary>
public static class PriorityTaskQueues
{
    /// <summary>The suffix of the high class's queue.</summary>
    public const string HighSuffix = "-high";

    /// <summary>The suffix of the low class's queue.</summary>
    public const string LowSuffix = "-low";

    /// <summary>The queue of <paramref name="priority"/> for the kind whose own queue is <paramref name="baseQueue"/>.</summary>
    /// <param name="baseQueue">The kind's own queue: the normal class's.</param>
    /// <param name="priority">The class.</param>
    public static string For(string baseQueue, JobPriority priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseQueue);
        return priority switch
        {
            JobPriority.High => baseQueue + HighSuffix,
            JobPriority.Normal => baseQueue,
            JobPriority.Low => baseQueue + LowSuffix,
            _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
        };
    }

    /// <summary>The three queues of a kind, from the highest class to the lowest.</summary>
    /// <param name="baseQueue">The kind's own queue.</param>
    public static IReadOnlyList<string> All(string baseQueue) =>
        [For(baseQueue, JobPriority.High), For(baseQueue, JobPriority.Normal), For(baseQueue, JobPriority.Low)];

    /// <summary>The class a queue serves, read from its suffix: a queue without one is the normal class's.</summary>
    /// <param name="queue">The queue.</param>
    public static JobPriority ClassOf(string queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        if (queue.EndsWith(HighSuffix, StringComparison.Ordinal))
        {
            return JobPriority.High;
        }

        return queue.EndsWith(LowSuffix, StringComparison.Ordinal) ? JobPriority.Low : JobPriority.Normal;
    }

    /// <summary>
    /// Reads the one class a worker process serves: absent or blank means
    /// <see cref="JobPriority.Normal"/>; otherwise exactly one of the three wire values,
    /// compared ordinally. A list, an unknown value or surrounding text is no class.
    /// </summary>
    /// <param name="text">The setting's value.</param>
    /// <param name="served">The class, when the value names one.</param>
    public static bool TryParseServedClass(string? text, out JobPriority served)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            served = JobPriority.Normal;
            return true;
        }

        return JobWireValues.TryParsePriority(text, out served);
    }

    /// <summary>
    /// The queue a worker process serves for its kind: the queue of the class the setting names,
    /// or the kind's own queue when the setting is absent. A value that names no class also
    /// answers the kind's own queue here; the process's start check refuses it.
    /// </summary>
    /// <param name="baseQueue">The kind's own queue.</param>
    /// <param name="servedClassSetting">The setting's value.</param>
    public static string ServedQueue(string baseQueue, string? servedClassSetting) =>
        For(baseQueue, TryParseServedClass(servedClassSetting, out var served) ? served : JobPriority.Normal);
}
