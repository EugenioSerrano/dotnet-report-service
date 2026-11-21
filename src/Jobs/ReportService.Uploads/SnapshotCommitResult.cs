namespace ReportService.Uploads;

/// <summary>What an attempt to commit a snapshot did.</summary>
public enum SnapshotCommitResult
{
    /// <summary>The snapshot record was inserted and the upload became ready.</summary>
    Committed = 0,

    /// <summary>An earlier attempt had already committed a snapshot for this upload; nothing changed.</summary>
    AlreadyCommitted = 1,

    /// <summary>The upload is no longer verifying; nothing was written.</summary>
    NotVerifying = 2,
}
