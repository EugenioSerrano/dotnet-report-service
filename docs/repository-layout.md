# Repository layout

This page explains where everything lives in the repository, how projects are
named, and which conventions the layout follows. Each top-level folder also
has its own `README.md`.

## Top-level structure

```text
dotnet-report-service/
├── src/                       Product source — one folder per project
│   ├── ReportService.Api/                         The API (no workflow-engine client)
│   ├── ReportService.AppHost/                     The local composition
│   ├── ReportService.ServiceDefaults/             Validated settings, health bodies and the object store, shared by every process
│   ├── ReportService.ServiceDefaults.Temporal/    The workflow-engine connection, shared by the two workers
│   ├── ReportService.Storage/                     The object storage port: write-once, verified reads, key grammar
│   ├── ReportService.Storage.Filesystem/          The Filesystem provider of the storage port
│   ├── ReportService.Workers.Orchestration/       The orchestration worker (orchestration and data queues)
│   └── ReportService.Workers.Pdf/                 The PDF worker (container image)
├── tests/                     Automated tests, mirroring src/
│   ├── ReportService.Api.Tests/          Unit/component tests of the API (hermetic)
│   ├── ReportService.Storage.Tests/      Unit/component tests of the storage port and its Filesystem provider, and the provider-conformance suite (hermetic)
│   ├── ReportService.IntegrationTests/   Tests against real dependencies in containers
│   ├── ReportService.EndToEndTests/      Tests over the whole local composition
│   └── ReportService.Testing/            Support library shared by the test projects
├── samples/                   Templates and datasets for adopters
├── docs/                      Documentation (this page; the processes and their readiness: process-topology.md; object storage: object-storage.md)
├── eng/                       Build and engineering assets
│   ├── pdf-base/              The PDF base image (Chromium headless shell)
│   └── test/                  The test runner: one command per test level
├── spikes/                    Stage 0 experiments (not part of the product)
├── artifacts/                 Local build and test output (git-ignored, never committed)
├── .storage/                  Object storage root of the local composition (git-ignored, never committed)
├── dotnet-report-service.slnx Product solution
├── global.json                .NET SDK pin (10.0.4xx band)
├── Directory.Build.props      Build settings shared by every project, product version
├── Directory.Packages.props   Central package versions
├── NuGet.config               Package sources (nuget.org only)
├── .editorconfig              Code style and whitespace rules
├── .dockerignore              Files kept out of container build contexts
├── .gitattributes             Line endings (LF) and binary files
├── CHANGELOG.md               Release history
├── README.md
└── LICENSE
```

## Naming rules

| What | Rule | Example |
|------|------|---------|
| Product project | `src/ReportService.<Area>/ReportService.<Area>.csproj` | `src/ReportService.Workers.Pdf/` |
| Root namespace | Same as the project name | `ReportService.Workers.Pdf` |
| Unit/component tests | `tests/ReportService.<Area>.Tests/` — one per product project; a provider project is tested in its port's project | `tests/ReportService.Workers.Pdf.Tests/`, `tests/ReportService.Storage.Tests/` (the port and its Filesystem provider) |
| Cross-cutting test suites | `tests/ReportService.<Kind>Tests/` | `tests/ReportService.IntegrationTests/`, `tests/ReportService.EndToEndTests/` |
| Shared test support (library, referenced by test projects only) | `tests/ReportService.Testing/` | `tests/ReportService.Testing/` |
| Sample template | `samples/templates/<template-id>/` | `samples/templates/invoice-energy/` |
| Sample tool | `samples/<area>/ReportService.Samples.<Tool>/` | `samples/datasets/ReportService.Samples.DatasetGenerator/` |
| Image built from a Dockerfile | `eng/<image>/Dockerfile` — only for images that need operating-system steps; product images are built by the .NET SDK from their project file | `eng/pdf-base/Dockerfile` |
| Engineering tool | `eng/<area>/ReportService.Eng.<Tool>/` | `eng/pdf-base/ReportService.Eng.PlaywrightInstaller/`, `eng/test/ReportService.Eng.TestRunner/` |
| Test run output | `artifacts/test-results/<yyMMdd-HHmm>/` (git-ignored) | `artifacts/test-results/261101-1430/run.json` |
| Spike | `spikes/NN-<topic>/` | `spikes/01-streaming-parser/` |
| Documentation page | kebab-case `.md` in `docs/` | `docs/template-authoring.md` |

Product and test projects are added to `dotnet-report-service.slnx` under the
`src` and `tests` solution folders (`dotnet sln add <path> --solution-folder src`);
sample tools go under the `samples` solution folder and engineering tools
under the `eng` solution folder, so they build with the same rules. Spikes are never added.

## Commands

Every command runs from the repository root.

| What | Command |
|------|---------|
| Start the local composition | `dotnet run --project src/ReportService.AppHost` (see the [README](../README.md)) |
| Hermetic tests (unit/component, coverage floors) — no container runtime | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic` |
| Integration tests — real dependencies in containers | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- integration` |
| End-to-end tests — the whole composition | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- e2e` |

What each test level may reach, the evidence a run leaves under
`artifacts/test-results/` and the coverage rules are described in
[`tests/README.md`](../tests/README.md).

## Shared configuration

- **`global.json`** pins the .NET SDK to the 10.0.4xx band (10.0.402 or a
  later patch, no previews); any `dotnet` command run in the repository uses
  it or fails with an explanation of what to install. It also selects
  Microsoft.Testing.Platform as the runner of `dotnet test`.
- **`Directory.Build.props`** is imported automatically by every project below
  the root. It sets the shared defaults `net10.0` and analyzer level 10.0,
  enables nullable reference types and implicit usings, treats warnings as
  errors, enforces code style during build, declares the product version
  (`VersionPrefix`) and sets the package metadata (MIT licence, repository
  URL). A project may override the target framework or analyzer level
  explicitly when it has a reason to; the language version is the framework's
  default.
- **`spikes/Directory.Build.props`** imports the root file and relaxes it for
  experiments: warnings do not fail the build and nothing is packable.
- **`Directory.Packages.props`** enables central package management: package
  versions are declared once there with `PackageVersion`; project files use
  `PackageReference` without `Version`.
- **`NuGet.config`** clears inherited sources, keeps only nuget.org and maps
  every package to it, so a package cannot be resolved from an unexpected
  feed. Adding a feed means adding it explicitly with its own package pattern.
- **`.editorconfig`** and **`.gitattributes`** keep files UTF-8 with LF line
  endings (the service targets Linux containers) and set the C# style rules
  that the build enforces.

## Requirements

- The **.NET 10 SDK, 10.0.402 or a later 10.0.4xx patch**
  (see `global.json`). It also opens `dotnet-report-service.slnx` (`.slnx` is
  the default solution format from .NET 10 on).

## Versions

### Platform

| Component | Version | Notes |
|-----------|---------|-------|
| .NET | 10 (LTS) — SDK 10.0.4xx band, `net10.0` | Supported until November 2028 |
| Aspire | 13.6.2 | Local development composition; requires the .NET 10 SDK |
| Temporal | .NET SDK 1.21.0; dev server image `temporalio/temporal:1.9.1`; production reference server 1.32.x with PostgreSQL | |
| PostgreSQL | 18 (client Npgsql 10.0.3) | |
| Cache | Valkey 9.1 | Redis protocol — Redis works as well |
| Playwright / Chromium | Playwright 1.63.0 → Chromium 153 | Installed into the PDF base image at build time; the worker image is built on it by the .NET SDK |
| Paged.js | 0.4.3 | Vendored in the render image; no Node.js at runtime |

Package versions appear in `Directory.Packages.props` as the first change
that needs each package adds it; the packages present so far (Aspire hosting
13.6.2, Microsoft.Playwright 1.63.0, and for the tests only: xUnit v3 4.0.2,
coverlet.MTP 10.1.0, Microsoft.AspNetCore.Mvc.Testing 10.0.13, Testcontainers
4.16.0, Npgsql 10.0.3, Aspire.Hosting.Testing 13.6.2) are listed there.

### Container images

| Image | Tag | Used by |
|-------|-----|---------|
| PostgreSQL | `postgres:18.6` | Local composition (metadata database); integration tests |
| Valkey | `valkey/valkey:9.1.2` | Local composition (distributed cache); integration tests |
| Temporal development server | `temporalio/temporal:1.9.1` | Local composition (`server start-dev`); integration tests |
| .NET SDK | `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` | Build stage of the PDF base image |
| ASP.NET Core runtime | `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble` | Base of the PDF base image (and of every .NET image) |
| Local development registry | `registry:3.1.2` (pinned by digest) | Local composition only, on `127.0.0.1:5055`; holds the PDF base image; never shipped |
| PDF base | `report-service-pdf-base` — project-owned, tag `<Playwright version>-<runtime>-noble` (now `1.63.0-10.0.13-noble`) | Base of the render worker image; built from `eng/pdf-base/Dockerfile` |
| Render worker | `report-service-render-worker` — built by the .NET SDK, tag = product version (now `0.2.0-dev`) | Local composition (`render-worker`) |

The three dependency images are pinned in one file,
`src/ReportService.AppHost/composition-pins.json`, read by the composition and
by the integration tests, so both always start the same versions. The digests
of these images as last run are recorded in
[`reference-environment.md`](reference-environment.md).

### Dependency rules

- **Exact versions only** — no ranges or wildcards; container images pinned by
  tag (and by digest in published images).
- **Stable releases only** in `src/` and `tests/`; a spike may try a
  prerelease only if its README says so.
- **Patch and minor updates** are routine changes that must keep the build
  and tests green; **any Playwright update** also requires the rendering
  snapshot tests to pass, because it changes Chromium.
- **Major updates** of a platform component are deliberate decisions, made
  and recorded on their own.
- Every new or upgraded dependency is checked for licence compatibility with
  MIT.

### Product version

- [Semantic Versioning 2.0.0](https://semver.org/): `MAJOR.MINOR.PATCH`, one
  version for the whole repository, declared as `VersionPrefix` in
  `Directory.Build.props`.
- The product stays at **0.y.z** until the first operational version ships as
  **1.0.0**; until then no API or contract stability is promised. While at
  0.y.z, MINOR grows with each delivered milestone or capability, PATCH with
  fixes.
- Each release has an entry in [`CHANGELOG.md`](../CHANGELOG.md)
  ([Keep a Changelog](https://keepachangelog.com/)) and an annotated tag
  `vX.Y.Z`; unreleased changes accumulate under `[Unreleased]`.
- **Between releases**, `Directory.Build.props` also sets `VersionSuffix` to
  `dev`, so every build reports a pre-release version (`X.Y.Z-dev`), which
  Semantic Versioning orders before the release `X.Y.Z`. Assembly and file
  versions stay numeric (`X.Y.Z.0`).
- **Branches:** work happens on `develop`; `main` only receives releases and
  always holds the latest released version.
- **Releasing a version** — only when the maintainer declares it:
  1. on `develop`, remove `VersionSuffix`, move the `[Unreleased]` entries into
     a dated `[X.Y.Z]` section, and commit;
  2. merge `develop` into `main`;
  3. tag that `main` commit with the annotated tag `vX.Y.Z` and push it;
  4. on `develop`, open the next version line (next MINOR by default while at
     0.y.z) with `VersionSuffix` `dev` again.
- Current line: **0.2.0 — in progress** (`0.2.0-dev`); latest release
  **0.1.0** (2026-10-22, tag `v0.1.0`).

## Why this layout

The layout follows conventions documented by the .NET ecosystem rather than
choices of taste:

- **`src/` and a parallel `tests/` folder** — Microsoft's guidance on
  organising application and test projects, which also accepts one test
  project per kind of test
  ([Test ASP.NET Core MVC apps](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/test-asp-net-core-mvc-apps)).
- **`docs/`, `eng/`, tests mirroring `src/`** — the layout of the .NET
  Foundation repositories such as
  [Aspire](https://github.com/microsoft/aspire), which also keeps internal
  test applications apart from samples (here: `spikes/` vs `samples/`).
- **`Directory.Build.props` and central package management** —
  [MSBuild properties reference](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props).
- **`.slnx`** — [`dotnet sln`](https://learn.microsoft.com/dotnet/core/tools/dotnet-sln).
- **Package source mapping** —
  [NuGet package source mapping](https://learn.microsoft.com/nuget/consume-packages/package-source-mapping).
- **Code style in `.editorconfig`** —
  [code-style rule options](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/code-style-rule-options).
