# tests

## Purpose

Automated tests of the product. The folder mirrors [`../src/`](../src/): every
product project gets its own unit/component test project, plus a few
cross-cutting suites organised by kind of test. Tests use xUnit v3 on
Microsoft.Testing.Platform (selected in [`../global.json`](../global.json));
each test project is a small executable that `dotnet test` runs.

## Test levels and their commands

Each level is run by **one command** from the repository root. The command is
the test runner in [`../eng/test/ReportService.Eng.TestRunner/`](../eng/test/ReportService.Eng.TestRunner/),
which knows the projects of each level, runs them with `dotnet test` in
Release, and exits non-zero naming the project (or the coverage floor) that
failed.

| Level | Projects | May reach | Command |
|-------|----------|-----------|---------|
| **Hermetic** — unit and component tests | `ReportService.Api.Tests` (one `ReportService.<Area>.Tests` per product project) | nothing outside the test process; files only under a temporary directory unique to each test | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- hermetic` |
| **Integration** — one real dependency at a time | `ReportService.IntegrationTests` | PostgreSQL, Valkey and the Temporal development server, each in its own container | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- integration` |
| **End-to-end** — the whole local composition | `ReportService.EndToEndTests` | the composition of [`../src/ReportService.AppHost/`](../src/ReportService.AppHost/), started from the test | `dotnet run --project eng/test/ReportService.Eng.TestRunner -- e2e` |

- The **hermetic** level needs only the .NET SDK — no container runtime. It
  is run with every change.
- The **integration** and **end-to-end** levels need a container runtime
  (Docker); the end-to-end level also needs what the composition needs (the
  local development registry and BuildKit, see the
  [README](../README.md)). Their tests are compiled and checked with every
  change, and run in a separate integration cycle against the real
  dependencies.
- The runner's `integration` command is the supported entry point for the
  integration level, because it switches off the Testcontainers resource
  reaper (the tests remove their own containers); running the project
  directly with `dotnet test` would start the reaper's extra container.

### Run evidence

Every run writes its evidence under `artifacts/test-results/<yyMMdd-HHmm>/`
(git-ignored; a second run in the same minute gets `-2`, `-3`, …):

- `<project>.log` — the full console output of each test project;
- `run.json` — the level, start and finish times, the product commit and
  whether the working tree was clean, the reference-environment revision, each
  project with its arguments and result, and — for the hermetic level — the
  coverage options and results; for the integration and end-to-end levels, the
  identities (digests) of the images used and, for integration, the suite's
  containers before and after the run;
- hermetic level only: `<project>/coverage.cobertura.<timestamp>.xml` (one
  Cobertura report per project) and `coverage-summary.txt`.

## Coverage

- Measured on the **hermetic level only**, with `coverlet.MTP`, per product
  assembly.
- Floors: **80 % line / 70 % branch** for every product assembly that has a
  unit/component test project; **90 % / 85 %** for the integrity-critical
  modules. They apply from the change that creates the assembly's test
  project. A floor not met fails the hermetic command like a failing test,
  and the runner names the assembly and the figure.
- The floors live in one table in the runner
  (`eng/test/ReportService.Eng.TestRunner/CoverageFloors.cs`). **A new
  unit/component project adds its product assembly's row there in the same
  change**; an assembly with a row that is missing from the report fails the
  run. An assembly with no coverable line yet is reported `n/a`.
- Rates are computed from the report's line and branch counts and are never
  rounded up.
- **What is excluded, and how.** The runner passes the coverage filters on
  the command line: only `ReportService.*` assemblies are measured; the
  composition host (`ReportService.AppHost`), the engineering and sample
  tools, this folder's support library, composition roots (`Program.cs`) and
  generated files (`obj/`) are excluded; generated code marked with
  `GeneratedCode` or `CompilerGenerated` is excluded by the tool's defaults.
  A justified local exclusion is written on the code as
  `[ExcludeFromCodeCoverage(Justification = "<one-line reason>")]`.
- **Do not add a coverage section to a `testconfig.json`** unless it repeats
  every default exclusion of the tool and every filter of the runner: such a
  file replaces the tool's defaults instead of adding to them.

## Rules every test follows

- **Deterministic:** the same result on every run; no dependence on test
  order; cultures and time zones explicit when formatting or parsing; no wall
  clock in the code under test.
- **Hermetic tests reach nothing outside the process:** no network, database,
  queue or shared environment.
- **No automatic retries.** A test that passes only when re-run is a defect
  to fix, never something to re-run until green. Integration tests may poll a
  dependency until it is ready, within a deadline — never repeat an assertion.
- **Every integration test class owns its containers** and removes them
  through its own handles; nothing is pruned. Container names start with
  `report-service-it-`.
- Test names describe behaviour.

## What goes here

- **Unit and component tests**, one project per `src/` project.
- **Cross-cutting suites by kind:**
  - `ReportService.IntegrationTests` — runs against real dependencies
    (PostgreSQL, Valkey, Temporal; object storage when it exists).
  - `ReportService.EndToEndTests` — starts the whole local composition.
  - `ReportService.RenderingTests` (planned) — renders templates and compares
    the resulting documents (HTML, PDF, XLSX, Markdown).
  - `ReportService.PerformanceTests` (planned) — memory and throughput
    measurements.
- **`ReportService.Testing`** — a support library shared by the test
  projects (repository paths, the composition's pinned images, a bounded wait,
  in-memory log capture, the container helper). It is not a test project, and
  only test projects reference it.
- Test fixtures and data owned by a single test project, inside that project.

## What does not go here

- Product code → [`../src/`](../src/). Nothing in `src/` references `tests/`.
- Datasets and templates meant for adopters → [`../samples/`](../samples/)
  (tests may read them, but do not own them).
- Exploratory measurements of Stage 0 → [`../spikes/`](../spikes/). Nothing
  in `src/` or `tests/` references `spikes/`.

## Naming

- Unit/component: `tests/ReportService.<Area>.Tests/ReportService.<Area>.Tests.csproj`,
  testing `src/ReportService.<Area>`.
- Cross-cutting: `tests/ReportService.<Kind>Tests/`.
- Shared support: `tests/ReportService.Testing/`.

Every test project is added to `dotnet-report-service.slnx` under the `tests`
solution folder, and to its level in the test runner.
