# tests

## Purpose

Automated tests of the product. The folder mirrors [`../src/`](../src/): every
product project has its own test project, plus a few cross-cutting suites
organised by kind of test.

## What goes here

- **Unit and component tests**, one project per `src/` project.
- **Cross-cutting suites by kind:**
  - `ReportService.IntegrationTests` — runs against real dependencies
    (PostgreSQL, Temporal, object storage, Redis).
  - `ReportService.RenderingTests` — renders templates and compares the
    resulting documents (HTML, PDF, XLSX, Markdown).
  - `ReportService.PerformanceTests` — memory and throughput measurements.
- Test fixtures and data owned by a single test project, inside that project.

## What does not go here

- Product code → [`../src/`](../src/).
- Datasets and templates meant for adopters → [`../samples/`](../samples/)
  (tests may read them, but do not own them).
- Exploratory measurements of Stage 0 → [`../spikes/`](../spikes/).

## Naming

- Unit/component: `tests/ReportService.<Area>.Tests/ReportService.<Area>.Tests.csproj`,
  testing `src/ReportService.<Area>`.
- Cross-cutting: `tests/ReportService.<Kind>Tests/`.

Every test project is added to `dotnet-report-service.slnx` under the `tests`
solution folder.
