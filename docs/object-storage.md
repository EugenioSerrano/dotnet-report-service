# Object storage

Every file the service keeps — uploaded data, template packages, rendered
parts and published reports — is an **object** in object storage. Product
code reaches storage through one port, `IObjectStore`
(`src/ReportService.Storage`), and never through file or cloud APIs of its
own. This page describes the port, the Filesystem provider, its
configuration, the storage root shared by the local composition, and the
conformance suite every provider must pass.

## The port

| Operation | What it does |
|-----------|--------------|
| `WriteAsync(key, stream)` | Writes the stream **once** under the key, through a bounded buffer, and returns the size and SHA-256 of the bytes actually written. A key that already holds an object is never overwritten: the outcome is `AlreadyExists` and the existing object is unchanged. |
| `OpenReadAsync(key, expectedSize, expectedSha256)` | Opens the object for a **verified read**: the returned stream counts and hashes the bytes as they are consumed and, at its end, throws `StorageIntegrityException` when the size or the SHA-256 differs from what the caller expected — a changed, shorter or longer object. |
| `GetInfoAsync(key)` | Size and last-modified time (UTC). |
| `DeleteAsync(key)` | Removes the object. Deleting a key with no object is a success. |
| `ListAsync(prefix)` | The objects under a key prefix, in ordinal key order, a page at a time; a whole listing is never held in memory. |
| `Capabilities` | What the provider can do beyond this contract (below). |

Content always travels as a stream — never as a byte array, a string or a
memory stream — so the memory a transfer uses does not depend on the size of
the object.

### Outcomes, not exceptions

Every operation returns a `StorageResult<T>` whose `Outcome` is one of:

| Outcome | Meaning |
|---------|---------|
| `Success` | Done; `Value` holds the result. |
| `NotFound` | No object at the key. |
| `AlreadyExists` | The key already holds an object; nothing was written. |
| `Transient` | A failure that may succeed later (for example an I/O error). |
| `Permanent` | A failure retrying will not fix: configuration, permission, an unsafe path. |
| `IntegrityFailure` | The content does not match its expected size or SHA-256. |

A failure carries an `Error` with a stable code (`storage.already-exists`,
`storage.path-outside-root`, `storage.integrity.hash-mismatch`, …) and a
message for the operator. **No key, path, URL or credential ever appears in
an error, an exception message or a log line** written by the storage code.

Two failures are raised as exceptions because no result can carry them: an
integrity failure found while a verified stream is consumed
(`StorageIntegrityException`), and a failure while a listing is enumerated
(`StorageException`). An exception thrown by the caller's own source stream
during a write is passed on unchanged, after the partial write is removed.

### Capabilities

| Capability | Meaning | Filesystem provider |
|------------|---------|---------------------|
| `SignedUpload` | Can grant the write of one staging key through a signed URL | no |
| `SignedDownload` | Can grant the read of one key through a signed URL | no |
| `NativeCreateOnly` | Refuses, by itself and atomically, a write to an existing key | **yes** |
| `RangeReads` | Can read part of an object (not verified) | no |

A provider declares its capabilities once; the application reads them before
choosing a path. Signed URLs (`ISignedUrlStore`) exist only on a provider
that declares them.

### Keys

A key is an `ObjectKey`, never a plain string. It is made of segments joined
by `/`:

- each segment matches `[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?` — lower-case
  letters, digits, `.`, `_` and `-`, starting and ending with a letter or a
  digit, 1 to 128 characters;
- a segment is never `.`, `..` or a reserved device name (`con`, `prn`,
  `aux`, `nul`, `com1`–`com9`, `lpt1`–`lpt9`, with or without an extension);
- at most 16 segments and 512 characters; no backslash, no `:`, no
  character outside ASCII; upper case is refused, never lowered.

A refused value gets a fixed reason that never repeats the value. Text that
comes from outside the service is mapped by its own code to valid segments
before it becomes a key.

## The Filesystem provider

`src/ReportService.Storage.Filesystem` stores an object as a file at
`<root>/<segment>/…/<segment>`.

- **Write once.** The content is streamed to a temporary file in the target
  directory (`.<name>.tmp-<32 hex>`), flushed to disk, and then published
  under its key with a create-only operation: a move without overwrite on
  Windows, a hard link on Linux (where a move without overwrite is a check
  followed by a rename that a concurrent writer can slip between). Two
  writers racing for one key never both succeed, and a partial object is
  never visible under its key.
- **Path safety.** Every call checks that the resolved path lies under the
  root, also through every link on the way; a link that leads out of the root
  is refused (`storage.path-outside-root`), and so is a path longer than 248
  characters (`storage.path-too-long`).
- **Crashes.** A writer that stops mid-way leaves only its temporary file.
  When the provider starts it deletes temporary files older than 24 hours;
  it never touches any other file.
- **Production use.** One volume visible to every process that uses storage,
  with POSIX rename semantics and hard links (a single machine, or a
  read-write-many volume). Network file systems with weaker rename or flush
  semantics are not supported.

### Configuration

| Setting | Required | Meaning |
|---------|----------|---------|
| `Storage:Provider` | yes | The provider: `filesystem` (the only one so far). There is **no default**: a process without it, or with an unknown provider, does not start. |
| `Storage:Filesystem:Root` | yes | The root directory, absolute or relative to the process's content root. |
| `Storage:Filesystem:CreateIfMissing` | no (`false`) | Create a missing root at start; otherwise a missing root stops the start. |
| `Storage:Filesystem:BufferBytes` | no (65536) | The copy buffer of a write, 4096 to 1048576 bytes. |
| `Storage:Diagnostics:Enabled` | no (`false`) | Mounts the development diagnostics below. |

At start the process checks that the root exists (or creates it when
allowed), that it is a directory and that a file can be written and
published in it; a failure stops the start naming the setting, never its
value. It then logs one line with the provider, the root, the capabilities
and the number of stale temporary files removed. The development settings of
the API and the orchestration worker (`appsettings.Development.json`) select
`filesystem` with the root `.storage` at the repository root.

### Health

Each process has a `storage` health check, part of its readiness: it writes a
small object under `staging/probe/<process>/`, reads it back verified and
deletes it, within 5 seconds. Healthy reports `provider`, `writable` and
`readable`; unhealthy reports `provider` and the `error` code. A process whose
root stops being writable becomes not ready and is ready again when the root
recovers, without a restart.

## The local composition

The API, the orchestration worker and the PDF worker share **one storage
root**: the directory `.storage` at the repository root, outside `src/` and
ignored by version control. The two host processes use its host path; the PDF
worker's container sees the same directory mounted at `/data/storage` and
writes there as its non-root user (uid 1654). One physical directory, two
logical roots.

### Development diagnostics

When `Storage:Diagnostics:Enabled` is true — the composition sets it for its
three processes, a deployed profile does not — each process offers two
endpoints that exist only to show that the root is shared. They are **not a
product API** and have no authentication; keep them off outside development.

| Endpoint | Behaviour |
|----------|-----------|
| `POST /diagnostics/storage/{key}` | Writes the request body once; `200 { key, size, sha256 }`, or `409` when the key exists. |
| `GET /diagnostics/storage/{key}?size=&sha256=` | Reads the object verified against those values; `200 { key, size, sha256, verified }`, `422` when the content does not match, `404` when it is missing. |

A refused key is answered `400` with the reason; an error body never repeats
the key.

## The conformance suite

`tests/ReportService.Storage.Tests/ObjectStoreConformanceTests.cs` is the
behaviour every provider must show, run unchanged against each one. It
proves that:

- a write returns the true SHA-256 and size of what was stored;
- a second write to the same key reports `AlreadyExists` (on a provider that
  declares `NativeCreateOnly`) and leaves the first object unchanged;
- a verified read detects a flipped byte, a truncated object and an extra
  byte;
- deleting a missing key succeeds;
- a listing is complete, in ordinal order and free of duplicates;
- writing a 1 GiB source that cannot be rewound grows the process's peak
  working set by no more than writing a 64 MiB source does, plus 32 MiB;
- the key grammar refuses traversal and foreign characters;
- no key, path, URL or credential appears in logs or errors;
- signed URLs, where a provider declares them, grant exactly one key and one
  verb and expire.

**Adding a provider.** Create its test project, reference
`ReportService.Storage.Tests`, derive `ObjectStoreConformanceTests`, and
supply the store, its declared capabilities and a way to change stored bytes
behind the store's back (and, if it declares signed URLs, their cases). A
provider that fails any case is not shipped.
