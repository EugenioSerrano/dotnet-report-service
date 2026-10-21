# Repository layout

This page explains where everything lives in the repository, how projects are
named, and which conventions the layout follows. Each top-level folder also
has its own `README.md`.

## Top-level structure

```text
dotnet-report-service/
├── src/                       Product source — one folder per project
├── tests/                     Automated tests, mirroring src/
├── samples/                   Templates and datasets for adopters
├── docs/                      Documentation (this page)
├── eng/                       Build and engineering assets
├── spikes/                    Stage 0 experiments (not part of the product)
├── dotnet-report-service.slnx Product solution
├── Directory.Build.props      Build settings shared by every project
├── Directory.Packages.props   Central package versions
├── NuGet.config               Package sources (nuget.org only)
├── .editorconfig              Code style and whitespace rules
├── .gitattributes             Line endings (LF) and binary files
├── README.md
└── LICENSE
```

## Naming rules

| What | Rule | Example |
|------|------|---------|
| Product project | `src/ReportService.<Area>/ReportService.<Area>.csproj` | `src/ReportService.Workers.Pdf/` |
| Root namespace | Same as the project name | `ReportService.Workers.Pdf` |
| Unit/component tests | `tests/ReportService.<Area>.Tests/` — one per product project | `tests/ReportService.Workers.Pdf.Tests/` |
| Cross-cutting test suites | `tests/ReportService.<Kind>Tests/` | `tests/ReportService.IntegrationTests/` |
| Sample template | `samples/templates/<template-id>/` | `samples/templates/invoice-energy/` |
| Spike | `spikes/NN-<topic>/` | `spikes/01-streaming-parser/` |
| Documentation page | kebab-case `.md` in `docs/` | `docs/template-authoring.md` |

Product and test projects are added to `dotnet-report-service.slnx` under the
`src` and `tests` solution folders (`dotnet sln add <path> --solution-folder src`).
Spikes are never added.

## Shared configuration

- **`Directory.Build.props`** is imported automatically by every project below
  the root. It enables nullable reference types and implicit usings, treats
  warnings as errors, enforces code style during build, and sets the package
  metadata (MIT licence, repository URL). It sets **no version** — no target
  framework, language version, analysis level or SDK.
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

- A .NET SDK of version 9.0.200 or later to open and build
  `dotnet-report-service.slnx` (`.slnx` is the default solution format from
  .NET 10 on). Exact SDK and framework versions are fixed when the first
  projects are added.

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
