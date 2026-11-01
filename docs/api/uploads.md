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
