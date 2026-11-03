# Upload verification

Verifying an upload is the step that turns received bytes into an immutable
snapshot — or ends the upload rejected with a stable reason. It runs in the
orchestration worker, never inside an HTTP request, and it writes nothing until
the whole content has been read and measured.

## What one verification does

1. It loads the upload and refuses to act unless the upload is waiting for
   verification. An upload that is already ready reports the snapshot that
   exists; one that ended rejected reports the reason recorded on it; any other
   state is left untouched.
2. It refuses before reading anything when the body ended early, when the
   declared encoding is one the service does not decode, or when the stored size
   is past the stored-size limit.
3. It reads the stored object **once**, forward only, and never seeks. The same
   pull of the storage port:

   - counts and hashes the stored bytes, and keeps their last eight bytes;
   - hands the blocks to the decompressor when the content is compressed, which
     counts and hashes the content and checksums it.

   The storage port writes a copy of exactly those bytes under a key that belongs
   to this attempt, so the object recorded in a snapshot is the object that was
   measured, not a second read of something that may have changed.
4. It decides the outcome only after both sides of that read have finished:

   | What happened | Outcome |
   |---------------|---------|
   | The stored object is no longer the size or hash recorded when it arrived, or it is gone | `DATA_INVALID` / `modified` |
   | The decompressor refuses the content it was given | `DATA_INVALID` / `malformed` |
   | The compressed object is shorter than twenty bytes, or its own trailer does not agree with the content that was read | `DATA_INVALID` / `truncated` |
   | The content decompresses past the decompressed-size limit | `QUOTA_EXCEEDED` / `input-bytes` |
   | The declared hash differs from the content's hash | `DATA_INVALID` / `sha256-mismatch` |
   | None of the above | `ready`, with a snapshot record |

5. It commits the outcome with a conditional update: a snapshot record plus the
   move to ready in one transaction, or the move to rejected. An upload that
   stopped waiting in the meantime keeps whatever the other writer left, and this
   attempt's object is removed. The upload's staged object is removed once the
   upload has ended.

## The jobs waiting on an upload

A report job may be created on an upload that is still being verified. When a
verification ends — ready or rejected, and also when it found the upload gone or
no longer waiting — it tells the jobs of that upload that its state changed: one
small step reads the queued jobs of the upload that carry the waiting stage or no
stage yet, in the order they were accepted, and nudges each of them. The nudge
carries nothing: a job re-reads its upload and decides from what it reads, so a
late, repeated or stale nudge cannot change an outcome by itself.

The nudge is a courtesy, never the guarantee. A job also re-checks its upload on a
durable timer of its own (`Jobs__InputWait__Backstop`, thirty seconds by default),
so a nudge that is lost, late or impossible costs at most one interval. A nudge
addressed to a job whose execution does not exist yet, or that has already
finished, is logged and skipped; the other jobs of the upload are still nudged.
The step gives up after three attempts and the verification's own answer is never
changed by it: an upload is never turned into a failed or an expired one because a
nudge could not be delivered.

What ends a wait: the upload becomes ready, the upload is rejected (the job ends
with the upload's own code), or the upload's verification deadline passes and the
upload expires (the job ends with `INPUT_NOT_FOUND`). An upload whose session
window has closed while it is still verifying is still verifying, so its jobs keep
waiting until the verification deadline — the deadline is what bounds this state,
never the session.

A wait that outlasts the checks one run of the job's workflow may take
(`Jobs__InputWait__ContinueAsNewAfter`) continues as a new run of the same
execution, so no run's history grows without limit.

## Compressed content: exactly one unit of content

`Content-Encoding: gzip` declares **exactly one** gzip member — one complete unit
of compressed content. A body that is a concatenation of members, or a
compressed body of zero bytes, is refused with `DATA_INVALID` / `truncated`. The
one exception is a chain whose earlier members hold no content at all: the last
member's trailer then attests everything that was read, and the content that is
fixed is exactly that member's own content.

A compressed body whose content is intact but whose own trailer is missing or
cut is refused the same way, **even when the HTTP transfer completed and the
declared hash matches**: the object does not end with a trailer that agrees with
what was measured, so it is not one complete unit of content.

The completeness decision is a comparison of the object's last eight bytes with
what was measured over the content — two 32-bit values, one of them the content's
length modulo 2^32. It is a very strong check, not a mathematical proof: a
producer would have to craft a tail equal to the checksum and length of exactly
the content that is fixed, which grants nothing it did not already have.

## Limits and settings

| Setting | Meaning | Shipped value |
|---------|---------|---------------|
| `Uploads__MaxStoredBytes` | The most stored bytes one upload may have. Enforced while the body arrives and again before verification reads anything. Required, greater than zero. | `4294967296` (4 GiB) |
| `Uploads__MaxUncompressedBytes` | The most content bytes a body may decompress to. Counted as the bytes leave the decompressor, so content that expands far past its stored size is stopped at the limit. Required, greater than zero. | `4294967296` (4 GiB) |

A value that is missing or outside its rule stops the worker at start, naming the
setting and the rule and never the value.

## Instruments

The worker records, on the product meter:

| Instrument | Kind | Tags |
|------------|------|------|
| `reportservice.upload.outcomes` | counter | `outcome`, `code`, `stage` |
| `reportservice.upload.verification.duration` | histogram, seconds | `outcome` |

Every tag is a value from a fixed set. No identifier, key or size is ever a tag.

## Memory

The stored side may run at most one megabyte ahead of the decompressor, and
resumes when it has caught up to half a megabyte, so verification of a body
larger than the process's memory is bounded by that window and by the copy
buffer — never by the body's size.

## Dispatching the verification

The API never talks to the workflow engine. A confirmation records a durable
request in the metadata database in the same transaction that moves the upload to
`verifying`; the orchestration worker's dispatcher turns it into one execution of
the verification workflow.

Each request carries a **kind**, and the kind names the workflow it is turned
into: `start-upload-verification` starts one verification of an upload, and
`start-job` starts the work of one accepted report job. A process that does not
serve a kind never dispatches it and never fails it.

- Each poll claims a bounded batch of the requests that are due, skipping rows
  another process holds, and keeps them in one transaction while it works.
- For each request it asks the engine whether the execution already exists before
  starting one, so a process that stopped between a start and its record — or a
  second worker polling beside the first — still produces exactly one execution
  per upload or per job.
- A request is never failed. A failed attempt is recorded with its wait and tried
  again, with the wait doubling from two seconds up to one minute. The claim
  transaction is bounded in time, so a slow engine can never hold a row beyond
  the deployment's own limit; whatever was not worked through is left for the
  next poll.
- A request pending longer than `Dispatcher__AlertAfter` is reported once, with
  its identifier and its kind.

## Settings

| Setting | Meaning |
|---------|---------|
| `Uploads__MaxStoredBytes` | The most stored bytes one upload may hold; the same value the API enforces. |
| `Uploads__MaxUncompressedBytes` | The most data bytes one upload may decompress to. |
| `Uploads__Verification__StartToClose` | How long one verification attempt may run. `01:00:00` by default, at most one day. |
| `Dispatcher__PollInterval` | How long the dispatcher waits between two polls. `00:00:01` by default, between 100 ms and one minute. |
| `Dispatcher__BatchSize` | The most requests one poll claims. `50` by default, 1 to 500. |
| `Dispatcher__AlertAfter` | How long a request may wait before it is reported. `00:05:00` by default. |
| `Dispatcher__ClaimBudget` | How long one poll works through its batch before leaving the rest. `00:00:30` by default, 5 to 50 seconds. |
| `Workers__DataActivitySlots` | How many verification attempts one worker runs at the same time. `2` by default, 1 to 64. |

Every setting is required and validated at start: an out-of-range value stops the
process, naming the setting and the rule and never the value.

## Instruments

| Instrument | Kind | Tags |
|------------|------|------|
| `reportservice.upload.outcomes` | counter | `outcome`, `code`, `stage` |
| `reportservice.upload.verification.duration` | histogram | `outcome` |
| `reportservice.intent.dispatch.attempts` | counter | `kind`, `result` |
| `reportservice.intent.pending.age` | observable gauge (s) | `kind` |
| `reportservice.job.outcomes` | counter | `status`, `code` |
| `reportservice.job.input.wait.outcomes` | counter | `outcome` |
| `reportservice.job.input.wait.passes` | histogram | none |
| `reportservice.job.input.wait.notifications` | counter | `result` |

Tags are fixed values only: an upload's identifier, a job's identifier, a key or a
size is never a tag. Identifiers appear on logs and on spans.

## The deadline and how to size it

A confirmed upload carries a verification deadline of its own, recorded in the
same transaction as the confirmation and counted from it:
`completedAt + Uploads__VerificationDeadline`. It is what an upload ends by once
it is `verifying`; the session's own expiry no longer applies to it.

The deadline has to cover the verification's retry schedule, or a healthy attempt
would be cut off by its own upload's deadline:

- `Uploads__Verification__StartToClose` bounds one attempt (one hour by
  default), and a verification is given five attempts with a wait that never
  exceeds a minute between them, so the whole retry window fits inside the
  shipped six hours;
- the same value is given to the workflow as the activity's
  `ScheduleToCloseTimeout`, so the engine stops scheduling attempts when the
  deadline passes instead of after it;
- the worker refuses to start when `Uploads__VerificationDeadline` is shorter than
  `Uploads__Verification__StartToClose`, naming both settings and no value.

A verification attempt that meets an expired upload writes nothing, and an
attempt whose commit is refused because the deadline passed deletes both its own
object and the upload's staging object. No snapshot record and no snapshot object
exist for an upload that expired.

## The expiry sweep

The orchestration worker runs a sweep that makes the stored state catch up
without traffic:

- every `Uploads__Expiry__SweepInterval` it expires the uploads that are past the
  end of their session or past their verification deadline, in batches of
  `Uploads__Expiry__BatchSize`, one round after another until a round finds
  nothing left;
- for each upload it expires it deletes the staging object, writes a log line
  naming the upload and the cause (`session` or `verification-deadline`) and
  counts the outcome;
- its update skips the rows another replica or the API is holding, so several
  processes may expire at once and an upload is expired exactly once;
- a database or a storage failure is logged with its type and the sweep waits for
  its next round: it never ends the process and never blocks an upload from being
  expired later.

| Setting | Meaning |
|---------|---------|
| `Uploads__VerificationDeadline` | How long after its confirmation a confirmed upload may still fix a snapshot. `06:00:00` by default, longer than zero, at most seven days and never shorter than `Uploads__Verification__StartToClose`. |
| `Uploads__Expiry__SweepInterval` | How long the worker waits between two sweep rounds. `00:01:00` by default, between one second and one hour. |
| `Uploads__Expiry__BatchSize` | The most uploads one round expires. `500` by default, 1 to 5000. |
