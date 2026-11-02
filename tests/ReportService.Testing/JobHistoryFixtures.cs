using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Testing;

/// <summary>
/// One recorded job history: the file the suite reads it by and the two settings of the
/// wait the recording was taken with. A recording replays only under the bounds it was
/// captured with, so the capture and every case that replays or bounds a recording read
/// this one table instead of each repeating a value that has to agree.
/// </summary>
/// <param name="File">The name the committed history is read by.</param>
/// <param name="Backstop">How long the recorded run waited between two checks of its upload.</param>
/// <param name="ContinueAsNewAfter">The checks the recorded run was allowed to take before it continued as a new run.</param>
public sealed record JobHistoryFixture(string File, TimeSpan Backstop, int ContinueAsNewAfter);

/// <summary>
/// The job histories the hermetic suite replays. Each is the outcome of one real execution
/// of the workflow and its steps, recorded by the opt-in capture case of the integration
/// suite, and each is committed with the change that captured it.
/// </summary>
public static class JobHistoryFixtures
{
    /// <summary>A job whose upload was ready at its first check: it never waits.</summary>
    public const string ReadyFile = "report-job-ready.json";

    /// <summary>A job whose upload had been rejected at its first check.</summary>
    public const string RejectedFile = "report-job-rejected.json";

    /// <summary>A job whose upload was still being verified at its first check.</summary>
    public const string VerifyingFile = "report-job-verifying.json";

    /// <summary>A job waiting when its upload became ready and a notification woke it.</summary>
    public const string WaitingThenReadyAfterANotificationFile = "report-job-waiting-ready-after-a-notification.json";

    /// <summary>A job waiting when its upload was rejected.</summary>
    public const string WaitingThenRejectedFile = "report-job-waiting-rejected.json";

    /// <summary>A job waiting when its upload's verification deadline passed.</summary>
    public const string WaitingThenExpiredFile = "report-job-waiting-expired.json";

    /// <summary>The run of a job that took every check it may and continued as a new run.</summary>
    public const string ContinuedAsNewFile = "report-job-continued-as-new.json";

    /// <summary>The run that followed the continuation, ended by a notification of a ready upload.</summary>
    public const string ContinuedAfterANotificationFile = "report-job-continued-after-a-notification.json";

    /// <summary>A job whose upload was ready at its first check: it never waits.</summary>
    public static readonly JobHistoryFixture Ready = new(
        ReadyFile,
        InputWaitOptions.DefaultBackstop,
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>A job whose upload had been rejected at its first check.</summary>
    public static readonly JobHistoryFixture Rejected = new(
        RejectedFile,
        InputWaitOptions.DefaultBackstop,
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>
    /// A job whose upload was still confirming at its first check: the wait is recorded,
    /// its own timer ends it, and the upload's own transition answers the check that
    /// follows. Taken with the shortest accepted interval, so the recording shows the timer
    /// doing the waking.
    /// </summary>
    public static readonly JobHistoryFixture Verifying = new(
        VerifyingFile,
        TimeSpan.FromSeconds(1),
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>
    /// A job waiting when its upload became ready: the notification ends the wait and the
    /// timer of that check never fires. Taken with the longest accepted interval, so a lost
    /// notification could not reach its timer inside the recording.
    /// </summary>
    public static readonly JobHistoryFixture WaitingThenReadyAfterANotification = new(
        WaitingThenReadyAfterANotificationFile,
        InputWaitOptions.MaximumBackstop,
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>A job waiting when its upload was rejected: the job ends with the upload's own code.</summary>
    public static readonly JobHistoryFixture WaitingThenRejected = new(
        WaitingThenRejectedFile,
        InputWaitOptions.DefaultBackstop,
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>A job waiting when its upload's verification deadline passed, with nothing yet marking it expired.</summary>
    public static readonly JobHistoryFixture WaitingThenExpired = new(
        WaitingThenExpiredFile,
        InputWaitOptions.DefaultBackstop,
        InputWaitOptions.DefaultContinueAsNewAfter);

    /// <summary>
    /// The run of a job that took every check one run may take and continued as a new run.
    /// It was taken with two checks, the shortest limit that shows a continuation; the
    /// relationship to the history bound is asserted for that value, never for a number.
    /// </summary>
    public static readonly JobHistoryFixture ContinuedAsNew = new(
        ContinuedAsNewFile,
        TimeSpan.FromSeconds(1),
        2);

    /// <summary>The run that followed the continuation, ended by a notification of a ready upload.</summary>
    public static readonly JobHistoryFixture ContinuedAfterANotification = new(
        ContinuedAfterANotificationFile,
        TimeSpan.FromSeconds(2),
        2);

    /// <summary>Every committed recording, in the order the cases name them.</summary>
    public static readonly JobHistoryFixture[] All =
    [
        Ready,
        Rejected,
        Verifying,
        WaitingThenReadyAfterANotification,
        WaitingThenRejected,
        WaitingThenExpired,
        ContinuedAsNew,
        ContinuedAfterANotification,
    ];

    /// <summary>The recordings of a job whose first check already answered: no wait was taken.</summary>
    public static readonly JobHistoryFixture[] OfAJobThatDidNotWait = [Ready, Rejected];

    /// <summary>The recordings that carry the wait of a job for its upload.</summary>
    public static readonly JobHistoryFixture[] OfAJobThatWaited =
    [
        Verifying,
        WaitingThenReadyAfterANotification,
        WaitingThenRejected,
        WaitingThenExpired,
        ContinuedAsNew,
        ContinuedAfterANotification,
    ];

    /// <summary>The recordings of a run that left the wait with its upload's outcome.</summary>
    public static readonly JobHistoryFixture[] OfARunThatLeftTheWait =
    [
        Verifying,
        WaitingThenReadyAfterANotification,
        WaitingThenRejected,
        WaitingThenExpired,
        ContinuedAfterANotification,
    ];

    /// <summary>The recording committed under <paramref name="file"/>.</summary>
    /// <param name="file">The name the suite reads the recording by.</param>
    /// <exception cref="ArgumentOutOfRangeException">No recording is committed under that name.</exception>
    public static JobHistoryFixture ByFile(string file) =>
        All.FirstOrDefault(fixture => string.Equals(fixture.File, file, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(
            nameof(file),
            file,
            "no job history is committed under that name.");
}
