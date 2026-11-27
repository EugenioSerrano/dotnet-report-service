# docs

## Purpose

Documentation for users and contributors that does not fit in the top-level
[README](../README.md): guides, references, runbooks and conventions. The
README gives the overview; each page below is the one place its topic is
described in full, and the README links to it rather than repeating it.

## Index

### API reference

- [`api/uploads.md`](api/uploads.md) — opening an upload session, sending the
  data, confirming it and reading its status; the declared consumer identity
  and every upload error code.
- [`api/report-jobs.md`](api/report-jobs.md) — creating a report job,
  following it, cancelling it, and listing and downloading its files, with
  every refusal.
- [`api/templates.md`](api/templates.md) — reading back a taken template
  version and every refusal that read returns.

### Writing templates and data

- [`template-authoring.md`](template-authoring.md) — what a template package
  holds, what a template may and may not do, and the local loop that scaffolds
  a package and previews it before it is published.
- [`data-contract.md`](data-contract.md) — the data contract a snapshot is
  checked against before anything is rendered.

### Running the service

- [`process-topology.md`](process-topology.md) — the processes, what each one
  owns, what "ready" means for each, and the consumer registry.
- [`metadata-database.md`](metadata-database.md) — the service's own
  PostgreSQL database, its role and its migrations.
- [`object-storage.md`](object-storage.md) — the object store, its key
  classes, the Filesystem provider and the conformance suite.
- [`observability.md`](observability.md) — the logs, traces and metrics every
  process reports, and where they go.
- [`limitations.md`](limitations.md) — what this version does not do, and what
  removes each limitation.
- [`document-capacity.md`](document-capacity.md) — how large one PDF document may
  be: the deployment's configured capacity, the measured figures, and what to do
  above it.

### Operations runbooks

- [`operations/upload-verification.md`](operations/upload-verification.md) —
  the step that turns received bytes into an immutable snapshot, its settings
  and its instruments.
- [`operations/printing-and-failed-attempts.md`](operations/printing-and-failed-attempts.md)
  — what happens while a report is printed: retries, restarts and failed
  attempts.
- [`operations/priority-and-per-class-capacity.md`](operations/priority-and-per-class-capacity.md)
  — the three priority levels, the aging of a waiting `low` job, the dedicated
  capacity per class and what to watch.

### Verification and measurement

- [`pdf-verification.md`](pdf-verification.md) — the checks every printed PDF
  passes before it is published, and how to run and reproduce them.
- [`reference-environment.md`](reference-environment.md) — the machine and
  software every published measurement runs on, and how measurements are
  reported.

### Contributing

- [`repository-layout.md`](repository-layout.md) — where everything lives,
  how projects are named, the command of each test level, the shared
  configuration and the versions.

## What does not go here

- Code comments or API docs generated from code — they live with the code.
- What a single folder holds — each top-level folder (`src/`, `tests/`,
  `samples/`, `eng/`, `spikes/`) keeps its own `README.md`.
- Design history and analysis — kept in the companion repository
  [dotnet-report-service-sdlc](https://github.com/EugenioSerrano/dotnet-report-service-sdlc).

## Naming

kebab-case Markdown files (`template-authoring.md`); a sub-folder only when a
topic needs several pages (`api/`, `operations/`). A new page is added to the
index above in the same change.
