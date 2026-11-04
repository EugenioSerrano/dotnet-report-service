# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A report job can be followed: `GET /api/v1/report-jobs/{reportJobId}` answers the
  job's owner with its state, the step it is in, the counters `recordsRead`,
  `blocksCompleted` and `pagesGenerated` — each of which only rises, across retries
  and restarts — and the totals `recordsTotal` and `blocksTotal`, `null` until the
  job has counted them and never a guess (a total of `0` is a counted, empty
  report). Each requested output carries its own state and, once published, the
  identifier of its file; a failed job and its failed output carry the stable code
  of the condition. No percentage is given anywhere. The answer is never cached and
  one read is one indexed query.
- A job's run now ends by closing the job from the outcome of its outputs: the last
  step of the run finalises the job idempotently — every output succeeded makes it
  `succeeded`, a failed output makes it `failed` with that output's code, and a job
  whose outputs have not ended is left as its steps left it. The step and the job's
  first transition are gated by one patch, so every history recorded before them
  still replays unchanged.
- A job's stage is written with its status and never moves back: the first
  transition records `validating-data`, and the rendering transition records
  `rendering-pdf`. The step a finished job last entered is kept in storage and
  hidden from the answer, because a finished job is in no step.

- A job's snapshot is checked against the data contract of the template version it
  is pinned to before anything is rendered: the header and every record are
  validated against the template's `schema.json`, in the same single pass that
  reads the snapshot, and a value that breaks the schema ends the job `failed`
  with `DATA_INVALID` and a diagnostic naming the ordinal, the byte offset, the
  field's path and the rule — never the value. A violation anywhere in the
  snapshot, the last record of a full block included, ends the job before the
  first page is rendered, and nothing of it is published. The schema is read
  from the taken copy of the pinned version through a verifying read, compiled
  once per package per worker and kept in a bounded cache; the validator is
  `Corvus.Text.Json` 5.6.1, pinned exactly, with its licence recorded in
  `THIRD-PARTY-NOTICES.md`.
- A template whose schema cannot be used — absent from the taken copy, outside
  the admitted profile, not compilable, or too slow or too deep to decide a
  value — ends the job `failed` with `TEMPLATE_INVALID` in one attempt instead.
- A job's own template reference is read before its snapshot, and the version it
  names is looked up in the taken versions: a reference that names no version, or
  one that was never taken, ends the job `TEMPLATE_INVALID rule=pin` in one
  attempt, with no exception and no retry.
- Referencing a template by name only: a job that names no version gets the
  highest valid **release** version of that template, ordered by Semantic
  Versioning 2.0.0 — so `1.10.0` is newer than `1.2.0`, a pre-release sorts below
  its release and is used only when a job names it exactly, and two builds of one
  version are of equal precedence. A newer version its author has to fix is
  logged with what to fix and skipped, so one broken version never breaks the
  jobs that name the template only; when no version is usable the job ends failed
  with `TEMPLATE_INVALID` naming the newest one, and when the template repository
  cannot be checked it uses the newest release already taken or ends failed with
  `TEMPLATE_SOURCE_UNAVAILABLE`. A taken version stays usable after its folder is
  removed from the repository, and a version the requesting consumer may not use
  is never chosen.
- `ResolveTemplateVersion`, the activity the PDF worker serves on its queue: it
  reads the template's version folders at one commit, orders them by precedence,
  takes each candidate through the take, and answers the version a job is pinned
  to with the values the take records, saying whether it was taken now or is the
  copy already kept. A refusal ends it with the resolution's own code, retrying a
  source that may come back and never retrying a template its author has to fix.
- `TemplateSource:ResolveDeadline`: how long one by-name resolution — the listing
  and every version it takes — may take, from five seconds to five minutes, four
  minutes by default, validated at start.
- The take's registration now shares the queue with the by-name resolution: one
  engine worker on `report-pdf-rendering` serves both steps at one activity at a
  time.
- The template repository now names what a by-name reference gets, in
  `samples/templates/README.md`, for template authors.
- The take: the first time a job references a template version, the service reads
  it from the configured repository and branch at one commit and keeps it as an
  immutable copy — one write-once object under
  `templates/<owner>/<templateId>/<version>/<sha256>`, made usable only by an
  insert-once record — so a version taken once is never taken again and never
  changes afterwards. A version already taken is answered from its record without
  reading the source; a version a consumer may not use is answered as missing
  before any refusal that could name a file of the package; a taken version that
  changed in the branch is logged as `TEMPLATE_VERSION_CONFLICT` and the copy is
  kept. A source that cannot be reached, an absent version and the refusals an
  author has to fix are told apart by the job's own error code.
- `TakeTemplateVersion`, the activity the PDF worker serves on its queue: it runs
  one take at a time, answers the values a job is pinned to (commit, package hash
  and size, engine and its version, schema path, owner and the shared flag), and
  ends a refusal with the take's own code, retrying only a source that may come
  back.
- Reading `template.yaml`: one reader, through the YamlDotNet 18.1.0 event
  parser, under a restricted subset with size, depth and node limits; the document
  is stored once as JSON, so nothing that reads a taken version needs YAML again.
- `THIRD-PARTY-NOTICES.md`, with YamlDotNet 18.1.0's MIT licence and the
  copyright line the package itself records.
- The pilot template manifests name their owning consumer and their `shared` flag,
  declare the fields their printed PDF is verified against, and write their part
  paths under `parts`; `samples/templates/README.md` documents the manifest subset
  for template authors.
- `tests/ReportService.Workers.Pdf.Tests`: the take's registration on its queue
  and the activity as a plain class.
- A report job created on an upload that is still being verified waits for it
  instead of stopping at `queued`: it reports the stage `waiting-for-input`, it
  starts once the upload is ready, it ends `failed` with the upload's own code
  when the upload is rejected, and it ends `failed` with `INPUT_NOT_FOUND` when
  the upload's verification deadline passes. The wait re-checks the upload on a
  notification a finished verification sends to the jobs of its upload and on a
  durable timer of its own, so a lost notification costs at most half a minute,
  and a wait that outlasts the checks one run may take continues as a new run of
  the same execution. `Jobs__InputWait__Backstop` and
  `Jobs__InputWait__ContinueAsNewAfter` set the two bounds; a pass limit the
  history of one run cannot carry stops the worker at start.
- The taken template version: a version read from the configured template
  repository and branch at one commit, kept as one write-once object under
  `templates/<owner>/<templateId>/<version>/<sha256>` and made usable by an
  insert-once record whose primary key is the template and the version, so a
  taken version is never overwritten and never taken twice. The record holds the
  commit, the package hash and size, the engine and its version, the schema path
  and the whole manifest as JSON, so nothing that reads a taken version ever
  needs the source format again.
- The `report_service.template_versions` table and its migration, and the
  record store behind it (Dapper over Npgsql, an insert that lets the primary
  key decide a race, constant SQL with parameters only).
- `ReportService.TemplatePackages`: the content-addressed key of a package, the
  semantic version syntax, the deterministic package archive (one uncompressed
  ustar archive whose bytes depend on the files alone) and the reader that
  extracts a taken copy only after the whole object verified.
- `ReportService.TemplateSource`: the settings of the configured source and
  their bounds, a client that reads a branch at one commit, walks to
  `templates/<templateId>/<version>/`, lists its files and streams each blob
  verified against its git hash, and classifies every failure as unreachable,
  misconfigured, too large or unsupported — never as "the version does not
  exist". A tree the source reports as truncated is refused rather than read as
  an absent version.
- The warning a taken version logs when it reads differently from its copy
  (`TEMPLATE_VERSION_CONFLICT`), which keeps the copy and refuses nothing, and
  the seam the take-time checks plug into (no check is registered yet).
- The template-source test double: a request handler that answers the source's
  documented shapes with real git object names, and an in-memory source whose
  versions can be seeded and replaced.
- `GET /api/v1/templates/{templateId}/versions/{version}`: the owner's read of a
  taken template version answers the commit, the package hash, the engine and its
  version, the owner and the shared flag the take fixed, plus when it was taken.
  The operation reads the record only — no storage and no template repository —
  and answers another consumer's version exactly like one that was never taken.
  See `docs/api/templates.md`.

- Report jobs: a consumer creates one with `POST /api/v1/report-jobs` for a
  confirmed upload it owns, a template reference and the output format it wants,
  and receives `202 Accepted` with the job's identifier, the state `queued` and
  the URL its status is read from. The job, its requested output, the template
  reference exactly as the request wrote it and the request to start it are
  written together in one transaction before the answer is given, so a client
  that closes its connection after sending its request has still created a job
  that runs. The orchestration worker starts each accepted job exactly once,
  across restarts of both processes; a job whose upload is refused or runs out
  of time ends failed with the upload's own condition. The creation operation
  checks the consumer against the registry before it reads anything else and
  answers an unsupported format or profile, an unusable data reference and a
  malformed body with their own codes; `Idempotency-Key`, `render` and
  `metadata` are not honoured yet. See `docs/api/report-jobs.md` and
  `docs/operations/upload-verification.md`.

- An upload ends by itself when nobody finishes it: one that is not confirmed
  before the end of its session becomes `expired`, takes no more content and
  cannot be confirmed, and one whose verification outlasts the deadline recorded
  at its confirmation expires as well and can never become a snapshot
  afterwards. The end is applied at the moment of any operation on the upload
  and, without traffic, by a periodic sweep in the orchestration worker that
  also deletes what an expired upload was still holding. The deadline is
  configured by `Uploads__VerificationDeadline` (six hours by default, never
  shorter than one verification attempt) and the sweep by
  `Uploads__Expiry__SweepInterval` and `Uploads__Expiry__BatchSize`. See
  `docs/api/uploads.md` and `docs/operations/upload-verification.md`.
- Every upload operation resolves the declared consumer against the service's
  consumer registry before it reads the body or touches a record: a consumer
  that is not registered is refused with `CONSUMER_UNKNOWN`, an inactive one
  cannot open a new upload (`CONSUMER_INACTIVE`) but keeps reading, streaming to
  and confirming the uploads it opened while it was active, and a registry change
  applies without a restart.

- Report data can be **streamed** to an upload — plain, or compressed with
  `Content-Encoding: gzip` — and then **confirmed**: the body is written to the
  upload's staging object as it arrives, counted against the size limit and never
  held in memory, and the confirmation answers `202 Accepted` with the upload
  `verifying`. The service then verifies the stored bytes in the background and
  keeps an immutable snapshot with the SHA-256 and size of the content, or ends the
  upload rejected with a stable code and reason that the status read shows back.
  Content sent to an upload that is already ready is refused and its snapshot stays
  as it was. See `docs/api/uploads.md` and
  `docs/operations/upload-verification.md`.
- Upload verification: a stored upload is read once, a copy of exactly those
  bytes is written to a snapshot object, and the content is measured as it is
  read — the bytes stored, the content they decompress to, and whether a
  compressed object is one complete unit of content whose own trailer agrees
  with what was read. A verified upload ends with an immutable snapshot record
  carrying the hash and size of the content; anything else ends rejected with a
  code and a reason, and no snapshot exists for it. The upload record and its
  store now live in one library shared by the API and the orchestration worker.
  See `docs/operations/upload-verification.md`.

- Upload sessions: a consumer opens one for its report data with
  `POST /api/v1/uploads` (`header-items-v1`, `API_STREAM`) and receives its
  `uploadId`, the state `created` and the time it expires, then reads the same
  status back with `GET /api/v1/uploads/{uploadId}`. A request for any other
  profile or mode — including direct-to-storage, which this version does not
  offer — is refused at once with `UNSUPPORTED_CAPABILITY` and no session is
  created. The declared consumer, carried in the `X-Consumer-Id` header, owns
  every session it creates, and another consumer's session is answered as one
  that does not exist.
- The `report_service.uploads` table, its migration, and the record store behind
  the upload operations (Dapper over Npgsql, owner-scoped reads, a five-second
  command timeout).
- The API's error answers are RFC 9457 problem documents carrying a stable
  `code` and the request's `traceId`: `CONSUMER_UNKNOWN` (403),
  `UNSUPPORTED_CAPABILITY` (422), `QUOTA_EXCEEDED` (413), `REQUEST_INVALID`
  (400, with `field`, `reason` and `unknownFieldCount`), `INPUT_NOT_FOUND` (404)
  and `SERVICE_UNAVAILABLE` (503 or 500). No answer carries an exception message,
  a statement, a connection detail or a value the caller sent.
- `Uploads__SessionLifetime`: how long a new upload session accepts data, one day
  by default, validated at start.
- `docs/api/uploads.md`: the identity header, both operations, the session states
  and every error code.
- The service's own metadata database and credentials: the `reportservice`
  database, owned by the `reportservice` login role, which is not a superuser
  and cannot create databases or roles. The local composition creates both once,
  from a script the PostgreSQL image runs at initialisation, with a password
  generated at every start; no process of the service receives the server's
  bootstrap user.
- `ReportService.Migrations`, a one-shot migrator: versioned, forward-only SQL
  scripts embedded in the build, applied in order under a PostgreSQL advisory
  lock, each in its own transaction with a journal row that records its SHA-256
  checksum. It refuses a script that changed after it was applied and a journal
  entry the build does not know, and exits non-zero naming the failing script.
  Its baseline creates the `report_service` schema; the upload sessions add the
  first business table beside it.
- A `metadata-database` readiness check in the API and the orchestration worker:
  ready only when the database is reachable and at the schema version the build
  expects, with the expected and applied versions in the health body. The PDF
  worker has no database check until it writes its first record.
- `ReportService.Migrations.Tests`, the hermetic unit and component tests of the
  migrator, with a coverage floor in the test runner.
- `docs/metadata-database.md`: the database and its role, the journal, how a
  change adds a migration, and how an operator provisions the database and
  applies migrations to an existing deployment.

### Changed

- **The package path rule now holds what the package archive can write:** one to
  eight segments of at most a hundred characters each and at most 240 characters
  in total, so a path the rule accepts can never fail to be written. A segment or a
  path over those lengths is refused by the rule itself.

- The error format is stated as RFC 9457 (Problem Details for HTTP APIs), which
  supersedes the RFC 7807 reference in the README.
- The API's `/health` body is JSON, like the workers': the metadata database's
  check reports the expected and applied schema versions. Status codes are
  unchanged (200 ready, 503 not ready).
- The health body's allowed data keys gain `expected`, `applied` and `error`.
- `Npgsql` becomes a product package and `Dapper` joins it; the composition and
  the processes that use the database connect as the application role only.
- A host's telemetry is now its own: its instruments are created on a meter
  identity the host carries, its metric provider subscribes to that identity, and
  a unit of work opens its `job_id` log scope on the logger factory of the host
  that opens it. A process that owns its host exports the same instrumentation
  scope name (`ReportService`), the same instruments and the same `job_id` scope
  as before; a process that hosts more than one host no longer mixes their
  telemetry.

## [0.1.0] - 2026-10-22

First release. It completes the groundwork milestone of the roadmap
(Stage 0): the repository layout and shared build conventions, a local
development composition started with one command, seeded pilot datasets
with sample templates, and five measured technical spikes. No report is
rendered yet: the API answers health checks only and the render worker only
checks that Chromium starts. The spikes record measurements, not decisions;
the remaining library choices are made from them in the next versions.

### Added

- Repository layout: `src/`, `tests/`, `samples/`, `docs/`, `eng/` and
  `spikes/`, each with a README describing its purpose and naming rules.
- Shared build configuration: nullable reference types, implicit usings,
  warnings as errors, code style enforced in build, deterministic builds.
- Central package management and a package source restricted to nuget.org
  with package source mapping.
- Platform matrix: .NET 10 SDK pinned to the 10.0.4xx band, `net10.0` and
  analyzer level 10.0 as shared defaults.
- Product versioning: one Semantic Versioning version for the whole
  repository, a `-dev` pre-release label on builds between releases, and
  this changelog.
- README describing the product design, and `docs/repository-layout.md`.
- Local development composition started with one command
  (`dotnet run --project src/ReportService.AppHost`): an API stub, PostgreSQL
  18, Valkey 9.1, the Temporal development server and a render worker
  container, each with a health check; the render worker image is built by
  the .NET SDK on a base image that holds only the Chromium headless shell.
- PDF base image definition (`eng/pdf-base/`) and a local development
  registry, bound to the loopback address, used by the local composition.
- Seeded pilot dataset generator (simple, grouped and high-volume datasets in
  the `header-items-v1` envelope) with a `verify` command and the expected
  hashes in `samples/datasets/SHA256SUMS`.
- Three sample template packages, one per pilot dataset.
- Reference environment description for published measurements
  (`docs/reference-environment.md`).

#### Measured spikes

Throwaway evaluation programs under `spikes/`, kept for their measurements.
They are not part of the product: they are not in the solution and are
never packaged or shipped. Each README states the hypothesis, how to run
the spike, the measured results and the conclusions.

- Streaming data and template engines
  ([`spikes/01-streaming-data/`](spikes/01-streaming-data/README.md)):
  incremental reading of the record envelope, plain and gzip, with every
  data limit failing with a stable, named error, and per-record rendering
  through Scriban, Handlebars and Liquid with one render context and one
  escaping policy; measures memory, the byte identity of the three engines'
  HTML and the cost of each engine.
- PDF layout ([`spikes/02-pdf-layout/`](spikes/02-pdf-layout/README.md)):
  page layout of the grouped pilot dataset with Paged.js in headless
  Chromium on the PDF base image; checks page subtotals against the data
  and measures memory and time per block size, the handling of one very
  tall row, and Chromium's native page margin boxes as an alternative.
- PDF assembly ([`spikes/03-pdf-assembly/`](spikes/03-pdf-assembly/README.md)):
  ordered merge of hundreds of partial PDFs and global "Page X of Y"
  numbering with PDFsharp and qpdf at 1 000, 5 000 and 10 000 pages;
  measures memory, time, temporary disk and output size, and checks the
  structure, page order, numbering and pixels of every output.
- XLSX writer ([`spikes/04-xlsx-writer/`](spikes/04-xlsx-writer/README.md)):
  1 000 000 rows written with MiniExcel and with the Open XML SDK writer;
  measures memory and checks exact per-column typing, formula-like text kept
  as text, the sheet row limit, and that the files open without repair.
- Markdown conversion ([`spikes/05-markdown/`](spikes/05-markdown/README.md)):
  four routes from report HTML to Markdown — a library per fragment, a
  streaming converter, Markdown template parts, and a hybrid by size;
  measures table continuity across fragments, memory, and what each route
  loses against the HTML output.

[Unreleased]: https://github.com/EugenioSerrano/dotnet-report-service/compare/v0.1.0...develop
[0.1.0]: https://github.com/EugenioSerrano/dotnet-report-service/releases/tag/v0.1.0
