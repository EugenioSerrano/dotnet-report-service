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

## The local loop: scaffold, preview, then publish

A version is taken the first time a job references it — that is when the
service checks the package and renders its sample. The local loop shows you the
same composed HTML and the same printed, verified PDF before that
publish-and-wait cycle, using the service's own checks and renderer: nothing
here is a second implementation of what the take does.

### Prerequisites

The .NET 11 SDK. The scaffold and the static checks need nothing else; to print
the PDF you also need the Chromium build Playwright 1.63.0 downloads — the same
build the PDF base image installs. Install it once from the tool's build
output, from the repository root:

```text
pwsh samples/template-authoring/ReportService.Samples.TemplateAuthoring/bin/Release/net11.0/playwright.ps1 install chromium
```

### Scaffold

```text
dotnet run --project samples/template-authoring/ReportService.Samples.TemplateAuthoring -- scaffold --engine <scriban|handlebars|liquid> --id <template-id> [--version <version>] [--out <directory>]
```

`--engine` names one of the three engines (case-insensitive) and is written to
the manifest; `--id` follows the template identifier rule (lower-case letters,
digits, `.`, `_` and `-`, beginning and ending with a letter or a digit, at most
128 characters); `--version` defaults to `1.0.0`.

`--out` names the package directory itself. Use
`--out samples/templates/<template-id>` for a package that lives in this
repository, or `--out artifacts/<template-id>` for a scratch one. Without it the
tool writes `<current directory>/<template-id>`; it refuses a directory that
already holds files.

The scaffold writes `template.yaml`, `schema.json`, a deterministic
`sample.json`, the three HTML parts in the engine's syntax
(`html/document-start.html.<ext>`, `html/row.html.<ext>`,
`html/document-end.html.<ext>`) and `html/styles.css`. The manifest names the
engine you chose, its part extension and the engine version this build actually
runs — never a constant — and the package already passes the take's static
checks: the command prints
`scaffolded <engine> <id> <version> at <path> checks=pass` and exits `0`.

### Render

```text
dotnet run --project samples/template-authoring/ReportService.Samples.TemplateAuthoring -- render <package-folder> [--out <directory>]
```

`render` runs the take's thirteen static checks on the package, in the take's
order, then renders the package's own `sample.json` once through the product's
sample renderer: the same engine adapter, fragment composition, Chromium print
and post-render verification a job uses. It writes two files into
`artifacts/template-preview/<id>/<version>/` under the current directory by
default (git-ignored when that is the repository root), or into `--out`:

- `<id>-<version>.html` — the document that was printed, with the page rule
  appended;
- `<id>-<version>.pdf` — the printed document, after the product's verification
  accepted it.

The two files are regenerated on every run; no other file is touched.

### Reading a refusal

A package the checks refuse prints the take's own sentence and exits `1`:

```text
<code>: <package file>: <what is wrong> <what to fix>
```

For example, a field declared as an anchor that the row does not print:

```text
TEMPLATE_INVALID: template.yaml: $.customer.id is declared as an anchor but the template does not print it as it is. Print the field as it is, or remove it from the anchor list.
```

The sentence names the package-relative file, what is wrong and what to fix; it
never prints template text or a data value, and neither does any other line the
tool writes. The exit codes are `0` for a package that rendered and verified,
`1` for a package the checks or the sample render refused, `2` for an invalid
command line (an unknown engine, an invalid identifier or version, a missing
package, or a target that already holds files) and `3` when the render could not
run — no browser, a timeout, a crash. A `3` says nothing about the package:
install or repair the browser and retry.

### The limits

The local loop runs the thirteen static checks and the ordinary sample render.
It does not run the zero-record sample render or the at-bound record check;
those run at publish time, in the take. A local green is necessary, not
sufficient.

The printed page comes from the Chromium installed on your machine. The
service's image is the reference: a different browser build or other fonts can
print differently, which is why the package's fonts and page settings are the
contract, and why `"Liberation Sans", sans-serif` with the page size and
margins `html/styles.css` declares is what the measurements cover.

### The examples

`pilot-simple` (Scriban), `pilot-handlebars` (Handlebars) and `pilot-liquid`
(Liquid/Fluid) under `samples/templates/` are the same report in the three
engines: copy the one written in the engine you want and change it.

`invoice-batch` and `invoice-batch-two-up` (Liquid/Fluid) are one batch of
invoices printed in two layouts — one invoice per page, and two per page; see
[Printing a batch of invoices](#printing-a-batch-of-invoices).

### Then publish

Nothing is uploaded through the tool. A version is taken by the service the
first time a job references it; the take and its checks are documented in the
sections of this page around this one.

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
refuses a version that writes a data value without it. A date and a number are
printed through the presentation functions below, so the job's culture and zone
reach them; the engines' own formatters are refused when a version is taken.

Templates are written for one engine, named by the manifest: Scriban, Handlebars
or Liquid. The three are peers — none is a default — and the same context is
available to each.

## Printing a batch of invoices

A batch of invoices — many invoices printed by one job, in one document — needs
no special mode. The job's data is an ordinary `header-items-v1` document in
which **each record is one invoice**: its own fields, its lines as a nested
array, its totals, and the values its graphics and codes are drawn from. The
header describes the batch. Every record is checked against the record schema,
printed once and in the order received, and the 1 MiB record bound applies to
each invoice.

The examples `samples/templates/invoice-batch` and
`samples/templates/invoice-batch-two-up` are the same package in two layouts;
copy one and change it.

### The layout is the template's

The service has no invoice-per-page rule. Where a page breaks is decided by the
template's own stylesheet: `invoice-batch` starts every invoice after the first
on a new page (`.invoice + .invoice { break-before: page; }`), and
`invoice-batch-two-up` keeps each invoice whole and starts a new page before
every odd invoice after the first, so two share a page. Any other arrangement
your layout produces is printed as it is.

### Lines and other nested values

A record's nested arrays are read in the row part with the engine's loop — in
Liquid, `{% for line in item.lines %}…{% endfor %}`. Totals are carried in the
data: a template does no arithmetic, so the consumer computes the subtotal, the
tax and the total and the template prints them.

### Graphics and codes are computed before the job

A graphic or a code that depends on an invoice — a barcode, a QR, a small
chart — is **computed by the consumer before the job** and carried inside the
record. The service never runs code written in a template: there is no script
in a package, and an expression has no effect beyond printing a value. The ways
that work today:

- **Geometry printed as SVG elements.** The record carries the shapes — for a
  barcode, the position and width of each bar — and the row prints them inside
  an inline `<svg>`, each value in an ordinary quoted attribute:

  ```text
  <svg viewBox="0 0 {{ item.barcode.width }} {{ item.barcode.height }}">{% for bar in item.barcode.bars %}<rect x="{{ bar.x }}" y="0" width="{{ bar.w }}" height="{{ item.barcode.height }}"></rect>{% endfor %}</svg>
  ```

  The examples print a Code 128 barcode of the invoice number this way.
- **Text in a font the package ships.** A barcode font declared with
  `@font-face` and packaged with the template prints the record's text as bars.
  The examples do not demonstrate it, and the font's licence is yours.

**What the take's current rule refuses:** a data value written inside `src`,
`srcset`, `href`, `xlink:href`, `poster`, `data`, `action`, `formaction`,
`style` or an event attribute. So a data URI carried in the record cannot be
the source of an `<img>` — `<img src="{{ item.qr }}">` is refused when the
version is taken, naming the part and the `"src"` attribute.

A code that depends on the final page rather than on an invoice is not
precomputed: the template declares it and the service applies it to every page.
It is documented with that feature.

### Showing or hiding on a flag

A template may show or hide a field, or a whole graphic, on a value the consumer
put in the data — in Liquid, `{% if item.flags.showBarcode %}…{% endif %}`.
That is a choice over data, not a computation. Two consequences:

- hide with the condition, never with CSS: text hidden by `display: none` is in
  the composed document but not on the page, and the verification fails;
- a field printed only on a flag is never an anchor — an anchor is printed as it
  is and unconditionally.

At the take's current rule, `if` and `for` over record data are accepted in
Liquid templates; a Scriban template that writes `{{ if item.… }}` or
`{{ for … in item.… }}` is refused by the lint as an unescaped data output,
which is why the examples are written in Liquid.

### What is yours

The **correctness of the data, and of every graphic and code printed from it**,
is the adopter's: test the reports you produce with your own testers and users.
The service verifies the printed text against the composed document and the
declared anchors — the fields that feed a code can be declared as anchors so a
missing or altered value is caught — and it never decodes, scans or analyses a
printed symbol: a barcode that encodes the wrong value, or none, does not fail
the job.

## Presenting a date or a number

Dates and numbers are written in the culture and the time zone the job was
accepted with, and the report's "now" is that job's own acceptance instant. A
template cannot choose otherwise: the functions below take no culture, no time
zone and no clock, and none of them reads the machine's clock or culture. The
names and the arguments are the same in every engine.

| Function | Arguments | Result |
|----------|-----------|--------|
| `format_number` | a value, a numeric format | the number in the job's culture and in that format |
| `format_date` | a date or an instant, a date/time format | the text in the job's culture and zone |
| `num_gt`, `num_ge`, `num_lt`, `num_le`, `num_eq`, `num_ne` | two values | a boolean, for a condition |
| `num_sign` | a value | `"-1"`, `"0"` or `"1"`, **as text** |

```text
Scriban      {{ format_number item.amount "N2" }}       {{ format_date item.date "dd/MM/yyyy" }}
Handlebars   {{format_number item.amount "N2"}}          {{format_date item.date "dd/MM/yyyy"}}
Liquid       {{ item.amount | format_number: "N2" }}     {{ item.date | format_date: "dd/MM/yyyy" }}
```

- **The formats are .NET's**, standard or custom and at most 32 characters:
  `N2`, `C2`, `F0`, `P1`, `#,##0.00` for numbers; `d`, `D`, `dd/MM/yyyy`,
  `dd MMM yyyy HH:mm` for dates. Rounding is the format's own.
- **A date is ISO 8601 text.** A value with a time (`2026-10-29T18:30:00Z`, a
  `±HH:mm` offset, or no offset, which means UTC) is an instant and is shown in
  the job's zone. A date-only value (`2026-10-29`) is a calendar date and is
  printed unchanged in every zone — converting it would print the day before
  west of Greenwich.
- **`num_sign` is for printing only.** Every non-empty text is true in all three
  engines, `"0"` included: a sign used as a condition is one of the six
  comparisons, which answer a real boolean. `num_sign` prints; it never decides.
- **A formatted field is never an anchor.** `outputs.pdf.verification.anchors`
  names only the fields printed as they are; a formatted value is a different
  text from the value it came from.
- **A value that cannot be presented is refused, never printed wrong.** A number
  with no exact decimal, a date that is not ISO 8601, or a format the runtime
  rejects ends the job `failed` — `DATA_INVALID` with `number-out-of-range` or
  `type-mismatch`, or `TEMPLATE_INVALID` with `format-string` — naming the
  record and the function and never the value. A number the report must show
  exactly as received is printed without a function.
- **An engine's own formatter is refused when a version is taken** (the list of
  forbidden functions includes them). A format applied through an engine's
  native filter would read the worker's own culture, so a version that reached
  printing with one is outside what this service can promise; that residual is
  why the take refuses them rather than warning.

The job's accepted culture and zone also reach the `render` values a template can
print: `render.culture`, `render.timeZone` and `render.clock` (an instant). The
package's `localeDefaults` apply only when a job was accepted without a culture
or a zone; they are never re-read at render, so a newer version of a package
cannot change the presentation of a job already accepted.

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
| 12 | the sample render | a sample that cannot render, print or verify with the engine the version declares, or a report with zero records that prints nothing. What it refuses, and the sentence an author reads, are in the section below |
| 13 | the record at the row bound | a version whose record at 256 KiB does not print every character on the page. The message names the file, the field's path and the cause; the fix is to print the field as it is. What it refuses is in the section below |

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

## The sample the service renders before it takes the version

The twelfth check does not read the package: it **renders** it. The
version's own `sample.json` is composed to HTML, laid out and printed to PDF with
the engine the version declares, verified with the product's own verification,
and then rendered once more with the same header and **zero records**. Before
this check passes, nothing of the version is kept.

- **`sample.json` is the data the version is tried on.** It follows the same
  `header-items-v1` envelope a job sends, and the third check has already
  validated it against the package's `schema.json`. It is the version's own
  sample - not a fixture of the service, and never a job's data.
- **One block.** A sample is one block: at most 2 000 records and at most 1 MiB
  of record content. A larger sample is refused before anything is composed -
  `sample.json: the sample holds N records and M bytes of record data, where one
  block holds at most 2 000 records and 1 MiB. Reduce the sample to a single
  block's worth of records.`
- **A sample the reader rejects is a refusal, not a crash.** A `sample.json` that
  does not follow the data format (for instance, no header) is refused in the
  same form: `sample.json: the sample does not follow the data format
  (<reason>). Correct the sample.`
- **The two renders.** The ordinary sample must print and verify - the page must
  carry the values the parts composed, the anchors the manifest declares, and the
  rows in the sample's order - and the empty sample must still print the
  report's header. A template that prints nothing at all for zero records is
  refused `sample-empty` here, at take, not at a consumer's job.
- **What a refusal names.** The package-relative file, what is wrong and what to
  fix; the engine's own message is never shown, because it carries template
  text. The families are `sample-engine-error`, `sample-engine-limit`,
  `sample-output-limit`, `sample-over-bound`, `sample-format`,
  `sample-not-printed` and `sample-empty`.
- **It is bounded.** Each of the two renders has a 20-second deadline
  (`Templates:Sample:RenderBudgetSeconds`, validated to at most 22 s), well below
  the take's own 240-second deadline. A render that does not finish, or a browser
  that goes away, is **not** a refusal of the version: the take is retried, and
  the job ends with `RENDER_TIMEOUT` only when its attempts are exhausted.
- **It is observable.** Each take writes one line under
  `ReportService.TemplateChecks.Sample` - `check=sample-render`, the rule, the
  file, the engine and the figures - and a `template.sample` span on the
  process's own `ReportService` source. Neither carries a value of the sample.

Anchors are part of this contract: an `outputs.pdf.verification.anchors` path is
a field the template must print **as it is and unconditionally**. A header field
the document parts do not print is refused at take:
`template.yaml: $.customer.id is declared as an anchor but the template does not
print it as it is. Print the field as it is, or remove it from the anchor list.`

## The record at the row bound

The thirteenth and last check prints the version once more, with a record whose
largest text field is exactly **256 KiB of UTF-8** - the most the service accepts
in one value of a detail record - through the same parts, stylesheet, printer and
page checks a job uses. A row that size does not fit a page: it is split across as
many pages as it needs, and every character of it must reach them, in order, with
no blank page inside the row. A version is taken only if that print carries what
the parts composed and, when the template declares the field as an anchor, exactly
the value it was given.

- **Where the value goes.** The check reads the version's own `schema.json` and
  tries **every place the data contract can carry text**: a field of the record
  itself, a field inside an object or an array, a field an open record admits (its
  additional, patterned and unevaluated members included), the text side of a
  field that also allows null, and a field the contract reaches only through a
  combined, conditional, dependent or fixed-position shape. A field the template
  declares as an item anchor is tried first. The first place that can hold 256 KiB
  carries the value; the rest of the record is the sample's own first record or,
  when the sample carries none, a record built from the contract with every
  declared field at a valid value.
- **What a refusal means.** The version is not taken: `TEMPLATE_INVALID` with the
  rule `at-bound-sample`, naming the package file, the field's path and the cause
  (`text-dropped` when the page lost characters the parts composed,
  `anchor-mismatch` when the anchored field is not printed as it is, `blank-page`
  when a page inside the row carries nothing), and nothing of the version is kept.
  Fix the part - a fixed-height or clipped cell, an overflow that hides the text, a
  filter that shortens the value, a rule that pushes the row off the page - so
  that the field prints as it is.
- **When a field is not a place.** A field whose own limit is shorter than 256 KiB
  is skipped, and so is one whose own shape cannot hold it: a date, a time, a
  timestamp, a uuid, an address (ipv4/ipv6), an e-mail address or a URI cannot
  hold 256 KiB and is never the field the value is measured in, while a duration
  can be, and is then built to that shape. When **no** place of the record can
  carry 256 KiB, the version is taken: nothing can be lost at a size the contract
  does not admit.
- **A sample with no records.** A version whose sample carries no detail record is
  still checked: the record is built from the data contract, the print runs, and
  the version is taken when it passes - never refused for the sample it is allowed
  to have.
- **It is observable.** Each take writes one line under
  `ReportService.TemplateChecks.Sample` - `check=at-bound-sample`, the field's
  path and shape, where the record came from, its size, the fields skipped and
  why, whether the value is anchor-bound, its hash, the page count and the timings
  - and the rule is `at-bound-sample`. Neither the line nor the message carries a
  value of the data.
