# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Work towards **0.1.0** (in progress — builds report `0.1.0-dev`).

### Added

- Repository layout: `src/`, `tests/`, `samples/`, `docs/`, `eng/` and
  `spikes/`, each with a README describing its purpose and naming rules.
- Shared build configuration: nullable reference types, implicit usings,
  warnings as errors, code style enforced in build, deterministic builds.
- Central package management and a package source restricted to nuget.org
  with package source mapping.
- Platform matrix: .NET 10 SDK pinned to the 10.0.4xx band, `net10.0` and
  analyzer level 10.0 as shared defaults.
- Product version line 0.1.0, with pre-release builds reporting `0.1.0-dev`,
  and this changelog.
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

[Unreleased]: https://github.com/EugenioSerrano/dotnet-report-service/commits/develop
