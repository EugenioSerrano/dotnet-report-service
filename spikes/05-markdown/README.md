# 05 — Markdown conversion routes

Throwaway measurement program. It converts the grouped pilot dataset to
Markdown through four routes on one shared harness and measures, per route,
whether grouped tables stay continuous across many fragments, how memory
behaves as the document grows, and what is lost against the HTML output.
The code is disposable; the measurements below are what this folder keeps.

## Hypothesis

Markdown is produced from the semantic HTML of a report (not from the
paginated layout), and a large report reaches the converter as a sequence
of HTML **fragments**: the document start, then one fragment per block of
consecutive records, then the document end. Holding a document tree of the
whole report is not acceptable for large reports. Four ways to produce the
Markdown are compared:

| Route | What it does |
|-------|--------------|
| **A** — library per fragment, with table stitching | Each fragment is wrapped in the elements left open by the previous fragments (and, inside a table, in that table's header), converted on its own by ReverseMarkdown, and the synthetic table header of a continuation fragment is removed so its rows continue the table already written. |
| **B** — own streaming converter | A forward-only tokenizer and a state machine over the fragments, with no document tree; its state survives fragment boundaries. It converts the HTML subset the sample templates produce. |
| **C** — Markdown template parts | Markdown parts written for the grouped package, rendered per record by the template engine (Scriban). No HTML is produced on this route. |
| **D** — hybrid by size | When the header's record count is at most 1 000, the whole HTML document is converted by ReverseMarkdown in one call; above that, route B. |
| **baseline** (`html`) | Reads, renders and writes the HTML only — the cost every HTML-based route (A, B, D) shares. |

What each claim means, stated before measuring:

- **Continuous table** — the checker streams the output and the dataset side
  by side. A route passes when the output has the number of tables the
  template's layout implies (the pilot templates open **one table per
  document**, with one heading row and one subtotal row per group), exactly
  one separator line per table, no repeated header line, the header's cell
  count on every line, every record's row in order, and no other rows than the
  template's heading and subtotal rows. Blocks are cut every **48 records**,
  ignoring group boundaries on purpose, so the largest group (2 400 records)
  spans at least 50 blocks.
- **Flat memory** — per route, with the median of 3 fresh-process runs per
  size, for both the peak managed heap and the peak working set:
  `value(500k) <= 1.5 x value(10k)` **and** `value(500k) <= 1.25 x value(100k)`.
  Otherwise the growth is given as a slope between 100 k and 500 k records and
  the size at which the working set would reach 1 GiB by linear extrapolation.
  The 1 k size is reported but not used by the rule (start-up dominated). This
  rule classifies evidence; it is not a product target.

## How to run

Commands from the repository root. Disk: about 3 GiB free under the
temporary folder (the 500 k dataset is 139 MiB; each output is deleted after
its check). Time: about 5 minutes for the whole grid (126 child processes) on
the reference environment.

```bash
# 1. Datasets (default seed) into a temporary folder, never into samples/
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset grouped --count 1000 --output <tmp>/datasets
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset grouped --count 10000 --output <tmp>/datasets
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset grouped --count 100000 --output <tmp>/datasets
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset grouped --count 500000 --output <tmp>/datasets
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset simple --output <tmp>/datasets

# 2. Build
dotnet build spikes/05-markdown/MarkdownSpike.csproj -c Release

# 3. Own checks, library probes, first render of the sample parts, sample outputs
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll selftest
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll probe       # writes results/probe.txt
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll inventory   # writes results/html-inventory.txt
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll samples     # writes results/samples/

# 4. The measurement grid (fresh child process per run, one at a time) and its summary
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll measure --data-dir <tmp>/datasets --work <tmp>/work
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll summarize

# One conversion and its check, by hand
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll convert --route b --data <tmp>/datasets/header-items-v1.grouped.100k.json --output <tmp>/out.md --temp <tmp>/t
dotnet spikes/05-markdown/bin/Release/net10.0/MarkdownSpike.dll check --data <tmp>/datasets/header-items-v1.grouped.100k.json --output <tmp>/out.md
```

`measure` runs sizes 1k, 10k, 100k and 500k × routes `html, a, b, c, d` × 3
runs at block size 48; then route D with `--force-whole` at 1k and 10k (3
runs each, to show how the whole-document branch grows) and one conversion
and check of routes a, b and c at 10k with 1 000-record blocks. Every
conversion except the baseline is checked in a separate process, so the check
never adds to the route's memory figures. `convert` prints one JSON line;
`check` prints one JSON line and exits 0 whether the outcome is `continuous`
or `broken`. Exit codes: 0 success, 1 failure, 2 invalid arguments.

Memory figures: the peak managed heap and the working set are sampled every
10 ms by a background thread, and the working-set figure is the larger of
that sample and the operating system's peak working set of the process;
temporary disk is the peak size of the run's own temporary folder (sampled
every second). No browser runs here, so the browser process-tree figure is
"n/a". A 10 ms sample can miss a very short managed-heap peak; the three runs
and their spread are shown so that noise is visible.

## Results

### Environment and inputs

- Taken on 2026-10-21 (grid from 12:35 to 12:40, UTC−03:00) on the
  [reference environment](../../docs/reference-environment.md), **page
  revision 1 (2026-10-21)** — the page was not yet committed when the
  measurements were taken, so no commit can be cited for it.
- .NET SDK 10.0.402, runtime 10.0.13, Release build, workstation concurrent
  garbage collector (set explicitly in the project file). Each run is a fresh
  `dotnet` process; runs never overlap. Other programs were running on the
  machine at the same time (other measurement programs, editors, a browser);
  the three runs per cell and their spread show how much that moved the
  figures.
- Packages (exact versions, all from nuget.org):

  | Package | Version | Licence | Used by |
  |---------|---------|---------|---------|
  | ReverseMarkdown | 6.2.1 | MIT | routes A and D |
  | AngleSharp (dependency of ReverseMarkdown) | 1.5.2 | MIT | routes A and D |
  | Scriban | 7.5.1 | BSD-2-Clause | rendering of every route |

  AngleSharp's `System.Text.Encoding.CodePages` dependency applies only to
  .NET Standard and .NET Framework targets and is not resolved on `net10.0`.
  No vulnerable package is reported (`dotnet list package --vulnerable
  --include-transitive`). Assembly sizes: AngleSharp 991 KiB, Scriban
  551 KiB, ReverseMarkdown 178 KiB.

- Datasets (default seed 20261021, grouped dataset, generated as in step 1):

  | File | Records | Groups | Bytes | SHA-256 |
  |------|---------|--------|-------|---------|
  | `header-items-v1.grouped.1k.json` | 1 000 | 6 | 288 170 | `ff9eb06bae5ab23011f530948a37a38d818a2387e8cd1b711ef43951f2e80387` |
  | `header-items-v1.grouped.10k.json` | 10 000 | 31 | 2 877 773 | `d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753` (equal to `SHA256SUMS`) |
  | `header-items-v1.grouped.100k.json` | 100 000 | 456 | 28 982 481 | `e49cd79499f14b6d6626c3aa1d0ac2face83f738a6c46ebff55a70b2f6fb2d24` |
  | `header-items-v1.grouped.500k.json` | 500 000 | 2 357 | 145 440 094 | `14b23eb3766684354ae2b3f9d5237c4e6e774e2c8782a3b50c0adf41763255fa` |
  | `header-items-v1.simple.25.json` | 25 | — | 4 198 | `9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3` (equal to `SHA256SUMS`; same file as the simple package's `sample.json`) |

All raw figures are in [`results/measurements.csv`](results/measurements.csv)
(one row per conversion: 69 rows, all `ok`) and
[`results/continuity.csv`](results/continuity.csv) (one row per check: 57
rows, all `continuous`).

### Comparison

Median of 3 runs, block size 48. Heap = peak managed heap, WS = peak working
set, both in MiB. "Own code" counts the lines (comments included) of the code
each route needs beyond the shared harness (reading, rendering, blocks,
measuring: about 850 lines).

| | Baseline (`html`) | A — library per fragment | B — own streaming converter | C — Markdown parts | D — hybrid by size |
|---|---|---|---|---|---|
| Continuity at 10k / 100k / 500k | — | continuous ×3 / ×3 / ×3 | continuous ×3 / ×3 / ×3 | continuous ×3 / ×3 / ×3 | continuous ×3 / ×3 / ×3 |
| Max blocks spanned by one group | — | 51 (group G0012, 2 400 records) | 51 | 51 | 51 |
| Heap 1k / 10k / 100k / 500k | 5.9 / 18.3 / 19.0 / 19.4 | 16.8 / 19.7 / 20.1 / 20.7 | 14.8 / 18.4 / 20.1 / 20.0 | 5.7 / 18.5 / 18.9 / 19.3 | 19.8 (whole) / 18.7 / 19.9 / 20.1 |
| WS 1k / 10k / 100k / 500k | 37.5 / 55.4 / 60.8 / 61.3 | 66.3 / 72.7 / 77.9 / 78.5 | 47.3 / 57.4 / 62.3 / 62.6 | 37.2 / 56.8 / 59.9 / 61.0 | 74.4 (whole) / 58.1 / 62.4 / 61.8 |
| Memory rule | flat | flat | flat | flat | flat |
| Largest spread of the 3 runs (heap / WS) | 0.3 / 0.9 | 1.1 / 2.2 | 1.7 / 2.2 | 0.3 / 1.7 | 0.9 / 2.0 |
| Elapsed at 500k | 5.1 s | 19.3 s | 8.4 s | 6.9 s | 8.6 s |
| Output at 500k | 148.8 MiB (HTML) | 83.3 MiB | 83.1 MiB | 83.1 MiB | 83.1 MiB |
| Temporary disk | 0 | 0 | 0 | 0 | 0 |
| Counted defects (missing records, unexpected rows, cell-count mismatches, raw HTML lines), every size | — | 0 | 0 | 0 | 0 |
| Fidelity defects not counted by the checker | — | group heading repeated in all 6 cells; subtotal amount in the second column instead of the Amount column; `[`, `]`, `\` and backticks in text not escaped; a literal `&amp;` in data comes out as `&amp;`, which renders as `&` | none found | none found | as A on the whole branch (≤ 1 000 records), as B above |
| Own code | — | ≈ 190 lines (wrap and stitch) + the tag scanner (≈ 380 lines, shared with B) | ≈ 805 lines (tokenizer, writer) + escaping (≈ 115, shared with C) | ≈ 12 lines + 5 Markdown parts (15 lines) + escaping (≈ 115) | ≈ 35 lines + all of B + the library |
| Dependencies beyond the engine | — | ReverseMarkdown + AngleSharp | none | none | ReverseMarkdown + AngleSharp |

Reading the memory figures: the managed-heap peak of about 19–21 MiB at
10 k and above is essentially the garbage collector's young-generation
budget, not data kept by the route. At 1 k a collection has hardly run yet,
which is why the 1 k figures are lower and are left out of the rule. The
working set of route A is about 16 MiB higher than the other routes at every
size; it is a constant cost (the library and its HTML parser loaded), not
growth.

### The whole-document branch, forced (route D, `--force-whole`)

| Size | Heap MiB | WS MiB | Elapsed s |
|------|----------|--------|-----------|
| 1k | 19.3 | 74.4 | 0.5 |
| 10k | 141.0 | 206.2 | 1.9 |

Converting the whole document at once grows about 7× in heap from 1 k to
10 k records (the complete HTML string plus the library's document tree).
The tables are continuous in both cases. The rule's ratios against 100 k and
500 k were not measured for this branch: it is forced only at 1 k and 10 k,
as planned, because it holds the whole document by design.

### Realistic block size (1 000 records per block, 10k dataset, one run)

| Route | Outcome | Max blocks spanned | Heap MiB | WS MiB |
|-------|---------|--------------------|----------|--------|
| A | continuous | 3 | 31.4 | 85.6 |
| B | continuous | 3 | 22.8 | 61.6 |
| C | continuous | 3 | 18.5 | 56.7 |

Route A's memory goes up with the block size, because each fragment is parsed
whole by the library: about +12 MiB heap and +13 MiB working set at
1 000-record blocks compared with 48-record blocks. Route C does not cut blocks
(block size only affects the checker's span count).

### Library behaviour observed (ReverseMarkdown 6.2.1)

Full input and output in [`results/probe.txt`](results/probe.txt).

- The `GitHub` value of the flavor setting keeps tables, and any HTML it
  cannot write in that flavor, as **raw HTML** in this version (probe i).
  GitHub-compatible pipe tables come from the default flavor, which is the
  value the older GitHub switch maps to. The library routes use the default
  flavor.
- A table row with no table around it becomes plain text (`1x`, probe a), so
  a continuation fragment must be wrapped in its table again. With the
  table's own header re-added before the rows, the library writes a header
  line and a separator line, and the stitcher removes exactly those two lines
  (probe j). No route A output measured has a repeated header or a second
  separator, so the rule held for every fragment. No
  adaptation of the planned stitching rule was needed, except one: when a
  continuation fragment does not start with a table row (the closing
  fragment, which starts with the totals), a blank line is kept so that the
  totals do not become part of the table.
- A table without a header gets an empty header row whose cells are written
  as `<!---->` (probe c). The pilot tables always have a header, so this
  never appears in the measured outputs.
- A header cell spanning columns (`th colspan`) is repeated in every spanned
  column. A data cell spanning columns (`td colspan`) is written once, and the
  row is padded with empty cells at its end, so the subtotal amount moves to
  the second column (probe f).
- In text: `|` is escaped in cells; `*` and `_` are backslash-escaped; `<` and
  `>` stay as `&lt;` and `&gt;`, so hostile text never becomes a tag; `&` is
  written as a bare `&`; `[`, `]`, `\` and backticks are not escaped (probe d).
  Template-like text (`{{ … }}`, `${7*7}`) stays text (probe h).
- The document head (`title`, `meta`, `link`) produces no output; wrapper
  elements (`header`, `section`) are dropped and their content kept
  (probe g).
- The library re-parses every fragment with an HTML5 parser. That is the
  reason route A takes 2.3× route B's time at 500 k and costs more memory per
  fragment as fragments grow.

### HTML inventory of the sample packages (first render of their parts)

Full inventory in [`results/html-inventory.txt`](results/html-inventory.txt).
Every part of `pilot-grouped` and `pilot-simple` parses and renders with
Scriban 7.5.1 without errors, so no fix to the sample templates was needed.
Both packages open **one table per document** in the document start part and
close it in the document end part. The grouped package's group start part
opens a `tbody` with one heading row (`th colspan="6"`), and its group end
part adds one subtotal row (`td colspan="5"` plus the amount) and closes the
`tbody`. There is no `tfoot`, list, link, image, code or horizontal rule.
Elements produced: `html`, `head`, `meta`, `title`, `link`, `body`,
`header`, `h1`, `p`, `table`, `thead`, `tbody`, `tr`, `th`, `td`,
`section`. Observation for the sample templates' owner: `html.escape` writes
every non-ASCII letter as a numeric character reference (`reparaci&#243;n`),
which makes the HTML larger (about 1.8× the Markdown size at 500 k records)
but is valid HTML; every route turns it back into the UTF-8 character.

### Losses against the HTML output

The routes keep the content (titles, paragraphs, the table, every value) and
lose the presentation. Sample outputs of every route, for the 40-record
grouped sample cut into 10 fragments and for the 25-record simple sample, are
in [`results/samples/`](results/samples/). The own converter's loss counts
are in `results/samples/*.route-b.losses.txt`.

| HTML element / feature | Route A | Route B | Route C | Route D |
|------------------------|---------|---------|---------|---------|
| `<!DOCTYPE>`, `html lang` | lost | lost | not produced | lost |
| `head`: `title`, `meta charset`, `link` | lost (the `h1` carries the same title) | lost (not written, by design) | not produced | lost |
| `styles.css`: page size and margins (`@page` A4, margins), font family and sizes, borders, right-aligned and tabular numbers, bold subtotal and totals | lost | lost | lost (not expressible) | lost |
| `header`, `section` wrappers | tags lost, content kept | tags lost, content kept | content written directly | tags lost, content kept |
| `h1` | kept as `#` heading | kept as `#` heading | kept as `#` heading | kept as `#` heading |
| `p` (customer, generation time, record count, total) | kept as paragraphs | kept as paragraphs | kept as paragraphs | kept as paragraphs |
| `class` attributes | lost | lost | not produced | lost |
| `table` / `thead` / `th` column headers | kept as one pipe-table header and separator | kept | kept | kept |
| `tbody` per group | lost as structure; the groups remain as heading and subtotal rows inside the one table | same | same | same |
| Group heading row (`th colspan="6"`) | kept as a row, text repeated in all 6 cells | kept as a row, text in the first cell and 5 empty cells | as B (written so by the parts) | as A (≤ 1 000 records), as B above |
| Subtotal row (`td colspan="5"` + amount) | kept as a row, but the amount moves to the second column | kept, amount under Amount | kept, amount under Amount | as A (≤ 1 000 records), as B above |
| Column alignment (right-aligned numbers) | lost (no alignment markers) | lost | lost (the parts do not use alignment markers) | lost |
| Data rows, values and order | kept | kept | kept | kept |
| Non-ASCII text (numeric references in the HTML) | kept as UTF-8 | kept as UTF-8 | kept as UTF-8 | kept as UTF-8 |
| Hostile text (`<script>…`, `<b>…</b>`) | kept as text (`&lt;…&gt;`) | kept as text | kept as text | kept as text |
| `&` in text | bare `&` (a literal `&amp;` in data comes out as an entity that renders `&`) | `&amp;` | `&amp;` | as A / as B |
| Markdown punctuation in text (`*`, `_`, `[`, `]`, `\`, backtick, `|`) | `*`, `_`, `|` escaped; `[`, `]`, `\`, backtick not | all escaped | all escaped | as A / as B |
| Page breaks, running headers and footers, page totals | not in the semantic HTML (added by the layout step for PDF); absent from every route | | | |
| Lists, links, images, code, horizontal rules, `tfoot`, `caption` | not exercised by the pilot templates | not exercised (supported by the own converter's subset, untested on real templates) | not exercised | not exercised |

Counted defects per route and size (from `continuity.csv`, every run): records
missing 0, unexpected rows 0, cell-count mismatches 0, raw HTML lines 0, for
routes A, B, C and D at 1 k, 10 k, 100 k and 500 k, with 48-record blocks, and
at 10 k with 1 000-record blocks. Every group's heading row and subtotal row
were found in place (6, 31, 456 and 2 357 of each), and the title, the
customer and the total were found around the table in every output.

### Repeatability

A fresh repeat of route B at 100 k gave the identical check result
(continuous, 100 912 rows, 456 heading and 456 subtotal rows) and the same
output size (17 329 967 bytes). Its working set (62.7 MiB) fell within the
three runs' range (61.9–64.1 MiB). Its managed heap (19.7 MiB) was 0.1 MiB
below the three runs' minimum (19.8 MiB), which is within the measurement
noise. `selftest` (15 checks) passes on repeated runs.

## Conclusion

Facts and trade-offs only; the choice is made later as a project decision.

- **Table continuity is achievable on every route.** With 48-record blocks the
  largest group crosses 51 fragments, and all four routes still produce one
  continuous table with one header, at every size measured.
- **Memory is flat on every streaming route.** By the stated rule, routes A, B,
  C, D and the baseline all stay within 2 MiB of heap and 6 MiB of working
  set between 10 k and 500 k records (50× the data). Only the whole-document branch
  of route D grows (≈ 7× from 1 k to 10 k records), which is why it is limited
  to small reports.
- **Route A** (library per fragment) needs the least own conversion code. It
  depends on re-wrapping each fragment in its open elements and table header,
  and on the library always writing exactly a header and a separator for the
  re-added header. That holds in 6.2.1, but it is an observed behaviour of
  the library, not a documented contract. Route A costs about 16 MiB more
  working set and about 2.3× route B's time at 500 k. Its memory per fragment
  grows with the block size. It has fidelity defects with spanning cells
  (repeated heading text, shifted subtotal amount) and lighter escaping. It
  also brings a 1 MiB HTML parser as a dependency, and its version's `GitHub`
  flavor setting does not do what its name says.
- **Route B** (own streaming converter) is the fastest HTML-based route and
  matches the baseline's memory. It gives the most faithful tables of the
  HTML routes (spanning cells kept aligned) and needs no dependency. In
  exchange it is the largest piece of own code to maintain (about 800 lines),
  and it is limited to the HTML subset it knows. The subset is enough for the
  pilot templates; anything outside it is dropped as a wrapper and counted.
- **Route C** (Markdown template parts) is the cheapest in time and memory,
  needs almost no own code and no HTML at all, and its output is identical to
  route B's byte for byte (checked on both samples and on the 500 k dataset;
  same size on every dataset). Its cost is moved to template authors: each
  template needs Markdown parts written and kept in step with its HTML parts,
  and the Markdown is only as correct as those parts.
- **Route D** (hybrid by size) behaves as route A for small reports
  (≤ 1 000 records, including A's fidelity defects) and as route B above. It
  therefore carries the code and dependencies of both, for a small-report
  branch whose memory, at that size, is no better than route B's.
