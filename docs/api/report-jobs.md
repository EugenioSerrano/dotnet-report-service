# Report job operations

A consumer hands the service the reference of the data it wants reported, the
template it wants it rendered with and the formats it wants out, and receives a
job it can follow. This page describes the creation operation, the answer, every
refusal it gives, and what the service guarantees about the connection and about
the work that follows.

The job's status read, its outputs and their download are not offered yet; the
status URL answers as not found until they are.

## Create a job

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
| `template.id` | Required string, 1 to 128 characters: a lower-case identifier (`a-z`, `0-9`, `.`, `_`, `-`, beginning and ending with a letter or a digit). It becomes part of a storage key, so anything else is refused as a malformed request. The identifier must name a template this service can take: a name it cannot find is answered as a missing template once the reference is resolved. **The form of this answer is still provisional** and may change when the template reference is resolved at acceptance. |
| `template.version` | Optional string of 1 to 128 printable ASCII characters without spaces. When present the job names exactly that version; when absent it names the template by name and takes the latest valid version. |
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

## Refusals

Every refusal is a Problem Details document with a stable `code`; the same code
always means the same condition. The checks run in this order: the consumer, the
body, the requested format and profile, the reference's form, and finally the data
reference itself inside the transaction that accepts the job.

| Case | HTTP | `code` |
|------|------|--------|
| No, malformed or unregistered consumer identity | 403 | `CONSUMER_UNKNOWN` |
| An inactive consumer creating a job | 403 | `CONSUMER_INACTIVE` |
| A format other than `pdf`, a profile other than `header-items-v1` | 422 | `UNSUPPORTED_CAPABILITY` |
| A body over 16 384 bytes | 413 | `QUOTA_EXCEEDED` |
| A malformed body, an unknown member, a value out of its rule | 400 | `REQUEST_INVALID` |
| An upload that does not exist for you, is not confirmed, was refused, or ran out of time | 404 | `INPUT_NOT_FOUND` |
| The service cannot reach its own database | 503 | `SERVICE_UNAVAILABLE` |

A refusal never creates a job, and an unusable data reference is answered the
same way whatever makes it unusable — an upload that is not yours is
indistinguishable from one that does not exist.

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
