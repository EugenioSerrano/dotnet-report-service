# Report job operations

A consumer hands the service the reference of the data it wants reported, the
template it wants it rendered with and the formats it wants out, and receives a
job it can follow. This page describes the creation operation, the answer, every
refusal it gives, and what the service guarantees about the connection and about
the work that follows.

The job's published files are listed and downloaded through the two operations
described below, after the status read that follows them.

## Follow a job

```http
GET /api/v1/report-jobs/job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a
X-Consumer-Id: dev-consumer
```

```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store

{
  "reportJobId": "job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a",
  "status": "running",
  "stage": "rendering-pdf",
  "progress": {
    "recordsRead": 1000,
    "recordsTotal": 1000,
    "blocksCompleted": 0,
    "blocksTotal": 1,
    "pagesGenerated": 0
  },
  "outputs": [
    { "format": "pdf", "status": "running", "artifactId": null, "errorCode": null }
  ],
  "errorCode": null,
  "createdAt": "2026-11-01T12:03:17.123Z",
  "startedAt": "2026-11-01T12:03:18.004Z",
  "finishedAt": null
}
```

Every member is always present; a value that does not exist yet is written as
`null`, so a client branches on `null` and never on a missing member.

| Member | Meaning |
|--------|---------|
| `reportJobId` | The job's identifier: the one its creation answered with. |
| `status` | Where the job is: `queued`, `validating`, `partitioning`, `running`, `finalizing`, `succeeded`, `failed`, `cancelling` or `cancelled`. |
| `stage` | The step the job is in while it runs: `waiting-for-input`, `resolving`, `validating-data`, `partitioning`, `composing-html`, `rendering-pdf`, `merging-pdf`, `exporting-xlsx`, `converting-markdown`, `publishing` or `notifying`. It is `null` while the job is queued, while no step has written one, and for a job that has ended — a finished job is in no step. |
| `progress.recordsRead` | How many records of your document the job has read. It only rises: a repeated attempt, a retry or a service restart never lowers it. |
| `progress.recordsTotal` | How many records the document holds, `null` until the job has counted them. **It is never a guess**: while it is `null`, no share of the work can be derived. A total of `0` is a counted, empty report — not an unknown one. |
| `progress.blocksCompleted` | How many blocks of the report are finished. Only rises. |
| `progress.blocksTotal` | How many blocks the job was divided into, `null` until that is known. |
| `progress.pagesGenerated` | How many pages are finished. Only rises. |
| `outputs[]` | One entry per requested output, in the fixed order `pdf`, `html`, `xlsx`, `markdown`. `format` is the requested format, `status` is `pending`, `running`, `succeeded`, `failed` or `cancelled`, `artifactId` identifies the published file once it exists, and `errorCode` names why the output failed. |
| `errorCode` | The stable code of the condition that ended a failed job; `null` otherwise. |
| `createdAt`, `startedAt`, `finishedAt` | When the job was accepted, when it entered its first step and when it ended, in UTC as RFC 3339 with `Z` and three fractional digits; `null` until each happens. |

**There is no percentage.** The service never presents a share of a total it has
not counted; a client that knows both numbers may compute one itself.

**The counters only rise, and a total once known never changes.** The state and
the stage never move back either. A job that ends `failed` carries the stable
code of the condition on itself, and its PDF output carries the same code; the
answer itself still succeeds with `200`.

Refusals of this read are the shared ones:

| Case | HTTP | `code` |
|------|------|--------|
| No, malformed or unregistered consumer identity | 403 | `CONSUMER_UNKNOWN` |
| The job does not exist, belongs to another consumer, or its identifier is malformed | 404 | `INPUT_NOT_FOUND` |
| The service cannot reach its own database | 503 | `SERVICE_UNAVAILABLE` |

The three not-found cases answer the identical document, apart from its trace
identifier: a malformed identifier is refused without a database read, and a
job of another consumer is indistinguishable from one that does not exist.

Every answer is `Cache-Control: no-store`: an intermediary serving an older
answer would show counters going back. Poll at about one request per second
while the job runs; at that rate a poll costs one indexed read of the service's
own database. An inactive consumer may read its own jobs.

## List a job's files

```http
GET /api/v1/report-jobs/job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a/artifacts
X-Consumer-Id: dev-consumer
```

```http
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store

{
  "reportJobId": "job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a",
  "artifacts": [
    {
      "artifactId": "artifact-9a1c4e0b7d2f4a3c8e6b5d4c3b2a1f0e",
      "format": "pdf",
      "mimeType": "application/pdf",
      "sizeBytes": 38211,
      "sha256": "71ab0c9f4d2e8b6a1f3c5e7d9b0a2c4e6f8d1b3a5c7e9f0b2d4a6c8e1f3b5d7a",
      "expiresAt": "2026-11-08T12:03:41.512Z"
    }
  ]
}
```

Every member is always present. The list is empty — `"artifacts": []` — while the
job has published nothing, and exactly one entry appears once its PDF exists.

| Member | Meaning |
|--------|---------|
| `reportJobId` | The job the files belong to. |
| `artifacts[].artifactId` | The identifier the download names. |
| `artifacts[].format` | The form the file was produced in: `pdf`, `html`, `xlsx` or `markdown`. |
| `artifacts[].mimeType` | The media type the download answers with. |
| `artifacts[].sizeBytes` | The size of the stored file, which is the length of a complete download. |
| `artifacts[].sha256` | The SHA-256 of the stored bytes, lower-case hexadecimal: the hash the received bytes must match. |
| `artifacts[].expiresAt` | When the file stops being downloadable, in UTC as RFC 3339 with `Z` and three fractional digits. |

An artifact whose `expiresAt` has passed **stays in the list** with its past
instant: expiry is not a status, and the answer to a download tells the caller
that the file expired. The order is fixed — by format, then by identifier — and
an answer is never served from a cache.

## Download a file

```http
GET /api/v1/report-jobs/job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a/artifacts/artifact-9a1c4e0b7d2f4a3c8e6b5d4c3b2a1f0e/download
X-Consumer-Id: dev-consumer
```

```http
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Length: 38211
Content-Disposition: attachment; filename="job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a.pdf"
Cache-Control: no-store
X-Content-Type-Options: nosniff

... 38211 bytes ...
```

The body is the stored file, streamed as it is read and **verified to its end**
against the size and the hash the listing carries. The file name is built from
the job's identifier — a caller never names a file — and no `ETag` is offered and
no range is served: a partial read would be unverified.

**A transfer that ends short is a failure, not a file.** The declared
`Content-Length` is the whole file, and the last bytes are released only once the
stored object matched its size and its SHA-256. When the stored bytes no longer
match — a changed, a missing or an extra byte — the response is aborted and the
client receives fewer bytes than announced: retry, and never use the bytes of an
incomplete transfer.

| Case | HTTP | `code` | `title` |
|------|------|--------|---------|
| The job or the file does not exist, belongs to another consumer, or either identifier is malformed | 404 | `INPUT_NOT_FOUND` | `Artifact not found` |
| The file's expiry has passed | 410 | `ARTIFACT_EXPIRED` | `Artifact expired` |
| No, malformed or unregistered consumer identity | 403 | `CONSUMER_UNKNOWN` | `Unknown consumer` |
| The stored object is missing or unreadable while the file has not expired | 500 | `SERVICE_UNAVAILABLE` | `Service unavailable` |
| A storage cause worth retrying | 503 | `SERVICE_UNAVAILABLE` | `Service unavailable` |

The expiry is decided from the service's own records, before the stored object is
read at all: an expired file is answered as expired even if its bytes are still
there, the job stays `succeeded`, and nothing is written. The not-found answer is
one answer for every unusable reference — an absent job, another consumer's job,
an absent file and a file of another job are indistinguishable — and it differs
from the expired one in its status and its code. An inactive consumer may list
and download the files of the jobs it already owns.

```http
POST /api/v1/report-jobs
X-Consumer-Id: dev-consumer
Content-Type: application/json

{
  "template": { "id": "pilot-grouped", "version": "1.0.0" },
  "data": { "uploadId": "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f", "profile": "header-items-v1" },
  "outputs": ["pdf"]
}
```

```http
HTTP/1.1 202 Accepted
Location: /api/v1/report-jobs/job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a
Content-Type: application/json

{
  "reportJobId": "job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a",
  "status": "queued",
  "statusUrl": "/api/v1/report-jobs/job-8c1d0f7a6b5e4d3c2b1a0f9e8d7c6b5a"
}
```

| Member | Rule |
|--------|------|
| `template.id` | Required string, 1 to 128 characters: a lower-case identifier (`a-z`, `0-9`, `.`, `_`, `-`, beginning and ending with a letter or a digit). It becomes part of a storage key and the folder the template is read from, so a value outside the rule can never name a template: the request is refused with `404 TEMPLATE_NOT_FOUND` before anything is read. |
| `template.version` | Optional. When present it must be a semantic version (`MAJOR.MINOR.PATCH`, with an optional pre-release and build metadata, at most 128 characters): a range (`^1.0.0`, `~1.2`, `1.x`), a branch name (`main`) and a partial version (`1.0`, `v1.0.0`) are refused with `404 TEMPLATE_NOT_FOUND`, because no folder can carry one. When absent the job names the template only and the version is chosen for it. |

A version **already taken** that belongs to another consumer and is not `shared` is
refused with the same `404 TEMPLATE_NOT_FOUND`, exactly as if it did not exist: the
answer never says that someone else holds it. A version that was never taken, or one
you may use, is accepted, and the take step decides whether it exists.

The checks run in this order: the consumer, the body, the requested format and
profile, the reference's syntax, the data reference, and the versions already taken.

A reference by name is never refused at acceptance. The highest **release** version
already taken that this consumer may use is recorded with the job as the version it
falls back to if the template source cannot be checked; a pre-release is never chosen
by name, though a pre-release named exactly is used if the take accepts it.
| `data.uploadId` | Required string. The upload must be yours, confirmed and not past its deadline; anything else is answered as an upload that does not exist. |
| `data.profile` | Required string. Only `header-items-v1` is produced. |
| `outputs` | Required, non-empty array of distinct strings. Only `pdf` is produced. |

`render`, `metadata` and every other member are **not accepted** yet: a request
carrying one is refused as malformed rather than having it ignored, because
silently dropping a requested culture or time zone would change the report.

The `Idempotency-Key` header is **not honoured yet**: it is accepted and ignored,
and a request repeated with the same key creates a second job. Every body is at
most **16 384 bytes** and must be sent as `application/json`.

## What the answer means

`202 Accepted` is given only after the job, its requested output, the template
reference exactly as the request wrote it and the request to start the job have
been written together, in one transaction, in the service's own database. A job
that was answered is therefore durable: it is not lost because the connection
broke, because the client timed out or because the process that answered
restarted.

Once accepted, a job is started in the background **exactly once**, by the
orchestration worker, even across restarts of the API and of the worker. A job
whose data reference cannot be used any more ends failed with the condition that
made it unusable; until the steps that print a report are part of the deployment,
an accepted job whose data is ready stays `queued` after its execution has
checked the data.

A job may also be created on an upload that is **still being verified**. It is
accepted the same way — `202`, `status: "queued"`, its `reportJobId` and its
status URL — and its stored `stage` then reports `WAITING_FOR_INPUT` while it
waits. The stage appears within a second of the answer (the request to start the
job is claimed by the worker on its next poll), so a read taken immediately after
the `202` may see `queued` with no stage at all. While it waits, nothing is
rendered, validated or published: the job holds no page and no artifact.

The wait ends in one of three ways, and the job reports which:

- the upload becomes ready → the wait ends, the stage is cleared, and the job
  continues at the step that takes its data — in a deployment without the steps
  that print, it stays `queued`;
- the upload is rejected → the job and its output end `failed` with the upload's
  **own** code (for example `DATA_INVALID`), the job's reason is
  `input-rejected`, and no page is rendered and nothing is published;
- the upload's verification deadline passes → the upload ends `expired` and the
  job and its output end `failed` with `INPUT_NOT_FOUND`, reason
  `input-expired`.

The wait is bounded by the upload's own verification deadline and never by the
time a consumer had to send its data: an upload whose session window has closed
while it is still verifying is still verifying, so its jobs keep waiting until the
verification deadline. A verification tells the jobs waiting on its upload that
the state changed; a job also re-checks on its own timer, so a lost notification
costs at most half a minute.

## Refusals

Every refusal is a Problem Details document with a stable `code`; the same code
always means the same condition. The checks run in this order: the consumer, the
body, the requested format and profile, the reference's syntax, the versions
already taken, and finally the data reference itself inside the transaction that
accepts the job.

| Case | HTTP | `code` |
|------|------|--------|
| No, malformed or unregistered consumer identity | 403 | `CONSUMER_UNKNOWN` |
| An inactive consumer creating a job | 403 | `CONSUMER_INACTIVE` |
| A format other than `pdf`, a profile other than `header-items-v1` | 422 | `UNSUPPORTED_CAPABILITY` |
| A body over 16 384 bytes | 413 | `QUOTA_EXCEEDED` |
| A malformed body, an unknown member, a value out of its rule | 400 | `REQUEST_INVALID` |
| A template identifier outside the rule, a version that is not a semantic version, or a version of another consumer's that is not `shared` | 404 | `TEMPLATE_NOT_FOUND` |
| An upload that does not exist for you, is not confirmed, was refused, or ran out of time | 404 | `INPUT_NOT_FOUND` |
| The service cannot reach its own database | 503 | `SERVICE_UNAVAILABLE` |

A refusal never creates a job, and an unusable data reference is answered the
same way whatever makes it unusable — an upload that is not yours is
indistinguishable from one that does not exist. The template answer carries one
constant problem for every reference it refuses and never repeats the value that
was sent.

## What happens to the template after the `202`

The job's template is fixed once, right after the request is accepted, and never
changes again: not on a retry, not when a worker restarts, and not when a newer
version is taken.

- A version **already taken** and usable is bound with one read of the service's own
  records, and the template source is not read at all — a job naming such a version
  runs even while the source is unreachable.
- A version that is **not taken** is taken by the PDF worker from the deployment's
  template source — the object store, read through the publication record the version
  was published with — and the version it read is recorded on the job with the
  publication descriptor's hash, the verified file list, the package hash, the engine,
  the engine's version, the schema the records are checked against and the instant it
  was resolved.
- A reference **by name** resolves the highest release version the source holds. If the
  source cannot be checked, the job uses the release version recorded when it was
  accepted and logs that the source could not be checked; a by-name job accepted while
  no release version had been taken fails with `TEMPLATE_SOURCE_UNAVAILABLE`.

A template the take refuses fails the job and its output after the `202`, with the
code and the message a consumer needs:

| Code | What it means |
|------|---------------|
| `TEMPLATE_NOT_FOUND` | The template or its version does not exist, or it is another consumer's and is not `shared` — the two answers are identical. |
| `TEMPLATE_INVALID` | The template cannot be used as it stands; the message names what its author has to fix. |
| `TEMPLATE_SOURCE_UNAVAILABLE` | The source could not be read. Nothing was taken; the request may be submitted again later. |
| `UNSUPPORTED_CAPABILITY` | The template's engine is not one this deployment renders. |

## Logs and instruments

An acceptance logs the job and the upload identifier with the form of the
template reference (`exact` or `by-name`), and a refusal logs its code and a
reason constant. A job that ends logs its status, the code that ended it and the
reason. Every log line and span of the job's own work carries the job identifier;
the identifier is never a metric tag.

| Instrument | Kind | Tags |
|------------|------|------|
| `reportservice.job.acceptances` | counter | `result`, `code` |
| `reportservice.job.outcomes` | counter | `status`, `code` |
| `reportservice.intent.dispatch.attempts` | counter | `kind`, `result` |
