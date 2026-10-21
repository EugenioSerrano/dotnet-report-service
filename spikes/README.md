# spikes

## Purpose

Technical experiments of the project's first stage (Stage 0): small,
throwaway programs that measure whether a technique or library works under
volume — the streaming JSON parser, the template engines, pagination with
Paged.js and Chromium, PDF merge and stamping, XLSX writers, Markdown
conversion. The **code is disposable; the measurements are kept**, because
dotnet-report-service publishes its measured figures instead of promising an
SLA.

## What goes here

- One folder per spike: `spikes/NN-<topic>/`, containing the spike's code and
  a `README.md` with four sections:
  - **Hypothesis** — what the spike tries to prove or disprove;
  - **How to run** — exact commands and inputs (pilot datasets from
    [`../samples/datasets/`](../samples/));
  - **Results** — the measured numbers and the environment they were taken on;
  - **Conclusion** — what it means for the library or limit decision.

## What does not go here

- Product code → [`../src/`](../src/). Nothing in `src/` or `tests/` may
  reference a spike; a technique that proves itself is re-implemented in the
  product, not copied from here.
- Spikes are **not** added to `dotnet-report-service.slnx` and are never
  packaged or shipped.

## Build settings

Spikes inherit the repository conventions, except that compiler warnings do
not fail the build and nothing is packable — see
[`Directory.Build.props`](Directory.Build.props).

## Naming

`spikes/NN-<topic>/` with a two-digit sequence and a kebab-case topic, for
example `spikes/01-streaming-parser/`, `spikes/03-pdf-layout/`.
