# PDF verification

Every PDF the service prints is checked before it is published. Nothing is
written under the output namespace until every check has passed, and a document
that fails is never delivered and never retried: the same data printed again
reaches the same verdict.

This page states what is checked, with what reader, how a reading becomes a
verdict, what a failure reports, and how to run the checks yourself.

## What is checked

The checks are in two groups. The first needs no page and runs **before**
printing; a failure there ends the work with nothing printed. The second reads
the printed document. The verdict is the conjunction of all of them, and no
template setting turns one off.

| Check | What it asserts | Where the expected values come from |
|-------|-----------------|-------------------------------------|
| **Row coverage, before printing** | The partition's index page and its record agree on the block's record count and first record; the block document read back holds exactly that many records, with contiguous ordinals; the composition holds exactly one row fragment per record, in the same order | the partition, written by the data step — never the composer's own count |
| **A printed unit per row** | Every row fragment yields at least one non-empty text unit | the composition |
| **Declared fields** | For every field a template declares it prints as it is, the value read from the block appears, unchanged, in that record's printed text; the header's declared fields appear in the document parts of the first block | the block's own records and header |
| **Row coverage, after printing** | The printed document consumes the row fragments in the order the partition fixed | the partition and the printed text |
| **Text completeness** | Every text unit of the composition appears, in document order, once, with every character | the composition, bound to the source by the three checks above |
| **Pages** | The document has at least one page, and every page carries at least one word | the document |

The last two are what make a silently lost record or a silently altered
character impossible to publish. A row the composition omits is caught by row
coverage, because the expected rows come from the partition. A value the
template corrupts consistently in the composition and in the print is caught by
the declared fields, because their expected values come from the block's own
records.

## The reader

The reader is **PdfPig 0.1.16**, used in one fixed configuration:

- each page's words come from the library's own word extractor, with each
  word's bounding box;
- boxes are converted to a frame with the origin at the page's top-left corner
  and the y axis pointing down;
- words are read in **content-stream order** — the order the page's operators
  paint them — never in geometric reading order;
- three readings of one document are identical.

Nothing else reads the document: no layout engine, no page script, no second
PDF library.

## From a reading to a verdict

Both sides are normalised by one rule before they are compared: Unicode
**NFKC**, then every whitespace character removed. Nothing else — no case
folding, no punctuation mapping, no de-hyphenation. A printed replacement
character (U+FFFD) or a private-use code point is an **unmapped glyph**: the
glyph the template asked for was not printed, so the document fails.

The expected text units are the units of the **composed** document: the HTML the
pinned template produced from the block's records and header, split at the
element boundaries that start a new box. The printed text is consumed page by
page against them, in content order:

1. On every page after the first, a repeated table head (a `thead`) is consumed
   first if it is there; before the last page's end, a repeated foot (`tfoot`)
   is consumed the same way.
2. Then the units a page left open continue, in order.
3. Then new units start, in order.
4. The page must be consumed to its end.

A unit that stops part-way and does not continue on the next page is text lost.
An alignment that could end a unit at several positions searches the split
points, longest first, within a fixed comparison budget; a document that needs
more than the budget fails rather than passing by luck.

Because the reference is the composed document, a template feature that prints
text the composition does not contain (CSS `content`, list markers,
`text-transform`, hyphenation) or hides text it does contain makes the
verification fail: the product fails closed. The constraints this puts on a
template author are in [template-authoring.md](template-authoring.md).

## Reasons and what they mean

A failure names a fixed reason, the block, the record (0 for a document part),
the declared field, the page and the unit or offset of the first mismatch —
**never a value**, neither the source's nor the printed one.

| Reason | What it means |
|--------|---------------|
| `row-lost` | A record's row is not printed |
| `row-extra` | A row is printed for a record the block does not hold |
| `row-order` | The rows are not printed in the block's order |
| `row-without-text` | A record's row prints no text, so it cannot be proven printed |
| `text-dropped` | A unit's text stops early or is missing |
| `text-altered` | A unit's text is not the composed text |
| `text-extra` | Text appears that the composition does not hold |
| `anchor-mismatch` | A declared field's value is not printed as it is |
| `unreadable` | The document could not be read at all |
| `unmapped-glyph` | A printed character is a replacement or a private-use code point |
| `ambiguous-alignment` | The comparison budget was spent before a verdict |
| `no-page` | The document holds no page |
| `blank-page` | A page carries no word at all |
| `anchors-undeclared` | The pinned version declares no field it prints as it is, so the document cannot be verified |
| `page-accounting` | The pinned version declares page subtotals, which this service does not print |

Every reason ends the job's output with `PAGINATION_FAILED`, except the two
refusals of a template the service cannot print, which end it with
`UNSUPPORTED_CAPABILITY`, and a state the step cannot use, which ends it with
`EXECUTION_FAILED`. None of them is retried.

## The second box configuration is a diagnostic

The reader was measured in two word configurations. The second one — a
nearest-neighbour extractor — places words differently by up to about two points
vertically, and its placement depends on the page's geometry: on a table
document it moves a line of the page, which changes a line's ordinal but not a
word. No verdict here depends on a line ordinal: lines are grouped at ±2 points
of vertical centre for diagnostics only. That is why the product reads documents
with one configuration and treats the other as a measurement, never as a second
opinion that could change a decision.

## Running the checks

The rendering regression suite prints the core set of documents with the
product's own composer, printer and verification — and judges each of them again
with an independent reader built from PDF text tools — and asserts that both
readers agree on every document: every positive document passes both, every
known-bad control fails both with its class.

```text
dotnet run --project eng/test/ReportService.Eng.TestRunner -- rendering
```

The run writes `artifacts/test-results/<stamp>/run.json` and the level's log; a
run that cannot start its containers is reported with the failing command and
its output, never as a pass.

The reader's agreement with the measured experiment that selected it is
reproduced by an engineering tool that reads the experiment's own document set
with the product reader and compares the reading with the recorded one:

```text
dotnet run --project eng/pdf-reader-agreement/ReportService.Eng.ReaderAgreement -c Release -- \
  reading --set <the experiment's pdf-set.json> --out <a folder of its own>
dotnet run --project eng/pdf-reader-agreement/ReportService.Eng.ReaderAgreement -c Release -- \
  compare --measured <the experiment's measured reading> --product <the folder above>
```

The tool writes its own folder and never the measured one: the comparison is
between two readings, not a replacement of the record.
