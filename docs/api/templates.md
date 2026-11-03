# Template version operations

A consumer reads back what the service fixed when it took a template version, so
its own tooling can show which version its jobs render with — the commit it was
read from, the hash of the package the service keeps, the engine and the engine
version. This page describes the read, the answer and every refusal it gives.

The take itself is the service's job step, not an operation of this API: a
consumer never uploads a template and never asks for one to be taken. A version
that was never taken has no metadata to answer, and the read says so.

## Read a version

```http
GET /api/v1/templates/invoice-energy/versions/1.1.0
X-Consumer-Id: billing-system
```

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "templateId": "invoice-energy",
  "version": "1.1.0",
  "commitSha": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
  "packageHash": "sha256:5e88e6f1b5f8d8f0eaa2b2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4",
  "engine": "SCRIBAN",
  "engineVersion": "7.5.1",
  "owner": "billing-system",
  "shared": false,
  "takenAt": "2026-11-01T12:00:00.000Z"
}
```

| Member | Meaning |
|--------|---------|
| `templateId` | The template's identifier, as the version's folder and its manifest name it. |
| `version` | The version, exactly as the manifest declares it; build metadata is part of the value. |
| `commitSha` | The commit of the configured repository and branch the whole read was pinned to. |
| `packageHash` | The SHA-256 of the package's bytes, prefixed with the algorithm it was computed with. |
| `engine` | The engine that renders the package: `SCRIBAN`, `HANDLEBARS` or `LIQUID_FLUID`. |
| `engineVersion` | The version of that engine this deployment renders with. |
| `owner` | The consumer the version belongs to. |
| `shared` | Whether every consumer may use the version, as the manifest declares it. |
| `takenAt` | When the version was taken, in UTC with milliseconds. |

Every member is always present, and none of them changes after the version is
taken: the package is write-once and the record it is published through is
inserted once, so a change to the version in the repository can never alter what
this read answers. A changed version is a new version; the service logs the
conflict under the old one and keeps what it has.

`templateId` must be a lower-case identifier: `a-z`, `0-9`, `.`, `_` and `-`,
beginning and ending with a letter or a digit, at most 128 characters.
`version` must be a semantic version (`MAJOR.MINOR.PATCH`, with an optional
pre-release and build metadata). A request whose two values cannot name a taken
version is answered as a version that does not exist, without reaching the
record at all.

## Who may read

The declared consumer identity decides, as it does for every operation of this
API:

- The header must be present **exactly once**, carry **one** value and match
  `[A-Za-z0-9._-]{1,128}`. Comparison is ordinal.
- A consumer that is registered may read, **including one that was made
  inactive**: a version it owns stays readable, as an upload's status does.
- Only the **owner** of a version reads it. Another consumer's version is
  answered exactly like one that was never taken, so the operation never tells a
  consumer which templates exist outside its own. Sharing a version with other
  consumers is not offered yet.

## Refusals

Every refusal is a Problem Details document with a stable `code`; the same code
always means the same condition. The checks run in this order: the consumer, the
form of the two names, and finally the record.

| Case | HTTP | `code` |
|------|------|--------|
| No, malformed or unregistered consumer identity | 403 | `CONSUMER_UNKNOWN` |
| The version was never taken, belongs to another consumer, or its name cannot be a taken version | 404 | `TEMPLATE_NOT_FOUND` |
| The service could not read its own records | 503 | `SERVICE_UNAVAILABLE` |

`404` is the same document for all four of its cases — an absent version, another
consumer's version, an untaken version and a malformed name — and the same
constant detail: **"The template version was not found."** The caller learns
nothing about which versions exist or how their names are formed.

A `503` says the request was not the caller's fault and may be retried later. It
carries no exception, connection or SQL detail.

## What this operation does not do

- It does not read the template repository, and no request to this API can: the
  API holds the taken version's record only, and the source is read by the PDF
  worker's take step.
- It does not return the package, its files or its storage key; a version is used
  by the service, never downloaded through this operation.
- It does not list versions or templates; a version is read by the exact pair of
  its template and its version.
