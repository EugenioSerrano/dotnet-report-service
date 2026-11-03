# Sample templates

One sample template package per pilot dataset (see
[`../datasets/README.md`](../datasets/README.md)):

| Package | Renders | Page | Page totals | Partitioning |
|---------|---------|------|-------------|--------------|
| [`pilot-simple/`](pilot-simple/) | the simple dataset | A4 portrait | off | — |
| [`pilot-grouped/`](pilot-grouped/) | the grouped dataset | A4 portrait | on (`amount`, 2 decimals) | by `groupId`, cut at group boundaries |
| [`pilot-volume/`](pilot-volume/) | the volume dataset | A4 landscape | on (`amount`, 2 decimals) | by `groupId`, cut at group boundaries |

Each package contains:

- `template.yaml` — the manifest: id and version, the owning consumer and the
  `shared` flag, the template engine and its version, the data contract
  (`header-items-v1`, version 1.0, with its schema), the HTML parts, the PDF
  page settings and the fields the printed document is verified against, the raw
  spreadsheet columns with their types, the partitioning and the locale defaults
  (`es-AR`, `America/Argentina/Buenos_Aires`);
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

## Writing a manifest

The manifest is YAML, but only a part of it is read. A manifest is **one
document** whose root is a mapping, and it may hold:

- block and flow mappings and sequences, and comments;
- plain, single-quoted, double-quoted, literal (`|`) and folded (`>`) scalars.

It may **not** hold anchors or aliases (`&`, `*`), explicit tags (`!!str`,
`!foo`, `!`), the `%YAML` or `%TAG` directives, a second document, a key that is
not a single value, the same key twice in one mapping, or the `<<` merge key.
None of those is refused silently: a manifest that uses one is refused with the
file, the line, the column and the rule.

Two further rules follow from how a value is read:

- **Quote anything that only looks like a number.** `version: "1.0.0"` and
  `manifestVersion: "1.0"` are text, and must be quoted to stay text;
  `engineVersion: "7.5.1"` likewise. The same applies to a value that would
  otherwise read as a boolean: `culture: "no"` is the text `no`.
- **Write `true` and `false` as `true` and `false`.** The older spellings
  (`yes`, `on`, `y`) are read as text, so a flag written that way is refused.
- **Write numbers only in the form JSON can hold.** `15`, `2` and `1.50` are
  fine and keep the digits as written; `+15`, `007`, `.5`, `1.`, `0x1F` and
  `.inf` are refused.
- **Quote every field path.** `header: ["$.reportTitle"]` — a path is text.

## What a package says about itself

- `owner` — the consumer the version belongs to, required. A version that names
  no owner, or names one that is not a consumer identifier, is refused.
- `shared` — optional, `false` when absent. `true` lets every consumer use the
  version; until sharing is decided across consumers, a version is visible to
  its owner only.
- `outputs.pdf.verification.anchors` — required for every package that declares
  PDF output: two lists of field paths, `header` and `item`, naming every field
  the parts print **as is and unconditionally**. Either list may be empty, but
  only when written as an empty list. A field printed through a formatter,
  truncated on purpose or printed under a condition is not an anchor.

The part paths of a document live under `parts`:

```yaml
outputs:
  html:
    parts:
      documentStart: html/document-start.html.sbn
      row: html/row.html.sbn
      documentEnd: html/document-end.html.sbn
    styles: [html/styles.css]
```

**Not rendered yet.** No template engine runs in this repository yet, so the
HTML parts have not been rendered or validated. The manifests, schemas and
sample files are checked; the parts are a starting point that the
template-engine measurements render, correct and validate. The parts use
only the `header`, `item`, `group` and `aggregates` context names, write
every text value through `html.escape` and write numbers as received.
