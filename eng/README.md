# eng

## Purpose

Build and engineering assets: everything needed to build, check and package
the repository that is not product code.

## What goes here

- Scripts (build, packaging, local environment helpers).
- Shared MSBuild `.props` / `.targets` imported by the root build files.
- Tool configuration that does not belong at the repository root.
- Container image definitions that need operating-system steps the .NET SDK
  cannot perform ([`pdf-base/`](pdf-base/): the PDF base image with the
  Chromium headless shell), and the small tools they build.

## What does not go here

- Product code → [`../src/`](../src/).
- CI workflow definitions → `../.github/workflows/` (GitHub requires that
  location); they may call scripts that live here.

## Naming

kebab-case script names (`build.sh`, `generate-datasets.ps1`); MSBuild files
named after what they configure (`Packaging.props`); .NET tools
`ReportService.Eng.<Tool>` in a folder of the same name
(`pdf-base/ReportService.Eng.PlaywrightInstaller/`).
