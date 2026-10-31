# Table-and-grid experiment — rows split before Paged.js, checked page by page

Throwaway measurement harness. It lays the shared fixture series out with
Paged.js 0.4.3 inside the PDF base image, after the in-page pre-split of
`spikes/08-pagedjs-pre-split` (rows taller than one content area cut at line
boundaries into continuation parts that each fit a page), in **grid markup**
and in **table markup**, and checks every PDF with two checks that never read
the layout library's own output (its page, console or completion signal):

- the text-integrity check of `spikes/07-native-tall-rows` (TI-1 … TI-8),
  unchanged;
- a **strict accounting check**: from the PDF alone, the rows that print on
  every page, the page subtotals and the brought/carried-forward chain
  recomputed from the source amounts by the unchanged accounting rules of
  `spikes/02-pdf-layout`, through the PDF reader, adapter and full evaluation
  of `spikes/09-two-pass-page-subtotals`, linked unchanged.

No decision is taken here.

## Hypothesis

1. With rows split before layout, Paged.js 0.4.3 keeps every row and every
   character in **table markup**, as it did in grid markup.
2. The printed page subtotals and chain are exact **on every page, row set by
   row set**, in both markups.
3. The grid results equal those of `spikes/08-pagedjs-pre-split` on the same
   fixtures and sizes.

The known table effects of Paged.js 0.4.3 (column widths lost after page 1,
about twice the pages, no repeated column header) are **measured, never
corrected**.

## How to run

### Prerequisites

- The local registry `report-service-dev-registry` on `127.0.0.1:5055`
  serving `report-service-pdf-base:1.63.0-10.0.13-noble` (restored through
  the AppHost; never recreated by hand).
- The text-integrity checker image `text-integrity-check:local` of
  `spikes/07-native-tall-rows`.
- The 53 fixtures listed in `results/fixtures.sha256` (the 49 shared fixtures
  of `spikes/07-native-tall-rows` and the four ordinary blocks B250, B500,
  B2000 and B4000 built by `spikes/09-two-pass-page-subtotals`), in
  `results/raw/fixtures/`, each checked against that list.
- A quiet engine for the two measured stages: `docker ps` lists nothing but
  the registry, and no other measurement runs.

### Commands (product repository root, PowerShell)

`$wslRoot` is the WSL form of the current directory
(`X:\...\<repository>` becomes `/mnt/x/.../<repository>`). With Docker
Desktop on Windows paths, use `${PWD}` instead of `$wslRoot`.

```powershell
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
$raw = "spikes/10-pagedjs-table-strict-accounting/results/raw"

# Identity of every reused file and result (host only, Git Bash): every line two equal hashes.
sh spikes/10-pagedjs-table-strict-accounting/check-identity.sh > spikes/10-pagedjs-table-strict-accounting/results/identity.txt

# Images (not measurements). The digest comes from the registry; there is no default.
$pdfBaseDigest = (Invoke-WebRequest -Method Head -Uri http://127.0.0.1:5055/v2/report-service-pdf-base/manifests/1.63.0-10.0.13-noble `
  -Headers @{ Accept = "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json" }).Headers["Docker-Content-Digest"]
docker buildx build --progress=plain --load --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/10-pagedjs-table-strict-accounting/Dockerfile -t pagedjs-markup-spike:local .
docker buildx build --progress=plain --load `
  -f spikes/10-pagedjs-table-strict-accounting/Dockerfile.account -t strict-accounting-check:local .

# Plan parity with the grid experiment's committed plan (not a measurement; host build).
dotnet <harness>/PagedMarkupSpike.dll plan-parity --reference spikes/08-pagedjs-pre-split/results/plan.json `
  --out spikes/10-pagedjs-table-strict-accounting/results/plan-parity.json

# Probe on the grid experiment's committed PDFs (not a measurement).
docker run --name strict-accounting-check-<ts>-probe --init --memory 2g --memory-swap 2g `
  -v "${wslRoot}/spikes/08-pagedjs-pre-split/results/pdf:/evidence:ro" -v "${wslRoot}/$raw/fixtures:/fixtures:ro" `
  strict-accounting-check:local probe --evidence /evidence --fixtures /fixtures --out /out/probe

# Stage 1, grid (measured; quiet engine), then stage 2, table — the same with --stage 2 and --budget-seconds 7200.
docker run --name pagedjs-markup-spike-<ts>-stage1 --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/$raw/fixtures:/fixtures:ro" pagedjs-markup-spike:local run --stage 1 --fixtures /fixtures --out /out --budget-seconds 3600
docker cp pagedjs-markup-spike-<ts>-stage1:/out "$raw/stage1"; docker rm pagedjs-markup-spike-<ts>-stage1

# Checks (not measurements; inputs read-only), per stage, then over both.
docker run --name text-integrity-check-<ts>-check1 --init --memory 2g --memory-swap 2g -v "${wslRoot}/${raw}:/raw:ro" `
  text-integrity-check:local check --fixtures /raw/fixtures --runs /raw/stage1 --out /out/check      # -> integrity-stage1.json
#   the same for stage 2; then the text check's own controls on the grid footer-off base:
#   controls --fixtures /raw/fixtures --runs /raw/stage1 --out /out/c --base s1-grid-s2-G200-split-t1.00-f0.90-off-r1
docker run --name strict-accounting-check-<ts>-account --init --memory 2g --memory-swap 2g -v "${wslRoot}/${raw}:/raw:ro" `
  strict-accounting-check:local account --fixtures /raw/fixtures --runs /raw/stage1,/raw/stage2 `
  --integrity /raw/integrity-stage1.json,/raw/integrity-stage2.json --out /out/a
#   then: controls (… --accounting --extraction-grid --probe), compare (--reference-summary and --reference-integrity
#   of spikes/08-pagedjs-pre-split/results, mounted read-only), verdict (--identity --parity --comparison --summary)
```

`run --matrix quick` (7 runs per markup, one container) is the development
loop and the check that the run records meet both checkers' contract. Its
results are not evidence.

### Caps, classes, ceilings and budget

- **Caps per run:** 300 s in the run's own process; the process tree killed
  at 330 s; the container `--memory 4g --memory-swap 4g`; the cgroup
  `oom_kill` counter read before and after each run (the out-of-memory
  evidence).
- **Classes:** `ok`, `timeout`, `killed-wall-clock`, `oom-killed`, `crashed`
  (only on the browser's own crash or disconnection signals, or a layout the
  page reports as failed), `error` (harness defects only), `not-run` (with its
  reason).
- **After a cap** in a size series (T150 … T500; T150u … T500u; X64K … X1M; the
  free text per placement) the larger sizes are not run.
- **Ceilings:** a repeated fixture whose first repetition took over 240 s, or
  whose Chromium process tree exceeded 3.2 GiB, has its remaining repetitions
  `not-run (near ceiling)`; no run starts once the stage's output exceeds
  4 GiB. Stage budgets 1 h (grid) and 2 h (table) of driver wall clock.
- **Memory figures:** each record carries the run's Chromium process-tree
  peak RSS and PSS, the harness `VmHWM`, and `cgroupMemoryPeakBytes` — the
  **container running peak** (the stage container's peak up to that run, not a
  per-run figure). The near-ceiling rule reads the Chromium tree peak.
- **Run ids** carry their stage and markup from the plan: `s1-grid-…` and
  `s2-table-…` followed by the grid experiment's own id of the same run
  (including its stage tag, `s1-…` or `s2-…`, because that experiment ran
  three runs in both of its stages), or `mem-…` for the four memory-series
  blocks. Example: `s2-table-s2-X1M-split-t1.00-f0.90-on-r1`.

## The split and the markups

| | Grid | Table |
|--|------|-------|
| Pages | `spikes/08-pagedjs-pre-split`'s page builder, linked unchanged | `TablePageBuilder.cs`: the same page configuration; the shared series' table structure (column group, table head, group rows, block-total row), item rows with `data-seq` and `data-amount` |
| Pre-split script | `pre-split.js` of `spikes/08-pagedjs-pre-split`, linked unchanged | `pre-split-table.js`: a copy whose only changes are the table selectors and that the off-screen measuring copy is the whole table, so rows are measured at the declared column widths |
| Stylesheet | `fixture.css` of `spikes/08-pagedjs-pre-split`, linked unchanged | `table.css` (below) |
| Trigger / part limit | 1.00 / 0.90 (the grid experiment's selection) | the same — **table results hold at 1.00 / 0.90 only** |

`assets/table.css`, as used (no width, no table display or layout change):

```css
body { hyphens: manual; }
h1.title { font-size: 13pt; margin: 0 0 3mm 0; }
.free p { margin: 0 0 2mm 0; }
.free-after { margin-top: 3mm; }
table.rows tr.block-total td { font-weight: bold; }
table.rows td.note { overflow-wrap: anywhere; }
table.rows.pre-line td.desc, table.rows.pre-line td.note { white-space: pre-line; }
table.rows tr.item { break-inside: avoid; }
table.rows tr.item.part.continuation { break-before: page; }
```

Reused, by folder: `spikes/02-pdf-layout` (accounting rules, sampler, result
types, base stylesheet, accounting script, Paged.js 0.4.3 — linked);
`spikes/07-native-tall-rows` (fixture library — referenced; PDF reading and
text-check files — linked into the checker; its checker image — used as it
is); `spikes/08-pagedjs-pre-split` (grid builder, run record, pre-split script,
stylesheet — linked; runner, driver and program — copied and changed only to
take the markup, write this plan, add the ceilings and narrow `crashed`);
`spikes/09-two-pass-page-subtotals` (PDF reader, adapter, full evaluation and
their record types — linked; the parts that judge a second pass or a re-saved
PDF are compiled and never called); `spikes/03-pdf-assembly` (image reader,
compiled only). `check-identity.sh` lists every file and result read, with
its hash.

## The checks

- **Strict accounting (per run).** The PDF is read with poppler; each content
  line is assigned to its item row by the text check's own classification;
  per page, the rows with a word there form the page's row set. The unchanged
  rules: 1 rows not lost or duplicated, on contiguous pages; 2 each page's
  printed subtotal equals the sum of the source amounts of the rows that end
  there; 3 the chain from 0.00 to the block total; 4 visible amounts; 5
  "(continues)" marks; 6 geometry; 8 escaping; rule 7 compares a footer-on run
  with its footer-off twin. When rule 1, 4 or 5 fails, every page is evaluated
  and each violation gets its cause. Expected values come only from the
  fixture.
- **Character check (column-agnostic).** Per row, every word the reading
  assigns to the row, whatever column it fell in: the one word equal to the
  date and the one equal to the amount removed, the rest compared with the
  description by the text check's own comparison; for the five-column
  fixtures, a multiset of characters (it cannot see a reordering inside a
  row). It reads only the PDF and the fixture.
- **Column geometry (observation).** Per page, the left edge of the first
  word of each item-row line (sequence, marks and amount band excluded)
  against the declared column starts plus the 1 mm cell padding: `declared`
  or `other`.
- **Attribution.** In table markup only, a TI-3 (description or note) or TI-4
  (date) text failure on an `other` page is attributed to column geometry, and
  the character check judges that row — allowed because the character check
  is proven by its own controls in table (below). TI-1, TI-3 unmapped glyphs,
  TI-4 amount and rules 4 and 5 are never attributed.
- **What the checks cannot show.** The character check cannot see a
  reordering inside a five-column row. The text check lists at most 100
  failures per run; a run with more is **refuted by the count alone** (the
  rule of `spikes/09-two-pass-page-subtotals`), even when no listed failure is
  a loss — see "Refuted by the unlisted count" below.

## Results

Environment: PDF base `sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d`
(the digest the grid experiment measured on), Chromium 153.0.8010.12,
Playwright 1.63.0, .NET 10.0.13, Paged.js 0.4.3 unchanged; 16 CPUs; containers
capped at 4 GiB with swap off; reference environment page revision 2.

- **Stage 1, grid:** 95 runs, all `ok`, 320 s of driver wall clock.
- **Stage 2, table:** 95 runs, all `ok`, 419 s. No cap, ceiling, budget or
  out-of-memory event in either stage.

### The checks and their controls

All behave, so the outcomes below are computed:

- **Text check, grid.** B100 and K1 pass. NC-W, NC-L and NC-R on B100 and on
  T300u, and NC-F on F26K-a, each fail with exactly their one expected failure.
  E1–E4 behave on the footer-off base `s1-grid-s2-G200-split-t1.00-f0.90-off-r1`.
  E5–E8 are not evaluable: the text check's controls cannot take real marks.
- **Strict check, grid.** B100 and K1 fail nothing. NC-R fails exactly rule 1
  (sequence 51 missing) and rule 3 on the last page. NC-W, NC-L and NC-F fail
  nothing. Four faults injected into the parsed `s1-grid-s2-G200-split-…-on-r1`
  each fail exactly their set: a page subtotal + 0.01, a brought-forward value
  + 0.01, a removed "(continues)" mark, and a removed row.
- **Strict check, table.** K1 (one page, declared geometry) fails nothing.
- **Character check, grid.** It agrees with the text check row by row on 94
  grid runs. The 95th, G200 without the split, has more failures than the
  text check lists, so it was not compared.
- **Character check, table — proven by its own controls.** On table B100,
  which passes the character check, NC-W gives exactly `text-dropped` row 50,
  NC-L `text-repeated` row 50, and NC-R `row-lost` row 51. Both mutated rows
  print on page 2, a page whose column geometry is `other`. On table T300u
  the controls are not evaluable, because the base run itself loses text.
- **Probe.** On the grid experiment's four committed PDFs: on the three with
  the split, every row prints and the chain closes on the block total; on G200
  without the split, sequences 51–200 are absent.

### Grid: comparison with the 08 experiment

All 91 shared grid runs compared field by field with the run of the same id
in `spikes/08-pagedjs-pre-split/results`, with **0 differences**:

- class, pages, parts, split rows;
- the library's own rule lines and "Layout repeated" signals;
- the body hash and length of the PDF page;
- the text check's verdict, its normalised content hash and the PDF page count.

The printed accounting is now checked strictly: every grid footer-on run is
exact on every page, except G200 without the split, the known giant-row
signature (rows 51–200 lost).

### Table: rows, characters and accounting

| What | Table result |
|------|--------------|
| Rows lost (strict rule 1) | **G200** with the split, footer on and off: 150 rows lost after row 50. **T150, T200, T300, T500** with the split: 50 rows lost after row 50. T150 and G200 without the split: the same. "Layout repeated" on the console in every one of these runs. The last page holds only row 50's text, with no header and no footer (`png/…T150…page012.png`). |
| Characters lost (rows print, text inside a row does not) | In table markup at part limit 0.75 and above, the tall row lost text in **T150u–T500u, X64K–X1M (all repetitions), W2a–W2c and H3**, and in every sweep run. The pattern is regular: one contiguous block of about 16–18 words at the end of every continuation part. T300u (4 parts) lost 52 of its 369 numbered tokens in 3 gaps (tokens 43–60, 152–169, 262–277); X64K (14 parts) lost 218 of 1483 in 13 gaps. Grid T300u prints all 369. In `pdf/s2-table-s2-T300u-…pdf`, page 3 ends with token 42 and page 4 starts with token 61 (`png/…T300u…page003.png`, `…page004.png`). The sweep rows S080–S125, which are never split, lost text as well. |
| Characters kept | At part limit 0.50, T500u (11 parts, 27 pages) and X64K (26 parts, 57 pages) kept every character. H1 and H2 kept every character. |
| Printed accounting (strict rules 1–3) | **Exact on every page** of every footer-on table run whose rows all printed. Even in runs that lost characters, every page's subtotal equals the source amounts of the rows that end there, and the chain closes on the block total. Not exact only where rows were lost (G200, T150–T500, with or without the split). |
| Ordinary blocks | B100, B1000, B250–B4000: every row and character printed; accounting exact; the split splits no row and changes nothing (B1000 split on and off: same pages, row sets and text; the three repetitions agree). |
| Free text | Every character of free text before and after the detail printed (TI-6), up to 1 MiB, in both markups. |

**Table outcome counts:** 15 confirmed (K1 twice and 13 free-text runs), 6
partial (free-text runs whose detail moves to pages 2 and later: rule 4,
the amount-band effect below), 67 refuted, 7 controls. Grid: 87
confirmed, 1 refuted (G200 without the split), 7 controls.

**Refuted by the unlisted count.** 16 of the 67 refuted table runs are
refuted only because the text check reported more failures than it lists:
B100, B1000 (all 7 runs), B250–B4000, H1, H2, and T500u and X64K at part
limit 0.50. In each of them rules 1–3 are exact and the character check
passes every row. The failures it does list are geometry-attributed or rule 4
and TI-4 amount failures (below). They are stated as `refuted` by the rule as
written; nothing was changed to alter them.

### Table effects (measurements only)

- **E-W column widths.** On every table run, page 1 has the declared column
  geometry. Every later page with detail is `other`: date at 59.5 mm,
  description at 106 mm — four equal columns
  (`png/s2-table-s1-B1000-…page001.png` against `…page002.png`).
- **E-P pages.** Table prints more pages than grid. B1000: 78 against 36.
  B4000: 307 against 143. X1M: 665 against 224 (row 50 over 659 pages). Free
  text: the same.
- **E-H column header.** The table head prints on page 1 only.
- **E-A amounts and marks outside the declared amount band.** The base
  stylesheet `layout.css` sets `table.rows th, table.rows td { text-align:
  left; }`. That rule is more specific than `.amount { text-align: right; }`,
  so every amount cell is left-aligned, unchanged here.
  - On page 1, the amount column starts at 168 mm, so amounts fall in the
    declared amount band (168–198 mm).
  - From page 2, the equal columns start the amount column at 151.5 mm. The
    amount and any "(continues)" mark print at about 152.5 mm, inside the
    description band. pdftotext of `s2-table-s1-B100-…-on-r1` page 2: row 31's
    amount `5509.41` at x 432.2–464.8 pt (152.5–163.9 mm).
  - Result: rules 4 and 5 fail on pages 2 and later of 77 table runs, never on
    page 1 (`amount-band.json`).
  - **On those pages the strict check cannot separate a real visible-amount
    or mark defect from this effect.** That is why table outcomes are at best
    `partial` there. The character check finds every amount where text is not
    lost.

### Cost (per run, Chromium process tree)

| Fixture | Grid pages / layout / RSS | Table pages / layout / RSS |
|---------|---------------------------|----------------------------|
| B1000 (split on) | 36 / 0.98 s / 538 MiB | 78 / 1.50 s / 559 MiB |
| B4000 | 143 / 3.54 s / 820 MiB | 307 / 7.48 s / 895 MiB |
| T300u | 8 / 0.22 s / 458 MiB | 16 / 0.40 s / 474 MiB |
| X1M | 224 / 4.25 s / 816 MiB | 665 / 17.1 s / 998 MiB |
| F1M-1p-b (one 1 MiB paragraph) | 124 / 34.2 s / 704 MiB | 124 / 34.5 s / 669 MiB |

Every figure is in `runs.csv`.

## Conclusion

Within what was measured — Paged.js 0.4.3, Chromium 153.0.8010.12, this base
image, these stylesheets, trigger 1.00 and part limit 0.90:

- **Grid.** Splitting rows before layout keeps every row and every character
  in grid markup. The printed page subtotals and chain are exact on every page,
  row set by row set. The grid results are identical to those of
  `spikes/08-pagedjs-pre-split`.
- **Table.** The tall rows of the unique-text, two-cell and record-limit
  series and H3 lost text: about 16–18 words at the end of each continuation
  part. H1 and H2, and the two runs at part limit 0.50, kept every character.
- **Table.** The repeated-text tall rows of `spikes/02-pdf-layout`
  (T150–T500, G200) still lost every row after the tall one, with "Layout
  repeated", even with the split.
- **Table.** The printed accounting stayed exact for the rows that did print.
- **Table.** Part limit 0.50 kept every character of the two rows measured at
  that value. Whether it does so for other rows, sizes or stylesheets is not
  measured.
- **Table.** Column widths after page 1, page counts, the column header and
  the amount band behave as listed under "Table effects". None was corrected.

Nothing here is generalised to another library version, browser, template
or stylesheet, and no threshold is claimed beyond the measured values. No
decision is taken here.

## Packages and licences

| Component | Licence | Notes |
|-----------|---------|-------|
| Microsoft.Playwright 1.63.0 | MIT | The only package (harness); pinned in this folder's `Directory.Packages.props` |
| Chromium headless shell 153.0.8010.12 | BSD-3-Clause | From the PDF base image; bundled notices inventoried in `spikes/02-pdf-layout/README.md` |
| Paged.js 0.4.3 | MIT | Vendored in `spikes/02-pdf-layout`, linked unchanged with its `LICENSE.md` |
| Fonts | As in the PDF base image | Liberation Sans |
| poppler-utils 24.02.0 | GPL-2 or GPL-3 | Only in the checker images; never shipped |

The checker image installs only poppler-utils; the harness image installs
nothing over the PDF base.

## Maintenance observations

- The table path depends on Paged.js 0.4.3 rebuilding each page's table
  without the `<colgroup>` widths. Parts measured at the declared widths grow
  on later pages, and the overflowing end of each part is not printed.
- The table path does not repeat the table head.
- The grid path's results did not change between the two experiments on the
  same base digest.

## Files

- `PagedMarkupSpike/` — the layout harness: `Program.cs`, `Plan.cs`,
  `MatrixDriver.cs`, `PreSplitRunner.cs`, `TablePageBuilder.cs`,
  `assets/pre-split-table.js`, `assets/table.css`, and the linked files.
- `StrictAccountingCheck/` — the checker: `Program.cs`, `Account.cs`,
  `CharacterCheck.cs`, `Controls.cs`, `Compare.cs`, `Verdict.cs`, `Probe.cs`,
  `HarnessRecords.cs`, and the linked files.
- `Dockerfile`, `Dockerfile.account` and their allow-list ignore files;
  `check-identity.sh`.
- `results/`:
  - `summary.json` — verdict, controls, statements, attributed failures,
    table effects, costs, every outcome;
  - `accounting.json` and `integrity.json` — compact copies: long per-page
    lists are cut, with totals kept;
  - `controls.json`, `extraction-controls-grid.json`, `comparison.json`,
    `probe.json`, `amount-band.json`, `plan.json`, `plan-parity.json`,
    `runs.csv`, `environment.json`;
  - `identity-start.txt`, `identity.txt`, `fixtures.sha256`;
  - 4 PDFs: table T300u, T150 and B1000; grid T300u;
  - 8 page images.
- `results/raw/` — local output, not committed.
