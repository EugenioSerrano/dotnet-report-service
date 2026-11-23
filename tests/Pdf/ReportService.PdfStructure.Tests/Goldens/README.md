# Golden PDFs — hermetic PDF structure verification

These are **committed golden PDFs** the hermetic PDF structure verification
(`ReportService.PdfStructure.Tests`) reads **in-process** with the product's own
reader (PdfPig, `PdfWordReader`). The hermetic level needs **no browser, no
engine and no container**: it reads these committed binaries and checks block/row
order, the global "Page X of Y", row coverage against the source fixture and the
words of each page (ADR-039 §3.5.8).

## What is committed, per golden

Each golden `<name>` is three files, always committed together:

| File | What it is |
|------|------------|
| `<name>.pdf` | the golden document (binary), produced once by the product rendering path |
| `<name>.fixture.json` | the **source fixture snapshot** it was printed from, so the structure check compares against the source, never the engine's own output |
| `<name>.expectation.json` | the **page/frame declaration** (paper, margins, columns) — the same shape the cycle's oracle reads |

`SHA256SUMS` holds the content hash of each committed `.pdf`. **A golden is never
committed without its paired source fixture and declaration.**

## The set

| Golden | Fixture | Package (markup, native path) | Covers |
|--------|---------|-------------------------------|--------|
| `table-b100-multipage` | `B100` | `integrity-table` | a representative multi-page table |
| `table-t300u-tall-split-multipage` | `T300u` | `integrity-table` | a multi-page document with a tall row that splits across pages |
| `table-ge30-group-edge` | `GE30` | `integrity-table` | the group-edge fixture (a multi-line first row) |

All three exercise the **native** layout path with the `integrity-table` markup
(the known-positive combination; `integrity-grid` on the chosen fixtures is
refused by the product verification with `text-extra`, so it is not a valid
golden). A golden for the **Paged.js** layout path is a documented follow-up: the
available test packages do not cleanly separate it, and committing an unverified
golden is never done.

## How they were generated (one-off, Docker)

The goldens are produced **once** through the existing rendering path on the PDF
base image — the only Docker step, and never part of the V-Bounce:

1. Prepare the base image and local registry:
   `dotnet run --project src/ReportService.AppHost -c Release -- --prepare-render-worker-image`
2. Render each fixture with its package through the rendering host image
   (`report-service-rendering-host:0.10.0-dev`, built on the PDF base
   `localhost:5055/report-service-pdf-base:1.63.0-11.0.0-resolute`), keep the
   printed `.pdf`, and pair it with the fixture snapshot
   (`ReportService.Testing` fixture library) and the package declaration
   (`ReportService.Testing/OracleExpectations/<package>.json`).
3. Render each fixture **twice** and confirm the two renders are the **same
   document structure** read in-process (the determinism check). The PDF bytes
   are **not** identical because the engine stamps a per-render `/CreationDate`
   and `/ModDate`; the hermetic structure verification reads structure, not
   bytes, so this does not affect it.

A golden that cannot be generated with the **same structure** on two renders is a
stop condition (ADR-039 / SPEC §15): the structure verification needs a stable
golden.

## When a golden must change

Only a rendering-touching Bolt updates a golden, deliberately, showing the diff
in its MEM and validating the new render in the integration/rendering cycle
(ADR-039 §3.5.8 item 4).
