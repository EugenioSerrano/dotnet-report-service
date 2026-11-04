using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Resolution;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The take seam held in memory: each <c>(template, version)</c> is seeded with
/// what the take would answer — a record, or one of its own refusals with the
/// code, the text to fix and the retryable flag. Every call is logged with the
/// template, the version, the requesting consumer, the commit and the job, so a
/// case can prove which version was taken and which never was, and the token the
/// resolver passes is honoured, so a walk can be cut off inside a take.
/// </summary>
public sealed class InMemoryTemplateTake : ITemplateTake
{
    private readonly Dictionary<(string TemplateId, string Version), TakeAttempt> _answers = [];
    private readonly List<TakeCall> _calls = [];
    private readonly Lock _gate = new();

    /// <summary>Every call the seam received, in order.</summary>
    public IReadOnlyList<TakeCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Runs before the seeded answer, so a case can edit the branch or move the clock inside a take.</summary>
    public Action<TakeCall>? BeforeTake { get; set; }

    /// <summary>When set, a take waits until the token it was given is cancelled instead of answering.</summary>
    public bool WaitForCancellation { get; set; }

    /// <summary>Seeds the record the take answers with.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="record">The record the take produces.</param>
    public void SeedTaken(string templateId, string version, TemplateVersionRecord record) =>
        Seed(templateId, version, TakeAttempt.Taken(record));

    /// <summary>Seeds a refusal the take answers with.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="code">The take's code.</param>
    /// <param name="reason">What the author has to fix.</param>
    /// <param name="retryable">Whether taking the same version again later may succeed.</param>
    public void SeedRefused(string templateId, string version, string code, string reason, bool retryable) =>
        Seed(templateId, version, TakeAttempt.Refused(code, reason, retryable));

    /// <summary>Seeds what the take answers for one version.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="answer">The answer.</param>
    public void Seed(string templateId, string version, TakeAttempt answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        lock (_gate)
        {
            _answers[(templateId, version)] = answer;
        }
    }

    /// <summary>Forgets every call so far, keeping the seeded answers.</summary>
    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<TakeAttempt> TakeAsync(
        string templateId,
        string version,
        string requestingConsumerId,
        SourceCommit? commit,
        string? reportJobId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(requestingConsumerId);

        var call = new TakeCall(templateId, version, requestingConsumerId, commit?.CommitSha, reportJobId);
        lock (_gate)
        {
            _calls.Add(call);
        }

        BeforeTake?.Invoke(call);

        if (WaitForCancellation && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_answers.TryGetValue((templateId, version), out var answer))
        {
            throw new InvalidOperationException($"the take double holds no answer for '{templateId}' version '{version}'");
        }

        return answer;
    }

    /// <summary>One call the take seam received.</summary>
    /// <param name="TemplateId">The template's identifier.</param>
    /// <param name="Version">The version.</param>
    /// <param name="RequestingConsumerId">The consumer the take ran for.</param>
    /// <param name="CommitSha">The commit the resolution read the candidate set at, when it had one.</param>
    /// <param name="ReportJobId">The job the take ran on behalf of, when there was one.</param>
    public sealed record TakeCall(
        string TemplateId,
        string Version,
        string RequestingConsumerId,
        string? CommitSha,
        string? ReportJobId);
}
