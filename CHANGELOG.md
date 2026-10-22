# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
