# src

## Purpose

Product source: every project that is built, shipped or deployed as part of
dotnet-report-service — the API, the workers, the workflows, the template
engine adapters, the storage providers and the Aspire AppHost.

## What goes here

- One folder per project, containing that project only.
- Production code and its embedded resources (scripts, packaged assets the
  runtime needs).

## What does not go here

- Tests → [`../tests/`](../tests/).
- Sample templates and datasets for adopters → [`../samples/`](../samples/).
- Experiments and throwaway prototypes → [`../spikes/`](../spikes/).
- Build scripts and tooling → [`../eng/`](../eng/).

## Naming

`src/ReportService.<Area>/ReportService.<Area>.csproj`, root namespace
`ReportService.<Area>` (for example `ReportService.Api`,
`ReportService.Workers.Pdf`, `ReportService.Templates.Scriban`). Every project
is added to `dotnet-report-service.slnx` under the `src` solution folder.
Shared build settings come from the repository's `Directory.Build.props`;
package versions are declared only in `Directory.Packages.props`.
