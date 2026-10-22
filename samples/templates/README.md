# Sample templates

One sample template package per pilot dataset (see
[`../datasets/README.md`](../datasets/README.md)):

| Package | Renders | Page | Page totals | Partitioning |
|---------|---------|------|-------------|--------------|
| [`pilot-simple/`](pilot-simple/) | the simple dataset | A4 portrait | off | — |
| [`pilot-grouped/`](pilot-grouped/) | the grouped dataset | A4 portrait | on (`amount`, 2 decimals) | by `groupId`, cut at group boundaries |
| [`pilot-volume/`](pilot-volume/) | the volume dataset | A4 landscape | on (`amount`, 2 decimals) | by `groupId`, cut at group boundaries |

Each package contains:

- `template.yaml` — the manifest: id and version, the template engine and
  its version, the data contract (`header-items-v1`, version 1.0, with its
  schema), the HTML parts, the PDF page settings, the raw spreadsheet columns
  with their types, the partitioning and the locale defaults (`es-AR`,
  `America/Argentina/Buenos_Aires`);
- `schema.json` — the JSON Schema (draft 2020-12) of the dataset's envelope;
- `sample.json` — a small sample written by the dataset generator's
  `samples` command (25, 40 and 30 records); `verify` checks that it
  regenerates byte for byte;
- `html/` — the template parts (`document-start`, `row`, `document-end`,
  plus `group-start` and `group-end` for the grouped packages) and
  `styles.css` with the page size and margins of the manifest.

**One engine per package.** Every package declares exactly one template
engine; these three are written for Scriban 7.5.1. Scriban is **not** a
default engine of the service — a package always names its engine. Handlebars
and Liquid variants of the same templates are added by the template-engine
measurements.

**Not rendered yet.** No template engine runs in this repository yet, so the
HTML parts have not been rendered or validated. The manifests, schemas and
sample files are checked; the parts are a starting point that the
template-engine measurements render, correct and validate. The parts use
only the `header`, `item`, `group` and `aggregates` context names, write
every text value through `html.escape` and write numbers as received.
