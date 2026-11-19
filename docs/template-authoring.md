# Writing a template

A template is a package: a manifest, the HTML parts its document is rendered
from, and the data schema its reports are checked against. This page covers what
a template author writes and what the service does with it.

## The package

```text
<templateId>/
  template.yaml        the manifest
  schema.json          the data contract of the reports
  html/…               the parts of the HTML document
```

The manifest declares the template's identifier, its version, the engine that
renders it, the engine's version, and the data contract: its profile
(`header-items-v1`), the profile's version (`1.0`) and the package-relative path
of the schema. A version is taken once: the service reads the package at one
commit, keeps it as one write-once object and records what it fixed. A job is
then pinned to the version it named and reads only that stored copy.

## The data schema (the data-contract section)

`schema.json` is one JSON Schema document. It is the **envelope** schema of the
reports: the same document declares the header and the record, and the service
evaluates those two subschemas against every job's data:

- `properties.header` — the document's header;
- `properties.items.items` — one record.

Both must exist, directly or behind a `$ref` inside the document. Nothing else
of the envelope is evaluated at job time: an `items` keyword such as `minItems`
describes the envelope, not a job's data, and an empty `items` array is admitted
whatever the envelope says.

### The dialect and the profile

**Draft 2020-12** (`"$schema": "https://json-schema.org/draft/2020-12/schema"`).
The profile admits:

- the assertion and annotation keywords of the draft that apply to a value —
  `type`, `enum`, `const`, `required`, `properties`,
  `additionalProperties`, `unevaluatedProperties`, `propertyNames`, `items`,
  `prefixItems`, `contains`, `minContains`, `maxContains`, `minItems`,
  `maxItems`, `uniqueItems`, `minLength`, `maxLength`, `pattern`, `minimum`,
  `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`,
  `dependentRequired`, `dependentSchemas`, `format`, `not`, `allOf`, `anyOf`,
  `oneOf`, `if`/`then`/`else`, and the boolean schemas;
- `$schema` at the root, `$ref` to a fragment of the same document, `$defs` and
  `$comment`.

It refuses, by naming the rule and the place inside the schema:

| Rule | What it refuses |
|------|-----------------|
| `size` | A schema larger than 65 536 bytes |
| `schema` | A root without the draft 2020-12 `$schema` |
| `keyword` | `$id`, `$anchor`, `$dynamicRef`, `$dynamicAnchor`, `$recursiveRef`, `$recursiveAnchor`, `$vocabulary`, or any other member the profile does not admit |
| `depth` | A schema nested deeper than 64 schema positions |
| `pattern` | A `pattern` or `patternProperties` key longer than 1 024 characters, or not a valid ECMA-262 pattern |
| `format` | A `format` name outside the nine the service asserts |
| `reference` | A `$ref` that is not a fragment of the document, or one that does not resolve |
| `cycle` | A reference cycle that never descends into the value, so it could never terminate |
| `envelope` | A document without `properties.header` or `properties.items.items` |
| `compile` | A document the evaluator cannot compile |

**No reference leaves the document.** A remote or a file reference is refused at
load time; the service resolves nothing over the network and reads nothing from
the file system.

### Formats

`date`, `date-time`, `time`, `duration`, `email`, `uri`, `uuid`, `ipv4`,
`ipv6`. Each is asserted: a value that does not conform fails the job. An
unknown format name is refused by the profile rather than ignored. At the pinned
validator, `time` requires an offset (`13:45:30Z` is admitted, `13:45:30` is
not).

### Ceilings

| What | Ceiling |
|------|---------|
| The schema document | 65 536 bytes |
| Nesting of the schema | 64 schema positions |
| One pattern | 1 024 characters |
| One match of a pattern | 1 second, then the job ends `TEMPLATE_INVALID` |
| Nesting of a value | 64, then the value cannot be decided |
| One record | 1 MiB (the reader's own limit) |

The evaluator parses the schema with its own bound as well, and that bound is
lower than 64 positions: a schema nested past it is refused as `compile` rather
than admitted, so a document too deep to evaluate never reaches a job. Keep a
schema well inside 24 positions.

A pattern near the one-second limit can decide differently on a slower machine;
a pattern written to match in ordinary time is the safe choice.

## What the service does with it

The header is checked against the header subschema and every record against the
record subschema, record by record, in the pass that reads the snapshot and
before any page is rendered. A value that breaks the schema ends the job
`failed` with `DATA_INVALID` and a diagnostic that names the ordinal, the byte
offset, the field's path relative to the value and the rule — never the value:

```text
the document cannot be used: code=DATA_INVALID reason=schema-violation ordinal=2 offset=1043 path=$.amount keyword=type
```

A schema that cannot be used at all ends the job with `TEMPLATE_INVALID` instead,
in one attempt, and the message names `schema.json` and the rule. The reasons, the
shape of a snapshot and the whole diagnostic are in
[data-contract.md](data-contract.md).

## What the document may print, and what it is checked for

Every printed PDF is compared with the document the template produced: each row
once, in the snapshot's order, with every character of its text. Three
constraints follow from that, and a template that breaks one is refused when its
sample is verified — never silenced at print time.

1. **Print the text; do not generate or hide it.** Text that only CSS adds
   (`content`, list markers, `text-transform`) is not in the document the
   verification compares against; text that CSS hides (`display: none`) is in it
   and is not printed. Both make the verification fail. Hyphenation is the same
   case: it breaks a word into two differently spelled halves.
2. **Declare what you print as it is.** `outputs.pdf.verification.anchors`
   names, as member paths, the header and record fields the template prints
   without changing them:

   ```yaml
   outputs:
     pdf:
       verification:
         anchors:
           header: ["$.reportTitle", "$.customer.name"]
           item: ["$.sequence", "$.description"]
   ```

   A declared field's value is read from the block and looked for in the printed
   text. A value that is missing, null, a boolean, an object or an array is not
   checked; a string or a number is. The list is **required**: a version that
   declares no list cannot be verified and is refused before anything is printed.
   An empty list is honoured — a template that prints no field as it is declares
   `header: []` and `item: []` and relies on the text comparison alone.
   Characters the browser does not paint (soft hyphens, zero-width characters,
   bidirectional controls) inside a declared value make the check fail; keep them
   out of the data a template prints.
3. **Name one font family, and let the page break where it must.** Fonts come
   from the image (Liberation Sans) or from the package; nothing is fetched from
   the network. A row taller than the page continues on the following pages, and
   its amount prints on the page the row started on. A grid cell aligned to the
   bottom of a page cannot be split and is refused when a version is taken.

## The values a template can read

The document is composed in block mode, and the same context is available to
every part of the template:

| Name | What it holds |
|------|---------------|
| `header` | the snapshot's header, as it was received |
| `item` | one record, for `row` (absent from the document parts) |
| `group` | the group a record is in, for `groupStart` and `groupEnd`, when the manifest declares a group key |
| `block` | `index` (0 for the first block), `recordCount`, `firstRecordOrdinal`, `isFirst`, `isLast` |
| `aggregates` | currently empty: no accumulator is declared by the templates this release prints |
| `render` | `culture`, `timeZone` and `clock`, fixed for the whole job |

Values are plain: objects, arrays, strings, booleans and null; a number is the
invariant text of its exact decimal, so `0.10` prints as `0.10` and never as
`0.1`. Every data value must be encoded: end a Scriban output with
`| html.escape` (Liquid and Handlebars encode `{{ }}` for you), and the take
refuses a version that writes a data value without it. No formatting function of
the host is available: format in the template or in the data.

Templates are written for one engine, named by the manifest. This release prints
with the Scriban adapter; the other engines arrive with their own adapter and
the same context.

## What the service checks when it takes a version

A version is read, checked and kept the first time a job references it — never
by the HTTP request, which answers `202` before anything is read. The checks run
in one fixed order and the **first** refusal ends the take; nothing is written
for a refused version, so it can be checked again later, and its message names
the file to fix. A version whose publication record is unchanged is refused
again; the fix is a new version.

| # | Check | What it refuses, and what the message names |
|---|-------|---------------------------------------------|
| 1 | The manifest | the data profile (only `header-items-v1`), `dataContract.version` and `maxDepth`, the required parts, every path the manifest names (outside the package or absent), the declarations this release does not deliver (`aggregates`, `dataContract.verifiedTotals`, `partitioning.groupOrder`), and the PDF page settings: `paper` (`A4` or `Letter`), `orientation` (`portrait` or `landscape`) and four margins in millimetres leaving at least 50 mm printable — the message names `outputs.pdf.<field>` and never its value |
| 2 | `schema.json` | anything outside the template profile: the dialect, a keyword outside the list, `$id`/`$anchor`, a file or remote `$ref`, an unknown `format`, a pattern over 1 KiB, depth over 64, a schema over 64 KiB, both envelope roots missing — with the rule and a pointer into the schema; and a pattern that repeats a part which repeats without limit |
| 3 | `sample.json` | a sample that does not follow the data format, or one that breaks the schema: the message names the first violation's path and rule, never a value. The whole check has 10 seconds |
| 4 | the anchors | an `outputs.pdf.verification.anchors` path the schema does not declare at the header or the record root |
| 5 | the parts' syntax | a syntax error in a part or an included file, with the file and, when the engine gives it, the line and column — the engine's own message is never shown |
| 6 | includes and assets | an include that is not a fixed name, points outside the package, names no file the render loads (in Scriban, an include must be a declared part), carries another engine's extension or makes a cycle; a stylesheet or asset the manifest or a part refers to and the package does not hold; a `data:` URL outside an image |
| 7 | the lint | a data output that is not escaped (Scriban: end it with `\| html.escape`), a raw output, a forbidden function, a data value inside a script, style, URL or event-handler attribute or an unquoted attribute value, and a Liquid range that is not fixed or has more than 10 000 values |
| 8 | the engine version | an `engineVersion` whose major differs from the engine this deployment renders with |
| 9 | the printed page totals | a version that turns `outputs.pdf.pageTotals.enabled` on: this build prints no totals, and a report with silently missing numbers is worse than a refusal. The message names the declaration, never its value; the fix is to print the totals in the template or turn them off |
| 10 | the font family of the detail text | detail text whose text-bearing cells name no font family, or that no declaration reaches at all. The message names the part and the stylesheet and line of the declaration that sets only a generic keyword; the fix is to name the family the report is measured with, `"Liberation Sans", sans-serif`, on a rule that applies to the detail text |
| 11 | the grid cell that may split | a row laid out as a grid whose direct child is aligned to the bottom by `align-self: end`, `align-items: end`, `place-self` or `place-items`, while nothing forbids that cell to break across pages. The message names the part, the cell and the rule that aligns it; the fix is to align the cell to the top |

**The three PDF rules in product words.**

- **Page totals.** A version that asks for printed page subtotals is refused
  because this service does not print them yet. The fix is to print the totals in
  the template or declare them off:
  `template.yaml: the template declares page subtotals (outputs.pdf.pageTotals), which this service does not print yet. Print the totals in the template or turn them off.`
- **Font family.** The detail text must name the typeface it is printed in: a
  stylesheet that sets only `sans-serif` and no real family leaves the page in a
  font the measurements do not cover. Name one family, with a generic fallback,
  on a rule that reaches the detail text:
  `html/row.html.sbn: the detail text names no font family; html/styles.css line 11 sets only the generic keyword "sans-serif" on the rule that governs its cells. Name the family the report is measured with, on a rule that applies to the detail text.`
- **Grid cell.** A cell of a grid row that may break across pages must not be
  aligned to the bottom: its amount would print on a later page, away from the
  record it belongs to. `vertical-align` has no effect in grid layout and is not
  refused. Align the cell to the top:
  `html/row.html.sbn: the cell class "amount" may split across pages and is aligned to the bottom in the grid layout (html/styles.css line 11, align-self: end). Align it to the top.`

**How the lint and the includes are read.** The escaping and sandbox rules and the
include names are read from each file's text, for all three engines; the engine's
own parser still decides syntax. The scan refuses an expression it cannot classify
— it never ignores one — and what a text scan cannot see (a context assembled
across several parts, an include whose name a helper builds) is left to template
review.

**One known limitation.** A page setting of the wrong JSON kind — `marginMm.top:
"15"`, a string — is refused as a generic `TEMPLATE_INVALID` by the manifest's
typed view before the field-level check runs, so that one case's message does not
name the field. Every value-level fault is named: an absent member, an unknown
paper or orientation, a negative margin, and a printable area under 50 mm name
`outputs.pdf.<field>`.

A refused version logs the reason and a stable rule name, and the job that
referenced it ends `failed` with the code (`TEMPLATE_INVALID` or
`UNSUPPORTED_CAPABILITY`). The authoring guide is the contract: if a package
passes the take, the render can parse it.
