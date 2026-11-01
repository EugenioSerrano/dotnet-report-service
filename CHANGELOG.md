# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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

- The error format is stated as RFC 9457 (Problem Details for HTTP APIs), which
  supersedes the RFC 7807 reference in the README.
- The API's `/health` body is JSON, like the workers': the metadata database's
  check reports the expected and applied schema versions. Status codes are
  unchanged (200 ready, 503 not ready).
- The health body's allowed data keys gain `expected`, `applied` and `error`.
- `Npgsql` becomes a product package and `Dapper` joins it; the composition and
  the processes that use the database connect as the application role only.

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
