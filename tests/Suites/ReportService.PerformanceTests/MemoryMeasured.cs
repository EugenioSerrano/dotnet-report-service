namespace ReportService.PerformanceTests;

/// <summary>
/// The cases that sample the process working set while they write share this
/// collection so they never run at the same time: two running at once would read
/// each other's memory.
/// </summary>
[CollectionDefinition(nameof(MemoryMeasured), DisableParallelization = true)]
public sealed class MemoryMeasured;
