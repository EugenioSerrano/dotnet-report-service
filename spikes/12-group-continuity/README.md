# Group-continuity experiment — groups crossing pages, rows split before Paged.js, grid markup

Throwaway measurement harness. It lays out blocks whose **groups cross page
boundaries** with Paged.js 0.4.3 in **grid markup**, after the in-page
pre-split of rows taller than a page, inside the PDF base image. It checks
every PDF with checks that never read the layout library's own output (its
page, console or completion signal):

- the text-integrity check of `spikes/07-native-tall-rows`, unchanged;
- the strict accounting check of `spikes/10-pagedjs-table-strict-accounting`,
  unchanged: from the PDF alone, the rows of every page, the page subtotals and
  the brought/carried-forward chain recomputed from the source amounts;
- a **group check** (new here): group headers, group subtotals, row membership
  and the block total, with every expected value computed from the source
  data;
- **placement observations** (never pass or fail): where headers and group
  subtotals print relative to page ends.

No decision is taken here.

## Hypothesis

When groups cross pages in grid markup, with rows split before layout:

1. every group header prints with its source text, in source order;
2. every group subtotal prints once, with the value its rows sum to;
3. every row prints inside its own group;
4. the page subtotals and the chain stay exact on every page.

Where headers and subtotals land relative to page ends is described, not
judged.

**What the group-subtotal check measures.** The "Group total" text is written
into the page from the fixture; Paged.js does not compute it. The group check
therefore shows whether that **static text survives pagination**: printed
once, unaltered, with the value the source data gives. The engine's own
arithmetic is the page subtotal and the brought/carried-forward chain, written
by the accounting script after layout. Those page values are the check of
what the layout computes. A row that continues across pages is counted once,
in the subtotal of the page where it ends; the earlier pages carry a
"(continues)" mark.

## Scope and exclusions

- **Grid markup only.** Table markup is excluded:
  `spikes/10-pagedjs-table-strict-accounting` measured Paged.js 0.4.3 with rows
  split before layout in table markup. Tall rows silently lost text at every
  continuation-part boundary (part limits 0.75 and above). The repeated-text
  tall rows lost every row after the tall one. The grid markup kept every row
  and character with exact page accounting. No table run is made here, not
  even as a comparison.
- **Open gaps, not measured here:**
  - **group continuity across blocks** — how a block that starts inside a
    group prints the group's header and total is a product rule, and it needs
    the assembly path. The blocks here start at a group's first record. A
    block that ends inside a group prints no total for it, as the shared block
    construction prints it;
  - **groups under native Chromium layout with the two-pass accounting** of
    `spikes/09-two-pass-page-subtotals`.
- No product rule is chosen: whether a header should stay with its first row,
  whether headers repeat, where a total or mark should go. No stylesheet rule
  about groups or page breaks was added.

## How to run

### Prerequisites

- The local registry `report-service-dev-registry` on `127.0.0.1:5055`
  serving `report-service-pdf-base:1.63.0-10.0.13-noble`.
- The text-integrity checker image `text-integrity-check:local` of
  `spikes/07-native-tall-rows`.
- The grouped dataset `samples/datasets/header-items-v1.grouped.10k.json` and
  the shared fixtures (`spikes/09-two-pass-page-subtotals/results/fixtures.sha256`).
- A quiet engine for the measured stage: `docker ps` lists only the registry.

### Commands (product repository root, PowerShell)

`$wslRoot` is the WSL form of the current directory
(`X:\...\<repository>` becomes `/mnt/x/.../<repository>`); with Docker Desktop
on Windows paths use `${PWD}`.

```powershell
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
$raw = "spikes/12-group-continuity/results/raw"

# Identity of every reused file and result (host, Git Bash): every line two equal hashes.
sh spikes/12-group-continuity/check-identity.sh > spikes/12-group-continuity/results/identity.txt

# Images (not measurements). The base digest comes from the registry; there is no default.
docker buildx build --progress=plain --load --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/12-group-continuity/Dockerfile -t group-continuity-spike:local .
docker buildx build --progress=plain --load -f spikes/12-group-continuity/Dockerfile.account -t group-continuity-check:local .

# Fixtures (not a measurement): reproduces the shared B100 and B1000 first, builds twice, writes $raw/fixtures.
dotnet <check>/GroupContinuityCheck.dll group-fixtures --dataset samples/datasets/header-items-v1.grouped.10k.json `
  --sums samples/datasets/SHA256SUMS --reference <shared fixture folder> --tall <shared fixture folder>/T300u.json --out $raw

# The measured stage (quiet engine): 64 runs.
docker run --name group-continuity-spike-<ts>-stage1 --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/$raw/fixtures:/fixtures:ro" group-continuity-spike:local run --stage 1 --fixtures /fixtures --out /out --budget-seconds 3600
docker cp group-continuity-spike-<ts>-stage1:/out "$raw/stage1"; docker rm group-continuity-spike-<ts>-stage1

# Checks (not measurements; inputs read-only): the text check and its own controls on the base
# s1-grid-s2-G200-split-t1.00-f0.90-off-r1; then, in group-continuity-check:local:
#   account, groups, controls, anchors (the committed results of spikes/08-pagedjs-pre-split), verdict.
```

`run --matrix quick` (4 runs) is the development loop and contract test; its
results are not evidence.

### Caps, classes, ceilings

- **Caps per run:** 300 s in the run's own process, and the process tree is
  killed at 330 s. The container runs with `--memory 4g --memory-swap 4g`, and
  `oom_kill` is read before and after each run.
- **Classes:** `ok`, `timeout`, `killed-wall-clock`, `oom-killed`, `crashed`,
  `error`, `not-run`.
- **After a cap, in the size series T300u → X64K → X1M,** the larger sizes are
  not run.
- **Near-ceiling:** if a repeated fixture's first repetition takes over 240 s,
  or its Chromium process tree goes over 3.2 GiB, its remaining repetitions are
  not run.
- **Disk:** at most 4 GiB of stage output.
- **Budget:** 1 h.
- **Memory figures:** per run, the Chromium process-tree peak, and
  `cgroupMemoryPeakBytes` — the **container running peak** (the stage
  container's peak up to that run, not a per-run figure).
- **Run ids:**
  - `s1-grid-` followed by the full id the grid experiment of
    `spikes/08-pagedjs-pre-split` gave the same run (for example
    `s1-grid-s2-X64K-split-t1.00-f0.90-on-r1`);
  - `s1-grid-new-…` for fixtures new here.

## Fixtures

They are built from the grouped dataset with the shared block construction,
from a start offset that is always a group's first record. Before writing,
the builder reproduces the shared B100 and B1000 byte for byte; the whole set
is built twice, with an identical `SHA256SUMS`
(`results/fixtures.sha256`, `results/group-fixtures.json`).

| Fixture | Case | Records | Groups |
|---------|------|---------|--------|
| GE30 … GE66 | a group starting near a page end | dataset records 401–650 (250) | G0003 (1 record), G0004 (192), G0005 (57, open at the block end) — the one G0003 row's description is *n* lines `edgeline01` … `edgeline<n>`, so G0004's header moves down one line per step |
| GS1200 | a group spanning several pages | 594–1959 (1 366) | G0005 (160), G0006 (1 200), G0007 (6, open) |
| GS2400 | a group spanning many pages | 3152–5559 (2 408) | G0012 (2 400), G0013 (8, open) |
| GTL300u / GTF300u | an over-tall row as a group's last / first row | 0–259 (260) | G0001 (182), G0002 (78, open) — row 182 / row 183 carries the tall text of the shared T300u |
| T300u, X64K, X1M, G200 | over-tall rows inside a group (row 50, and 120 in G200, of G0001) | shared | |
| B100, K1, F26K-a, B1000, B4000 | controls, the ordinary anchor, the top of the ordinary bracket | shared | |

## Results

**Environment:**
- PDF base `sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d`;
- Chromium 153.0.8010.12, Playwright 1.63.0, .NET 10.0.13, Paged.js 0.4.3
  unchanged;
- trigger 1.00 and part limit 0.90;
- 16 CPUs, with the container capped at 4 GiB and swap off.

**The stage:** 64 runs, all `ok`, 159 s of driver wall clock. No cap, ceiling
or out-of-memory event.

### The checks and their controls (all behave)

- **Text check:**
  - B100 and K1 pass;
  - NC-W, NC-L and NC-R on B100, and NC-F on F26K-a, each give exactly their
    one expected failure;
  - E1–E4 behave on `s1-grid-s2-G200-split-t1.00-f0.90-off-r1`;
  - E5–E8 are not evaluable (the text check's controls cannot take real
    marks).
- **Strict accounting:**
  - B100 and K1 fail nothing;
  - NC-R fails exactly rows (sequence 51 missing) and the chain on its last
    page; NC-W, NC-L and NC-F fail nothing;
  - four faults injected into the parsed G200 run each fail exactly their set:
    a page subtotal + 0.01, a brought-forward value + 0.01, a removed
    "(continues)" mark, a removed row.
- **Character check:** it agrees with the text check row by row on 63 runs.
  The 64th, G200 without the split, has more failures than the text check
  lists, so it was not compared.
- **Group check:** B100, K1 and B1000 fail nothing. On a parsed copy of
  `s1-grid-s1-B1000-split-t1.00-f0.90-on-r1`, five faults each fail exactly
  their rule:

  | Fault | Failure |
  |-------|---------|
  | G0002's header removed | header-missing |
  | G0001's total + 0.01 | total-wrong |
  | G0001's total duplicated | total-duplicated |
  | G0001's total moved above its last row | row-outside-group |
  | the block total + 0.01 | block-total-wrong |

- **Probe on the committed PDFs of `spikes/08-pagedjs-pre-split`.** On the
  three with the split, every row prints and the chain closes on the block
  total. On G200 without the split, rows 51–200 are absent.
- **The 19 runs shared with `spikes/08-pagedjs-pre-split`** match it field by
  field with **0 differences**: class, pages, parts, the library's rule lines,
  the page body hash and length, the text verdict and content hash.

### Groups, page by page

| Case | Runs | Headers | Group subtotals (static text) | Membership, block total | Page subtotals and chain |
|------|------|---------|-------------------------------|-------------------------|--------------------------|
| Group starting near a page end (GE30–GE66, and two footer-off twins) | 39 | all present, in order | all printed once, exact | exact | exact on every page |
| Group spanning several / many pages (GS1200 on and off, GS2400) | 3 | all | all | exact | exact |
| Groups containing over-tall rows (T300u, X64K, X1M, G200 on and off, GTL300u, GTF300u) | 7 | all | all | exact | exact |
| Anchors and ordinary blocks (B100, K1, B1000 ×7, B4000) | 10 | all | all | exact | exact |

**59 runs confirmed** — every check passes. The other 5 are:
- the four HTML controls;
- G200 without the split, the **baseline exception**. It shows the known
  giant-row signature: rows 51–200 lost, with group G0002's header, G0001's
  total and the block total missing with them. It is not a group-continuity
  failure.

### Placement (observations, no pass or fail)

- **A group header left alone at the end of a page** (its first row on the
  next page):
  - GE52–GE58: G0004's header is the last line of page 1
    (`png/…GE52…page001.png` against `…GE51…page001.png`, where the header
    still has rows below it);
  - GE62–GE66: G0003's header is alone at the end of page 1, its one row having
    moved to page 2 — and in GE62–GE63 G0004's header is alone at the end of
    page 2 too;
  - B1000 (all 7 runs) and B4000: G0004 on page 15, and in B4000 also G0008 on
    page 80;
  - GTF300u: G0002's header ends page 7, and the group's over-tall first row
    starts on page 8 (`png/…GTF300u…page007.png`).
- **A header repeated on a continuation page:** never, in any run. Every
  further page of a multi-page group prints its rows without a header —
  GS1200's G0006 on 43 pages, GS2400's G0012 on 80, X1M's G0001 on 223
  (`png/…GS1200…page010.png`).
- **A group total separated from its last row** (the total as the first
  content line of the next page): GE61, GE65 and GE66, for G0003
  (`png/…GE61…page002.png`). In every other run, every total prints on the
  page of its group's last row.
- **Totals after over-tall rows:** GTL300u's G0001 total prints on page 11, the
  page of the tall row's last part (`png/…GTL300u…page011.png`).
- **"(continues)" marks:** only on the pages of a split row, for that row's
  group.
- **Pages without detail:** only free-text pages, each with page subtotal
  0.00.

The page subtotals were exact on every page of every footer-on run, including
pages that end a group, start a group, or hold a lone header.

### Cost (per run)

| Run | Pages | Layout | Chromium tree peak | Container running peak |
|-----|-------|--------|--------------------|------------------------|
| GE52 | 10 | 0.62 s | 521 MiB | 445 MiB |
| GS1200 | 48 | 1.08 s | 552 MiB | 514 MiB |
| GS2400 | 81 | 2.10 s | 753 MiB | 759 MiB |
| X1M | 224 | 4.33 s | 813 MiB | 874 MiB |
| B4000 | 143 | 3.81 s | 821 MiB | 910 MiB |

Every figure is in `results/runs.csv`.

## Conclusion

Within what was measured — Paged.js 0.4.3, Chromium 153.0.8010.12, this base
image, these stylesheets, grid markup, trigger 1.00 and part limit 0.90,
groups crossing pages within one block:

- every group header and every group subtotal printed once, unaltered, with
  the values the source data gives;
- every row printed inside its own group;
- the page subtotals and the chain were exact on every page.

On placement, Paged.js:
- left a header alone at the end of a page whenever the header fitted and
  its first row did not;
- never repeated a header on a continuation page;
- moved a group total to the next page when it did not fit after its last row.

Nothing is generalised beyond these conditions. Continuity across blocks and
native layout with two-pass accounting are not measured. No decision is
taken here.

## Packages and licences

| Component | Licence | Notes |
|-----------|---------|-------|
| Microsoft.Playwright 1.63.0 | MIT | The only package (harness); pinned in this folder's `Directory.Packages.props` |
| Chromium headless shell 153.0.8010.12 | BSD-3-Clause | From the PDF base image; notices inventoried in `spikes/02-pdf-layout/README.md` |
| Paged.js 0.4.3 | MIT | Vendored in `spikes/02-pdf-layout`, linked unchanged |
| poppler-utils 24.02.0 | GPL-2 or GPL-3 | Only in the check images; never shipped |

## Files

- `GroupLayoutSpike/` — the harness:
  - `Plan.cs` and `Program.cs`;
  - linked unchanged: the runner and driver of
    `spikes/10-pagedjs-table-strict-accounting`, the grid builder, run record,
    pre-split script and stylesheet of `spikes/08-pagedjs-pre-split`, and the
    files of `spikes/02-pdf-layout`;
  - the assembly is named `PagedMarkupSpike`, the name the linked driver
    starts.
- `GroupContinuityCheck/` — the check:
  - `GroupFixtures.cs`, `GroupCheck.cs`, `GroupControls.cs` (its accounting
    fault code is that of `spikes/10-pagedjs-table-strict-accounting`),
    `Commands.cs`, `Verdict.cs`, `Program.cs`;
  - linked unchanged: the strict check, character check and record reader of
    `spikes/10-pagedjs-table-strict-accounting` and the files they need.
- `Dockerfile`, `Dockerfile.account` and their allow-list ignore files;
  `check-identity.sh`.
- `results/`:
  - `summary.json` — verdict, controls, statements, placement, costs,
    outcomes;
  - `groups.json`, `accounting.json`, `integrity.json` — compact copies;
  - `controls.json`, `extraction-controls.json`, `anchors.json`, `probe.json`;
  - `group-fixtures.json`, `fixtures.sha256`, `plan.json`, `runs.csv`,
    `environment.json`;
  - `identity-start.txt`, `identity.txt`;
  - 4 PDFs and 8 page images.
- `results/raw/` — local output, not committed.
