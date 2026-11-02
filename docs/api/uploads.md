# Upload operations

The first operations of the public API let a consumer system open an **upload
session** for its report data and read that session's status. A session is the
only way data reaches the service: nothing is sent inside a job request. This
page describes the declared consumer identity, both operations, the states a
session reports and every error the operations return.

The rest of the upload path — streaming the JSON, confirming it, and verifying
it into an immutable snapshot — is described in the [README](../README.md) and
is not part of these operations.

## The declared consumer identity

Every request carries the consumer it is made for in the **`X-Consumer-Id`**
header:

```http
X-Consumer-Id: dev-consumer
```

- The header must be present **exactly once**, carry **one** value and match
  `[A-Za-z0-9._-]{1,128}`. Comparison is ordinal: the value is used exactly as
  sent.
- The identity is **declared, not authenticated**. The service trusts it. Put
  the API behind your own gateway, network isolation or authentication layer and
  let that layer set or overwrite the header; called directly, anyone who reaches
  the API can act as any consumer.
- An upload belongs to the consumer that declared it. Every read is scoped to
  that consumer, and another consumer's upload is answered exactly like one that
  does not exist.

## Open a session

```http
POST /api/v1/uploads
X-Consumer-Id: dev-consumer
Content-Type: application/json

{ "profile": "header-items-v1", "mode": "API_STREAM" }
```

```http
HTTP/1.1 201 Created
Location: /api/v1/uploads/upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f
Content-Type: application/json

{
  "uploadId": "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f",
  "status": "created",
  "mode": "API_STREAM",
  "profile": "header-items-v1",
  "createdAt": "2026-11-01T04:39:23.123Z",
  "expiresAt": "2026-11-02T04:39:23.123Z"
}
```

The body is small and strict: one JSON object with exactly the two members
`profile` and `mode`, each once, each a string, at most **16 384 bytes**, sent
as `application/json`. Anything else is answered as described under
[Errors](#errors).

- `profile` must be `header-items-v1`; it is the only data profile the service
  admits in this version.
- `mode` must be `API_STREAM`. Direct-to-storage uploads (`DIRECT_STORAGE`) are
  not offered, whatever the storage provider supports.
- The session is recorded before the response is sent. A refused request creates
  nothing.
- `uploadId` is 122 random bits after the prefix `upload-`; it is not guessable
  and not ordered by time.
- `createdAt` and `expiresAt` are UTC with millisecond precision, so every later
  read shows the same values.

## Read a session's status

```http
GET /api/v1/uploads/upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f
X-Consumer-Id: dev-consumer
```

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "uploadId": "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f",
  "status": "created",
  "mode": "API_STREAM",
  "profile": "header-items-v1",
  "createdAt": "2026-11-01T04:39:23.123Z",
  "expiresAt": "2026-11-02T04:39:23.123Z"
}
```

An identifier that cannot exist — malformed, absent or belonging to another
consumer — is answered as `404` with `INPUT_NOT_FOUND`, never as a malformed
request, so the answer says nothing about which identifiers exist.

## Session states

| `status` | Meaning |
|----------|---------|
| `created` | The session is open and waiting for data. This is the state these operations produce. |
| `uploading` | Content is being received. |
| `verifying` | The content was confirmed and is being verified. |
| `ready` | Verification finished and the snapshot is fixed. |
| `rejected` | The content was refused and no snapshot exists for it. |
| `expired` | The session passed `expiresAt`. |

## Errors

Every error is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem
document with the media type `application/problem+json`, a stable `code` and the
trace identifier of the request:

```http
HTTP/1.1 422 Unprocessable Entity
Content-Type: application/problem+json

{
  "type": "about:blank",
  "title": "Unsupported capability",
  "status": 422,
  "detail": "The requested upload mode is not supported. Supported modes: API_STREAM.",
  "code": "UNSUPPORTED_CAPABILITY",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

| `code` | HTTP | `title` | When |
|--------|------|---------|------|
| `CONSUMER_UNKNOWN` | 403 | `Unknown consumer` | The request declares no usable consumer identity: the header is absent, repeated, empty or outside the grammar. |
| `UNSUPPORTED_CAPABILITY` | 422 | `Unsupported capability` | The request asks for a mode or a data profile this version does not offer. Both are named in one answer when both are unsupported, mode first. |
| `QUOTA_EXCEEDED` | 413 | `Request too large` | The creation body is larger than 16 384 bytes. |
| `REQUEST_INVALID` | 400 | `Invalid request` | The creation request is malformed; see below. |
| `INPUT_NOT_FOUND` | 404 | `Upload not found` | The upload does not exist for the declared consumer, or the identifier cannot exist. |
| `SERVICE_UNAVAILABLE` | 503 | `Service unavailable` | The service could not complete the request for a cause on its side, such as an unreachable database. Retry it later. |
| `SERVICE_UNAVAILABLE` | 500 | `Service unavailable` | An unexpected fault. Retry it later; the operator finds the request by its `traceId`. |

A `detail` is a constant of the service. It never carries a value the caller
sent, an exception message, a statement, a connection detail or a stack trace,
with one exception described below: the name of a field the operation itself
declares.

### A malformed creation request

`REQUEST_INVALID` answers a body that is not a small, valid document of the
operation. The answer carries two extension members — `field`, the path of the
field concerned (or `null`), and `reason`, a constant — and, for an unknown
member, `unknownFieldCount`. When a body has several faults, exactly one is
reported, always the same one:

| `reason` | `field` | What it says |
|----------|---------|--------------|
| `media-type` | `null` | The body was not sent as `application/json`. |
| `malformed-json` | `null` | The body is not one JSON object, is truncated, or carries more after it. |
| `unknown-field` | `null` | The body carries members this operation does not know; `unknownFieldCount` counts them. |
| `duplicate-field` | the member | A member appears more than once. |
| `missing-field` | the member | A required member is absent. |
| `null-field` | the member | A member is present and null. |
| `wrong-type` | the member | A member is not a string. |

The precedence is: media type, then the body as one JSON object, then unknown
members, then a repeated member, then each member in the operation's own order
(`profile`, then `mode`) as missing, null or of the wrong type. A member this
operation does not know is only counted, never named, because its name is
caller text like any other value:

```json
{
  "type": "about:blank",
  "title": "Invalid request",
  "status": 400,
  "detail": "The request contains 1 field that is not part of this operation.",
  "code": "REQUEST_INVALID",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "field": null,
  "reason": "unknown-field",
  "unknownFieldCount": 1
}
```

## Configuration

| Setting | Meaning |
|---------|---------|
| `Uploads__SessionLifetime` | How long a new session accepts data. Required, longer than zero and at most seven days; the shipped value is `1.00:00:00` (24 hours). A value outside that range stops the service at start, naming the setting and never the value. |

## Streaming the content

`PUT /api/v1/uploads/{uploadId}/content` puts the consumer's report data into
its upload. The body is streamed once, counted as it arrives and never held.

```http
PUT /api/v1/uploads/dev-upload/content
X-Consumer-Id: dev-consumer
Content-Type: application/json
Content-Encoding: gzip

<the report data, plain or one complete gzip member>
```

- **One body per upload.** The content is written once; a second body is refused
  with `409 UPLOAD_STATE_CONFLICT` and the upload keeps the bytes it has.
- **`Content-Type`** must be `application/json` (parameters such as
  `charset=utf-8` are accepted). Anything else is refused with
  `400 REQUEST_INVALID` and `reason: "media-type"`.
- **`Content-Encoding`** declares how the body is encoded: absent or `identity`
  means plain, and `gzip` declares **exactly one** gzip member. Any other value
  (another token, a list, an empty value) is recorded and refused later, when the
  upload is confirmed, with `UNSUPPORTED_CAPABILITY` and `reason: "encoding"`.
- **The size limit** is `Uploads__MaxStoredBytes` (4 GiB by default). A declared
  length above it is refused without reading the body; a body that crosses it
  while arriving is stopped where it is. Either way the answer is
  `413 QUOTA_EXCEEDED`, the connection closes with it, and the upload ends
  `rejected` with `QUOTA_EXCEEDED` / `stored-bytes`. A body of exactly the limit
  is accepted.
- **A body that ends early** — the connection fails, or fewer bytes arrive than
  the length that was declared — is recorded as interrupted and answered
  `400 REQUEST_INVALID` with `reason: "body-incomplete"` when the answer can still
  be written. The upload stays confirmable and its verification refuses it.
- The answer is `200 OK` with the status projection, showing
  `status: "uploading"` and the bytes received.

## Confirming the upload

`POST /api/v1/uploads/{uploadId}/complete` confirms the upload. The body is
optional and may declare the hash the consumer expects of its data:

```json
{ "sha256": "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3" }
```

- The declared hash travels **lower- or upper-case** and is compared with the
  SHA-256 of the **uncompressed** data, so it is the same declaration whether the
  body was sent plain or compressed.
- The document is strict: `application/json`, one object, the member `sha256` at
  most once, 64 hexadecimal characters. Anything else is refused with
  `400 REQUEST_INVALID` and the `reason`, `field` and `detail` of the contract,
  and nothing changes. An empty object declares nothing, which is valid.
- The answer is `202 Accepted` with the status projection (`status: "verifying"`),
  and the verification runs **outside the request**, in the orchestration worker.
- Confirming an upload that has nothing to confirm (`created`, or a body still
  arriving) is refused with `409 UPLOAD_STATE_CONFLICT`. Confirming again repeats
  the current state — `202` while verifying, `200` once it ended — unless the
  repeat declares a different hash, which is refused with `409`.
- An interrupted transfer can be confirmed: its verification ends it `rejected`.

## What the status shows

```json
{
  "uploadId": "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f",
  "status": "ready",
  "mode": "API_STREAM",
  "profile": "header-items-v1",
  "createdAt": "2026-11-01T04:39:23.123Z",
  "expiresAt": "2026-11-02T04:39:23.123Z",
  "receivedBytes": 3735928,
  "completedAt": "2026-11-01T04:41:02.456Z",
  "sha256": "d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753",
  "sizeBytes": 3735928,
  "rejectionCode": null,
  "rejectionReason": null
}
```

Every member is always present and is `null` while it does not apply.
`receivedBytes` is the bytes the content operation received; `completedAt` is the
confirmation time; `sha256` and `sizeBytes` are the hash and size of the
**uncompressed** data and are shown only once the upload is `ready`; the hash and
size of the stored object stay in the records and are never shown.
`rejectionCode` and `rejectionReason` are shown only when the upload is
`rejected`.

## Rejection codes and reasons

| Code | Reason | Meaning |
|------|--------|---------|
| `DATA_INVALID` | `sha256-mismatch` | The declared hash is not the hash of the data. |
| `DATA_INVALID` | `truncated` | The content is not one complete unit: it ended early, is a concatenation, or is empty while declared compressed. |
| `DATA_INVALID` | `malformed` | The compressed content is present and corrupt. |
| `DATA_INVALID` | `modified` | The stored object no longer matches what arrived. |
| `UNSUPPORTED_CAPABILITY` | `encoding` | The declared encoding is not one the service decodes. |
| `QUOTA_EXCEEDED` | `stored-bytes` | The body crossed the size limit while it arrived. |
| `QUOTA_EXCEEDED` | `input-bytes` | The data decompresses to more than the service accepts. |

A rejected upload has no snapshot: no record and no stored object. `DATA_INVALID`
and `UNSUPPORTED_CAPABILITY` are refusals of the consumer's data, which no retry
changes; a service-side cause is answered `503 SERVICE_UNAVAILABLE`.

## Configuring the size limit

| Setting | Meaning |
|---------|---------|
| `Uploads__MaxStoredBytes` | The most stored bytes one upload may hold. Required, greater than zero; the shipped value is `4294967296` (4 GiB). The limit is configuration, not a promise about the deployment's disk, and the same value is given to the API and to the orchestration worker. |
| `Uploads__MaxUncompressedBytes` | The most the stored data may decompress to. Required, greater than zero; the shipped value is `4294967296` (4 GiB). It is enforced by the orchestration worker as the data leaves the decompressor, so a small compressed body that expands past it is refused with `QUOTA_EXCEEDED` and the reason `input-bytes`; the same value is given to the API and to the worker. |

## Who may use an upload

Every operation of this API resolves the declared `X-Consumer-Id` against the
service's consumer registry **before it reads the body and before it touches any
record**. The registry is a file the operator maintains; a change to it applies
to the next request without a restart, and a request that is already running is
decided by the registry it started with.

| Operation | What it needs of the declared consumer | What an inactive consumer gets |
|-----------|----------------------------------------|--------------------------------|
| `POST /api/v1/uploads` | registered **and active** | `403 CONSUMER_INACTIVE`; no upload is created and the body is not read |
| `GET /api/v1/uploads/{uploadId}` | registered | the status, exactly as an active consumer would read it |
| `PUT /api/v1/uploads/{uploadId}/content` | registered | the content is accepted, as for an active consumer |
| `POST /api/v1/uploads/{uploadId}/complete` | registered | the confirmation is accepted, as for an active consumer |

A consumer that is removed from the registry loses every operation on its
uploads on the next request; one that is readmitted gains them back. Making a
consumer inactive never locks it out of the uploads it already opened: only
opening a new one is refused.

An identity that is absent, empty, repeated, comma-joined or outside the
registry's characters, and one that is simply not in the registry, are all
refused with the same `403 CONSUMER_UNKNOWN` — the same `title` and `detail`
whether or not the upload the request names exists, because no record is read at
all. Another consumer's upload is answered exactly as one that does not exist:
`404 INPUT_NOT_FOUND`, with the same `title` and `detail` as for an absent
identifier, and the storage of that upload is never touched.

## When an upload ends

An upload has two ends, and they are different:

- **the end of its session** (`expiresAt`) bounds the confirmation. An upload
  that was not confirmed before it — `created` or `uploading`, content received
  or not — becomes `expired`, takes no more content and cannot be confirmed.
  Content sent to it is answered `409 UPLOAD_STATE_CONFLICT` with the detail
  *"The upload has expired."*, without reading a byte of the body; a transfer
  still running when the end passes is not recorded, its staging object is
  deleted and it is answered `409`.
- **the verification deadline** bounds the verification, and is counted from the
  confirmation, not from the session. An upload still `verifying` at
  `completedAt + Uploads__VerificationDeadline` becomes `expired` and can never
  become a snapshot, even if a verification finishes later. A repeated
  confirmation of such an upload answers `200` with `status: "expired"` and
  records nothing; a repeated confirmation that declares a content hash
  different from the one the upload was confirmed with is answered `409`.

`expired` is final. The end is applied at the moment of any operation on the
upload and, without traffic, by a periodic sweep that also deletes what an
expired upload was still holding.

| Setting | Meaning |
|---------|---------|
| `Uploads__VerificationDeadline` | How long after its confirmation a confirmed upload may still fix a snapshot. Required, longer than zero, at most seven days, and never shorter than one verification attempt (`Uploads__Verification__StartToClose`); the shipped value is `06:00:00`. The same value is given to the API and to the orchestration worker. |
