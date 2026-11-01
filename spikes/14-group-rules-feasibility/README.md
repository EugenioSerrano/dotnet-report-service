# Group behaviours with Paged.js and pre-split rows, in grid markup

Throwaway experiment. It asks whether three group behaviours can be produced
on a block laid out with Paged.js 0.4.3 after rows taller than a page are split
before layout, in grid markup:

1. **keep-header** — a group header is never left alone at the end of a page;
2. **repeat-header** — the group header is repeated, followed by "(cont.)", on
   every page where its group continues;
3. **keep-total** — the group total stays on the page where the group's last
   row ends.

`spikes/12-group-continuity` measured the same blocks with no group rule:
Paged.js left headers alone at page ends, never repeated a header, and moved
some totals to the next page. This experiment lays the same blocks out five
times — without any behaviour (`none`), with each behaviour alone, and with
all three (`all-three`) — and checks every PDF with checks that never read the
layout library's own output. It reports what was printed; it decides nothing.

## Hypothesis

Each behaviour can be produced on these blocks, in grid markup, with the rows
split before layout at trigger 1.00 and part limit 0.90, without losing text
or rows, with every group header, group total and row in place, and with the
page subtotals and the brought/carried-forward chain exact on every page — at
some cost in pages and time, measured against the same blocks without it.

## How each behaviour is attempted

Only the PDF page changes, and only in its head: one script
(`assets/group-rules.js`) and, for the repeated header, one stylesheet
(`assets/group-rules.css`). The body of the page, the semantic page and every
other asset are those of `spikes/12-group-continuity`.

- **keep-header and keep-total** — a pagination handler of Paged.js that, when
  the content that does not fit a page starts at a group's first row, ends the
  page before that group's header instead (keep-header); and when it starts at
  a group total, ends the page before the group's last row instead
  (keep-total). A break is only ever moved earlier, by at most two rows, and
  never before the first row of a page, so no page is left empty.
- **repeat-header** — before the layout starts, each group's rows are wrapped
  in a container that carries the header text followed by " (cont.)". When
  Paged.js continues a container on a new page, and that part of the container
  does not hold the group's own header, the stylesheet prints that text at its
  top, laid out with the page (so the page's capacity, and therefore its
  subtotal, reflect it).

A behaviour is attempted with exactly this method. "Not produced" or "partly
produced" means not produced, or partly produced, **by this method**. Other
methods (for example a stylesheet rule that asks the engine to avoid the
break, or a second layout pass) are not attempted here.

## How a repeated header is read

The text check of `spikes/07-native-tall-rows` counts the group lines of a
block against its source, and attributes a line to a row by the lines before
it. A repeated header is a group line that is not in the source; above the
continuation part of a split row it would also detach that part's lines from
their row in the reading, and the checks would report losses that did not
happen.

The checks are therefore run, unchanged, on a **reading** of the PDF: the
printed text with the repeated-header lines set aside. A line is set aside
only when its text equals exactly "&lt;group id&gt; — &lt;group name&gt; (cont.)"
for a group of the block's own source. Nothing else is ever set aside — not a
line with any other text, not a group the block does not hold, and not the
"(continues)" mark of a split row. That mark is a different text in a
different place: it is printed in the amount column of a row that continues on
the next page, and it is judged by the text check's mark rule as before. Every
line set aside is listed per run. Controls (below) show that the rule sets
aside exactly those lines and hides nothing. This is a reading of the
checks' input, not a change of the checks; the text check's own image is also
run on the unread PDF and its verdict kept beside.

## Checks, and what each one measures

- **Text** (the rules of `spikes/07-native-tall-rows`, unchanged): every row,
  every character, the group lines, the free text, the marks, the header band.
- **Page accounting** (`spikes/10-pagedjs-table-strict-accounting`, unchanged):
  rules 1–9 read from the PDF; a row that continues across pages is counted
  once, in the subtotal of the page where it ends; the earlier pages carry a
  "(continues)" mark. The page subtotals and the chain are computed by the
  page's accounting script after layout: this is the **engine check**, and it
  is re-run for every configuration (a repeated header or a moved break
  changes what fits on a page). Rule 7 compares each footer-on run with its
  footer-off twin of the same configuration.
- **Group check** (`spikes/12-group-continuity`, unchanged): every header with
  its source text, in source order; every closed group's total printed once
  with the sum of its rows' source amounts; every row inside its group; the
  block total. The group-total text is written from the source data, so this
  shows whether that static text survives pagination, not whether the engine
  computes it.
- **The three behaviour observations** — below, fixed before any run.

## Observation rules and classes

These rules were written, and this section hashed
(`results/definitions-start.txt`), before the measured stage; the hash at the
end is in `results/definitions.txt`. They describe what a run printed; none is
a judgement against a product acceptance.

**keep-header — a group header never left alone at a page end.**
- *Instance:* each group that starts in the block (its own header line is
  read).
- *Produced:* the page of the group's own header line holds at least one line
  of a row of the same group.
- *Not produced at that instance:* the header's page holds no line of a row of
  its group (the page given).

**repeat-header — the header repeated with "(cont.)" on the pages where its
group continues.**
- *Repeated-header line:* a line whose text is exactly
  "&lt;group id&gt; — &lt;group name&gt; (cont.)" for a group of the block.
- *Instance:* each pair (group, page) where the page comes after the page of
  the group's own header line and holds a line of one of the group's rows
  (any part of a split row) or the group's total line.
- *Produced:* that page holds exactly one repeated-header line of the group,
  before every line of the group on that page.
- *Not produced at that instance:* the line is missing, appears more than
  once, or comes after a line of the group (each kind counted).
- *Spurious:* a repeated-header line of a group on a page that is not one of
  its instances (counted apart).

**keep-total — the group total kept with its last row.**
- *Instance:* each group closed in the block (its total line is read).
- *Produced:* the total line is on the page where the last line of the group's
  last row is.
- *Not produced at that instance:* the total is on another page (both pages
  given).

**Contested instances** (used only by "not produced"): for keep-header and
keep-total, an instance (identified by its group) that is not produced in the
run without behaviours of the same block, footer and repetition; for
repeat-header, every instance (the runs without behaviours print no
repeated-header line).

**Classes** — for each behaviour, in each configuration that attempts it (the
behaviour alone, and all three together), per case (GE, GS, GT, GI, ordinary
blocks) and over all cases:
- **produced** — every planned run of the set finished, its text, page
  accounting and group checks pass on the reading with no other cause (outcome
  `confirmed`), and the behaviour counts no instance not produced (and, for
  repeat-header, no spurious line) in any of them;
- **not produced** — no contested instance is produced in any run of the set
  (for repeat-header: no repeated-header line on any instance page), or no run
  of the set finished;
- **partly produced** — every other result, always listed with its reasons:
  the instances not produced by case, block, group and page; the runs that did
  not finish (with their class); the runs that finished but are not
  `confirmed` (with their causes); the spurious lines.

Each class describes what these runs printed with this method, these blocks,
this Paged.js and this Chromium. None decides whether the product should have
the behaviour.

## Blocks

The 50 blocks of `spikes/12-group-continuity`, rebuilt by its builder
unchanged; the `SHA256SUMS` of the build equals that folder's committed
`results/fixtures.sha256`.

| Block | Case | Records (dataset index) | Groups in the block |
|-------|------|-------------------------|---------------------|
| GE30 … GE66 (37) | GE — a group starting near the end of a page | 401 … 650 | G0003 (1 record, closed), G0004 (192, closed), G0005 (57, open); the G0003 record's description has *n* lines, so G0004's header moves down one line per step |
| GS1200 | GS — a group spanning several pages | 594 … 1959 | G0005 (160), G0006 (1 200), G0007 (6, open) |
| GS2400 | GS — a group spanning many pages | 3152 … 5559 | G0012 (2 400), G0013 (8, open) |
| GTL300u | GT — an over-tall row as a group's last row | 0 … 259 | G0001 (182), G0002 (78, open); sequence 182 carries a 15 930-character description |
| GTF300u | GT — an over-tall row as a group's first row | 0 … 259 | as above; sequence 183 (first of G0002) carries it |
| T300u, X64K, X1M, G200 | GI — over-tall rows inside a group | shared | row 50 in G0001 (G200 also row 120) |
| B100, B1000, B4000 | ordinary blocks | shared | many group boundaries |
| K1, F26K-a | controls | shared | — |

**A limit of the split settings, not tuned.** With trigger 1.00 and part
limit 0.90, a row between 0.90 and 1.00 of a page is not split. In GE62–GE66
G0003's single row is that tall: its header and its row cannot share a page,
so no break placement can keep the header with its row there. The experiment
reports what happens; it does not change the settings.

## The run

One stage of 305 runs, every run split at 1.00 / 0.90:

| Row | Runs |
|-----|------|
| 1 | controls without behaviours: B100, K1; three faults on B100; one on F26K-a (6) |
| 2 | the case set in the five configurations, interleaved per block (239) |
| 3 | footer-off twins of GE50, GE60, GS1200 and G200 in the five configurations (20) |
| 4 | repetitions 2 and 3 of B1000, GS2400, X1M and B4000 in the five configurations (40) |

Caps, classes and ceilings are those of `spikes/12-group-continuity`: 300 s
per run, the process tree killed at 330 s; `--memory 4g --memory-swap 4g`;
classes `ok`, `timeout`, `killed-wall-clock`, `oom-killed`, `crashed`,
`error`, `not-run`; a run over 240 s or a Chromium tree over 3.2 GiB marks the
remaining repetitions of its block `not-run (near ceiling)`; 4 GiB of output;
a 2 h budget (305 runs). The stage driver applies a cap or a near-ceiling to
the block and footer, whatever the configuration, so a later configuration of
the same block is then not run. X1M, GS2400 and B4000 are the top of the
measured bracket; nothing is concluded beyond the measured runs.

## How to run

From the repository root, Docker with BuildKit, PowerShell on Windows. Every
container is named and removed by name; inputs are mounted read-only.

```powershell
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
$raw = "spikes/14-group-rules-feasibility/results/raw"

# Identity of every reused file and result (host, Git Bash): every line two equal hashes.
sh spikes/14-group-rules-feasibility/check-identity.sh > spikes/14-group-rules-feasibility/results/identity.txt

# Images (not measurements); their build stages verify the formatting and audit the packages.
docker buildx build --progress=plain --load --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/14-group-rules-feasibility/Dockerfile -t group-rules-spike:local .
docker buildx build --progress=plain --load -f spikes/14-group-rules-feasibility/Dockerfile.account -t group-rules-check:local .

# Blocks (not a measurement): the builder of spikes/12-group-continuity, into $raw/fixtures.
docker run --name group-rules-check-<ts>-fixtures --init -v "${wslRoot}:/repo:ro" -v "${wslRoot}/${raw}:/out" group-rules-check:local `
  group-fixtures --dataset /repo/samples/datasets/header-items-v1.grouped.10k.json --sums /repo/samples/datasets/SHA256SUMS `
  --reference /repo/spikes/12-group-continuity/results/raw/fixtures --tall /repo/spikes/12-group-continuity/results/raw/fixtures/T300u.json --out /out

# Development loop (not evidence): the probe and the quick loop, then the planted controls on the quick runs.
#   run --matrix probe | quick --fixtures /fixtures --out /out ; evaluate ; planted --prefix q-

# The measured stage (quiet machine): 305 runs.
docker run --name group-rules-spike-<ts>-stage1 --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/$raw/fixtures:/fixtures:ro" group-rules-spike:local run --stage 1 --fixtures /fixtures --out /out --budget-seconds 7200
docker cp group-rules-spike-<ts>-stage1:/out "$raw/stage1"; docker rm group-rules-spike-<ts>-stage1

# Checks (not measurements): the text check's image of spikes/07-native-tall-rows (check; controls on the base
# s1-none-G200-split-t1.00-f0.90-off-r1), then in group-rules-check:local: evaluate, controls, anchors, verdict.
```

## Results

The stage ran once, in the serial window confirmed before it: at its start and
at its end `docker ps` listed only the local registry container
(`report-service-dev-registry`), and the window closed at
`2026-10-31T22:47:54-03:00`. All **305 planned runs** finished `ok` — none was
`timeout`, `killed-wall-clock`, `oom-killed`, `crashed`, `error` or `not-run`.
The driver's wall clock was **872.6 s** against the **7200 s** budget (the
stage summary records 874 s). No cap or ceiling was reached: the longest run
took 11.0 s of its 300 s, the largest Chromium process tree was 851.6 MiB of
3.2 GiB, no out-of-memory kill was recorded, and the output is 219.9 MB of the
4 GiB ceiling. The container running peak reached 1.55 GiB; it is the stage
container's running maximum, listed and never compared between configurations.

### Trust in the instrument, before the results

- **Controls.** Every control behaved under its own rule: the text positives
  and the four planted text faults; E1–E4; E5–E8 not evaluable (the text
  check's own controls cannot take real marks); the strict positives, the NC-R
  fault and AX-1…AX-4; the character check's agreement over 305 runs; the group
  positives and GX-1…GX-5; RH-1 (185 runs, 0 lines set aside), RH-2 (305 runs
  equal to the text check's own entry on the unfiltered PDF), RH-3…RH-6;
  OX-1…OX-6. The instrument is trusted.
- **Anchors.** All 59 runs without behaviours that
  `spikes/12-group-continuity` also ran match its committed results field by
  field — class, pages, HTML length, body hash, content hash, row-set hash, the
  library's rule lines, the repeated-layout signal, the text verdict:
  **0 differences**.
- **Identity and definitions.** 105 of 105 identity lines equal at the start
  and at the end (`results/identity-start.txt`, `results/identity.txt`); the
  observation definitions hash to the same value before and after the stage
  (`9243b3b6…`, 60 lines).
- **Checks on every run.** 305 PDFs read: 301 pass the text check and the four
  failures are exactly the planted negative controls; the page subtotals and
  the chain are exact on every footer-on run except the planted NC-R fault; the
  group check reports no failure on any run; rule 8 (escaping) passes on 305 of
  305; SEM passes on 305 of 305; rule 7 passes on all 20 runs that have a
  footer-off twin; the three repetitions of B1000, GS2400, X1M and B4000 agree
  in pages, content hash and row set; every behaviour run's body equals its run
  without behaviours. The reading rule set aside 0 lines in all 305 runs.

### The behaviours, case by case

Counts are *produced / instances*; the parentheses give the instances not
produced. The `none` column is the same blocks without any behaviour, shown for
reference and not classed. For the repeated header nothing was printed at all,
so its count is *produced / instance pages* and the parentheses give the
missing ones; no spurious repeated-header line appeared in any run.

| Behaviour | Case | `none` (reference) | Behaviour alone | All three |
|-----------|------|--------------------|-----------------|-----------|
| **keep-header** | GE — a group starting near a page end | 103/117 (14) | 115/117 (2) — **partly produced** | 114/117 (3) — **partly produced** |
| | GS — a group spanning several pages | 12/12 | 12/12 — **produced** | 12/12 — **produced** |
| | GT — an over-tall row as a group's last or first row | 3/4 (1) | 4/4 — **produced** | 4/4 — **produced** |
| | GI — over-tall rows inside a group | 9/9 | 9/9 — **produced** | 9/9 — **produced** |
| | ordinary blocks | 46/55 (9) | 55/55 — **produced** | 55/55 — **produced** |
| | all cases | 174/198 (24) | 195/197 (2) — **partly produced** | 194/197 (3) — **partly produced** |
| **repeat-header** | GE | 0/332 (332) | 0/332 — **not produced** | 0/320 — **not produced** |
| | GS | 0/334 (334) | 0/334 — **not produced** | 0/334 — **not produced** |
| | GT | 0/26 (26) | 0/26 — **not produced** | 0/25 — **not produced** |
| | GI | 0/727 (727) | 0/727 — **not produced** | 0/727 — **not produced** |
| | ordinary blocks | 0/534 (534) | 0/534 — **not produced** | 0/525 — **not produced** |
| | all cases | 0/1 953 (1 953) | 0/1 953 — **not produced** | 0/1 931 — **not produced** |
| **keep-total** | GE | 75/78 (3) | 76/78 (2) — **partly produced** | 76/78 (2) — **partly produced** |
| | GS | 7/7 | 7/7 — **produced** | 7/7 — **produced** |
| | GT | 2/2 | 2/2 — **produced** | 2/2 — **produced** |
| | GI | 2/2 | 2/2 — **produced** | 2/2 — **produced** |
| | ordinary blocks | 48/48 | 48/48 — **produced** | 48/48 — **produced** |
| | all cases | 134/137 (3) | 135/137 (2) — **partly produced** | 135/137 (2) — **partly produced** |

The K1 control block holds one group and no closed group: its header is
produced in every configuration (1/1) and no other instance is counted.

**keep-header.** With the break moved before the group's header whenever its
first row does not fit, the header was still left alone in two runs of its own
configuration — **GE65** and **GE66**, where G0003's single row is between 0.90
and 1.00 of a page and cannot be split, so the header and the row cannot share
a page. All 22 other contested instances (the instances not produced without
the behaviour) are produced, including every case where the header had been
left alone in `none` on G0004 (GE52–GE58, GE62–GE63), on G0002 (GTF300u) and
on G0004 in the ordinary blocks. With all three together a third GE run,
**GE64**, leaves G0003's header alone, one produced instance fewer than the
union of the single configurations. Every other case is produced.

**repeat-header.** The repeated header was **never printed**: 0 of 1 953
instance pages in its own configuration and 0 of 1 931 with all three, in every
case, with no spurious line anywhere. The earlier two-run probe found the same:
**0 of 47** instance pages on GS1200 and **0 of 8** on GE52. Its diagnostic
names the cause in the library's own stylesheet: Paged.js 0.4.3 sets
`content: unset` on the `::before` of every continuation fragment (line 27 361
of the vendored file), a selector more specific than the fixed rule, so the
generated "(cont.)" header is never printed. The class is **not produced by
this method**; no other method was attempted.

**keep-total.** With the break moved before the group's last row when its total
does not fit, 1 of the 3 contested instances is produced: G0003's total in
**GE61** now prints on the page of its last row (in `none` it opened page 2).
The two others remain: **GE65** and **GE66**, where the header moved earlier by
the same mechanism already occupies the page, and G0003's total still prints on
the following page (total page 3, last row page 2). With all three together the
same two remain, one page later (total page 4, last row page 3). Every other
case is produced.

### Interactions

Four runs differ between all three together and the single behaviours:

- **GE61** — all three print 11 pages; keep-header alone and repeat-header
  alone print 10.
- **GE64** — all three print 12 pages against 11 for each single behaviour,
  and G0003's header is kept with its row by keep-header alone but not with all
  three: one instance produced alone and not together.
- **GE65** and **GE66** — all three print 12 pages against 11 for
  repeat-header and keep-total alone.

The text check, the page accounting and the group check stay exact and clean in
all four.

### Cost against the same blocks without the behaviour

Pages are unchanged in 298 of the 305 runs. Seven runs print **one page more**
and none prints fewer: keep-total GE61 and all-three GE61; all-three GE64;
keep-header GE65 and all-three GE65; keep-header GE66 and all-three GE66.

The table gives medians of the three repetitions (r1–r3) of the four repeated
blocks; layout and total time in seconds, the Chromium process-tree peak in
MiB. The container running peak is not shown here: it is a running maximum of
the stage container, not a per-run figure.

| Block | Pages (every configuration) | Layout s median: none / keep-header / repeat-header / keep-total / all-three | Total s median: none / keep-header / repeat-header / keep-total / all-three | Chromium peak MiB: none / keep-header / repeat-header / keep-total / all-three |
|-------|-----------------------------|------------------------------------------------------------------------------|-------------------------------------------------------------------------------|---------------------------------------------------------------------------------|
| B1000 | 36 | 0.87 / 0.81 / 0.89 / 0.78 / 0.87 | 2.91 / 2.54 / 2.66 / 2.30 / 2.44 | 539 / 540 / 539 / 540 / 538 |
| GS2400 | 81 | 1.91 / 1.79 / 1.89 / 1.83 / 1.95 | 4.38 / 4.63 / 4.36 / 4.23 / 4.62 | 753 / 754 / 809 / 755 / 807 |
| X1M | 224 | 4.07 / 4.09 / 4.14 / 4.23 / 4.44 | 7.85 / 7.90 / 8.44 / 9.23 / 9.42 | 821 / 821 / 848 / 817 / 848 |
| B4000 | 143 | 3.56 / 3.56 / 3.57 / 3.69 / 3.63 | 7.59 / 8.21 / 7.82 / 7.99 / 7.75 | 819 / 819 / 795 / 817 / 800 |

Every per-run value, with its ratio to the run without behaviours, and the
values of `spikes/12-group-continuity` for the same runs without behaviours set
beside as reference, are in `results/summary.json` (costs) and
`results/runs.csv`.

### The page images

`results/pages/` holds the eleven images the run requires:

- `GE52-none-page001.png` and `GE52-keep-header-page001.png` — G0004's header
  alone at the end of page 1, and the same page after the break moved before
  the header.
- `GE61-none-page001.png`, `GE61-none-page002.png`,
  `GE61-keep-total-page001.png`, `GE61-keep-total-page002.png` — G0003's total
  opening page 2 without the behaviour, and kept on its row's page with it.
- `GS1200-none-page002.png` and `GS1200-repeat-header-page002.png` — a
  continuation page of G0005, with no repeated header in either.
- `GTF300u-all-three-page008.png` — G0002's header page with its over-tall
  first row, all three behaviours on.
- `GE66-keep-header-page002.png` — G0003's header alone, the remaining
  keep-header violation.
- `GE66-keep-total-page003.png` — G0003's total one page after its last row,
  the remaining keep-total violation.

### What these results say, and do not say

Each class describes what these runs printed with this method, these blocks,
this Paged.js and this Chromium. **No product decision is taken here.** Nothing
is concluded beyond the measured runs; the conditions that were not measured
are listed in the next section.

Within these runs: **keep-header** was produced everywhere except the GE cases
where the group's single row cannot share a page with its header (GE65 and GE66
alone; GE64, GE65 and GE66 with all three); **repeat-header** was not produced
by this method anywhere; **keep-total** was produced except in GE65 and GE66.
In the 301 runs that are not planted controls, every row, every group header
and group total, and every page subtotal and chain stayed intact.

## Not measured here

- **Table markup**, in any form: `spikes/10-pagedjs-table-strict-accounting`
  measured Paged.js 0.4.3 with rows split before layout in table markup; tall
  rows silently lost text at every continuation-part boundary, and the grid
  markup kept every row and character.
- **Groups crossing block boundaries** — each run lays out one block; a block
  that ends inside a group prints no total for it.
- **Groups under native Chromium layout** with the two-pass accounting of
  `spikes/09-two-pass-page-subtotals`.
- **Other methods** for the same behaviours, other split settings, fonts,
  templates, Paged.js or Chromium versions.

## Licences

Paged.js 0.4.3 (MIT) and Playwright 1.63.0 (MIT) as in the earlier
experiments; poppler-utils (GPL) only in the check image, never shipped.
