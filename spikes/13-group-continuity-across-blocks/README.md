# Groups crossing block boundaries in a report assembled from parts

This folder measures what prints at a block boundary when a report of grouped
records is laid out in several separately rendered parts and assembled into one
numbered PDF: group headers, group totals, row membership, the page subtotal and
its brought/carried-forward chain, and the global page number. It also counts,
per case, whether each of three group behaviours chosen by the human for the
product is produced at the boundary. It decides no product rule.

The layout path is the grid one: Paged.js 0.4.3 with rows taller than a page
split before layout, as in `spikes/12-group-continuity`, whose fixture builder,
group check, grid runner and driver, and the strict accounting check of
`spikes/10-pagedjs-table-strict-accounting`, are linked unchanged. The assembly
(merge, global page stamp and its verifier) is the measuring image of
`spikes/06-pdf-assembly-chromium-parts`, unchanged. The new code is this
folder's part builder, one derived copy of the in-page accounting script that
accepts an opening amount, the report-level checks, their controls, the boundary
observations and the verdict.

Everything below was produced on the reference environment: the PDF base image
`report-service-pdf-base:1.63.0-10.0.13-noble`
(`sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d`),
Playwright 1.63.0, Chromium 153.0.8010.12, .NET 10.0.13, 16 CPUs, 4 GiB of
memory per run container.

## 1. Hypothesis

When a report is cut into parts of a fixed number of records so that groups
cross the block boundaries, and every part is laid out on its own:

1. the hand-over of the chain and the open group from the source data keeps the
   page subtotals and the brought/carried chain exact on every page of the
   assembled report, including the first page of every part;
2. every group header prints once in source group order, and every closed
   group's total prints once, with the sum of all its records from every part;
3. every row stays inside its own group and every part's block total equals its
   own rows;
4. the assembled report carries "Page i of N" globally on every page, in part
   order;
5. at the boundary, the three chosen behaviours can be observed and counted:
   a group header never left alone at a page end, the header repeated with
   "(cont.)" on continuation pages, and the group total kept with its last row.

The hypothesis is measured, not assumed: the same reports are also printed as
independently laid-out parts (the baseline), which shows what the shared block
construction does at a boundary when nothing is handed over.

## 2. The reports, their sizes and block limits

A report is the dataset records `o ... o + R - 1`, where `o` is a group's first
record and `o + R - 1` a group's last record, cut into parts of `L` records (the
block limit); the last part holds the remainder. A row is never cut between
parts. Part `k` of report `XB-...` is the fixture `XB-...-P<k>`, laid out as one
run of its own (one fresh process, one browser) with the shared block
construction, which prints the title, the column caption and the part's
"Block total".

| Report | Case | Records | Block limit | Parts | Pages assembled |
|--------|------|---------|-------------|-------|-----------------|
| `XB-L180-H1` | split near a group's end | 0 ... 593 (594) | 180 | 4 | 23 |
| `XB-L181-H1` | near the end (1 row left) | 0 ... 593 | 181 | 4 | 23 |
| `XB-L182-H1` | boundary between groups | 0 ... 593 | 182 | 4 | 23 |
| `XB-L183-H1` | near a group's start | 0 ... 593 | 183 | 4 | 23 |
| `XB-L184-H1` | near the start (+2) | 0 ... 593 | 184 | 4 | 24 |
| `XB-L185-H1` | near the start (+3) | 0 ... 593 | 185 | 4 | 24 |
| `XB-L201-H1` | one-record group at a part end | 0 ... 593 | 201 | 3 | 23 |
| `XB-TL150-H1` | over-tall row as a part's last row | 0 ... 593 | 150 | 4 | 27 |
| `XB-TF150-H1` | over-tall row as a part's first row | 0 ... 593 | 150 | 4 | 27 |
| `XB-W250-H1` | group spanning more than one whole block | 594 ... 1953 (1 360) | 250 | 6 | 50 |
| `XB-G500-H1` | group spanning whole blocks, larger | 3152 ... 5551 (2 400) | 500 | 5 | 83 |
| `XB-PB-H1` | block boundary on a page boundary | 594 ... 1953 | 242 | 6 | 51 |
| `XB-ALL-H1` | the whole dataset | 0 ... 9999 (10 000) | 1 000 | 10 | 354 |
| `XB-L185-H0` | baseline: independent parts | 0 ... 593 | 185 | 4 | 24 |
| `XB-W250-H0` | baseline: independent parts | 594 ... 1953 | 250 | 6 | 50 |

The boundaries and the open group at each are computed from the source alone and
recorded in `results/report-fixtures.json`:

| Report | Boundary classes (before part, open group, records before/after) |
|--------|-------------------------------------------------------------------|
| `XB-L180-H1` | 2: near-end G0001 (180/2); 3: middle G0002 (178/41); 4: middle G0004 (138/54) |
| `XB-L181-H1` | 2: near-end G0001 (181/1); 3: middle G0002 (180/39); 4: middle G0004 (141/51) |
| `XB-L182-H1` | 2: between groups; 3: middle G0002 (182/37); 4: middle G0004 (144/48) |
| `XB-L183-H1` | 2: near-start G0002 (1/218); 3: middle G0002 (184/35); 4: middle G0004 (147/45) |
| `XB-L184-H1` | 2: near-start G0002 (2/217); 3: middle G0002 (186/33); 4: middle G0004 (150/42) |
| `XB-L185-H1` | 2: near-start G0002 (3/216); 3: middle G0002 (188/31); 4: middle G0004 (153/39) |
| `XB-L201-H1` | 2: middle G0002 (19/200); 3: between groups (G0003 closes part 2) |
| `XB-TL150-H1`, `XB-TF150-H1` | 2: middle G0001 (150/32); 3: middle G0002 (118/101); 4: middle G0004 (48/144) |
| `XB-W250-H1`, `XB-W250-H0` | 2 ... 6: middle G0006 (90/1110, 340/860, 590/610, 840/360, 1090/110) |
| `XB-G500-H1` | 2 ... 5: middle G0012 (500/1900, 1000/1400, 1500/900, 2000/400) |
| `XB-PB-H1` | 2 ... 6: middle G0006 (82/1118, 324/876, 566/634, 808/392, 1050/150) |
| `XB-ALL-H1` | 2: middle G0006; 3: G0007; 4: G0011; 5: G0012; 6: G0012; 7: G0015; 8: G0019; 9: G0024; 10: G0028 |

The block limit of `XB-PB-H1` is the one layout-dependent choice in the set. It
cannot be computed from the source: the report must end a part exactly at a page
end, so 22 layouts of part 1 (block limits 240 ... 261) were run before the
measured stage and the page counts read from their PDFs. The smallest block
limit whose next record starts a new page is **242** (pages: 240-242 -> 8,
243-261 -> 9); `results/calibration.json` holds the 22 counts. This is a
**documented exception** to the rule that no layout result chooses a fixture: it
was fixed before the measured stage, and it is recorded, not measured.

`XB-PB-H1` part 1 ends at global page 8, and that page's content fills 98.9 % of
the page area (measured from the PDF word boxes) - the boundary does coincide
with a page end.

## 3. The hand-over: how the chain and the group state reach the next part

This is a mechanism of the harness, not a product rule. Everything is computed
from the source data before any layout:

- each part's opening brought-forward is the sum of the amounts of every report
  record before the part, in decimal with two places;
- the open group at a part's start is the group of the part's first record when
  that record is not its group's first;
- each group's total is the sum of all its records in the report.

A part never cuts a row, so every row ends on a page of its own part, and by the
end-page rule a part's last "Carried forward" equals its opening plus its rows'
amounts - the next part's opening. No layout result is needed, so the parts can
be laid out in any order (they were laid out one after another).

**H1** - the hand-over:

1. **Chain.** The page builder wraps the linked grid builder and writes
   `data-brought-forward="<opening>"` on the page's root element; this folder's
   derived copy of the in-page accounting script starts its chain there. The
   value comes from `handover.json`, which the fixture builder writes and the
   fixture pin covers.
2. **Continuation header.** When a part starts inside a group, its first group
   start row prints `<id> - <name> (cont.)` instead of `<id> - <name>`.
3. **Group total.** Where a group ends, its group-end row prints the whole
   group's sum instead of the sum of that part's rows only.

**H0** - the baseline: the shared block construction exactly as it is, with no
attribute; each part's chain starts at 0.00, a part that starts inside a group
prints its plain header again, and a group that ends inside a part prints that
part's rows only. This is what independently laid-out parts print.

**The cost risk.** The hand-over is cheap here only because everything it needs
is in the data. A whole group's total must be known before the part that closes
the group is built: a group spanning many parts (G0012: 2 400 records, 5 parts)
needs a read-ahead or a second pass over the data. Every opening needs the sum
of everything before it, a sequential pass. Any rule that depends on **layout** -
a header kept with its first row at a page end, or a total kept with its last
row - would make part *k* + 1 wait for part *k*'s layout and serialise the
report. This experiment measures the data-level cost (the builder's hand-over
computation: **11.09 ms** for all 75 part fixtures) and the per-report sums and
maximums of the part times (section 8); the layout-dependent part is a stated
risk, not a finding.

## 4. The checks and what each one reads

The checks read only the assembled PDFs (through poppler), the source fixtures
and the dataset, and each run's id, class, PDF path and the engine's escaping
counters. They never read the page DOM, the console, the completion signal or
the in-page rule lines, and never `handover.json`; the hand-over values are
judged against values recomputed from the source.

- **Numbering and order.** The numbered merge (PDFsharp merge with the global
  stamp in one pass) is checked with the folder 06 verifier: structure, page
  boxes, order (every page's content words equal its source part page's),
  numbering ("Page i of N" in the numbering area) and pixels (0 changed outside
  the numbering area). The unnumbered merge (qpdf) is checked the same way with
  the numbering expectation off. Both pass on all 17 reports.
- **Content checks read the unnumbered merge.** The text oracle's page-header
  item requires the left half of each page's header band to read exactly the
  fixture title and the right half exactly "Page k of n". A centred global stamp
  straddles the middle, so a numbered page fails that item by construction. The
  unnumbered merge is cut back into one PDF per part (`pdfseparate` +
  `pdfunite`) and the text oracle, the report accounting and the group rules
  read the slices.
  The **numbered output is checked only through the verifier's order and pixel
  items** (and its numbering item for the stamp); that bridge proves every
  page's content words equal those of its source part page, so the content that
  was checked is the numbered report's content outside the numbering area.
- **Text (folder 07, unchanged), per slice:** TI-1 ... TI-8 against the part
  fixture.
- **Report accounting - XA.** The rules of folder 02, read on the concatenated
  report; expected amounts come from the source rows:
  - XA-1 rows: every report record prints exactly once, across all parts, in
    order;
  - XA-2 end-page subtotals: every page's printed subtotal equals the sum of the
    amounts of the rows that end on it (a row that continues across pages is
    counted once, in the subtotal of the page where it ends; the earlier pages
    carry a "(continues)" mark);
  - XA-3 chain: page 1 brings 0.00 forward, every page brings forward what the
    previous page carried, carried = brought + subtotal, and the last page
    carries the report's total. This rule stops at its first failing page; every
    boundary is counted separately by PB-7 below;
  - XA-4 visible amounts; XA-5 "(continues)" marks.
- **Group rules - RG**, expected values from the source fixtures' items and
  group start rows only (never from their group-end or block-total text):
  - RG-1 headers: one plain header per group, in source group order; a
    continuation header names the group of the row printed right after it; no
    header names a group the report does not hold;
  - RG-2 group totals: every closed group has exactly one total line in the
    whole report, equal to the sum of all its records from every part; an open
    group has none;
  - RG-3 membership: every row lies after its group's plain header and before
    its group's total, with no row of another group between them, across part
    boundaries;
  - RG-4 part totals: each part's printed "Block total" equals the sum of that
    part's records.
- **What RG-2 does and does not measure.** The group-total text is static
  fixture text: the hand-over writes it into the closing part. RG-2 therefore
  shows that the hand-over's value is right against the source and that the text
  survives pagination and assembly once in the report. It does **not** show the
  layout engine computing a group total. **The engine check is XA-2/XA-3**: page
  values computed by the in-page script from the opening amount.
- **Folder 10's strict check (`account`), unchanged, per run.** A verdict is
  read only where the run's opening is 0.00 (the parity runs, every H1 part 1
  and every H0 part). Elsewhere its chain rule necessarily differs because the
  opening is not 0.00; those entries are recorded and never read as failures.
- **Boundary observations - PB-1 ... PB-7**, never pass or fail, counted per
  report and per case:
  - PB-1 a header left alone at a page end, with the boundary pages counted
    apart; behaviour 1 at the boundary is "produced" when a part's last page does
    not end with a header that has no row of its group after it on that page;
  - PB-2 header repetition: whether part *k* + 1's first content line is the
    continuation header of the open group; also the pages holding a group's rows
    but no header line (inside parts, and at part starts); behaviour 2 at the
    boundary is counted, and so is behaviour 2 on in-part continuation pages;
  - PB-3 total placement: for each group, the page of its total against the page
    of its last row, with the boundary cases listed apart; behaviour 3 at the
    boundary is "produced" when the total is on its last row's page;
  - PB-4 the last page of each part: lines held, content height used against the
    page area, and whether it holds only group or total lines;
  - PB-5 pages without detail (folder 12's P-5);
  - PB-6 the boundary itself: class, open group, records before and after;
  - PB-7 the chain at **every** boundary: part *k* + 1's first-page "Brought
    forward" against part *k*'s last-page "Carried forward", both read from the
    slices' footer bands - continuous or broken, with the two amounts. It is an
    observation with its own controls, never a verdict; it exists because XA-3
    stops at its first failing page.

## 5. Controls, run before any result was read

The instrument is trusted only when every control's observed failures are
exactly its expected set. The run of record has **43 items in 7 groups** and
reported `trusted: true`:

| Group | Items | Result |
|-------|-------|--------|
| text | 14 | 10 behave; E5 ... E8 not evaluable |
| slice transparency | 1 (all 84 slices) | 84 of 84 slices equal to their part's own PDF (pages, verdict, failure list, content hash) |
| agreement with folder 10's strict check | 1 (31 documents) | XA-1 ... XA-5 equal rules 1-5 on every document whose opening is 0.00 |
| report positive | 6 | the six one-part parity reports fail no XA or RG rule |
| report one-fault-per-rule | 13 | XC-1 ... XC-6 and GXR-1 ... GXR-7 each fail exactly their set |
| numbering | 3 | positive (17 reports numbered and unnumbered pass), NX-1, NX-2 |
| hand-over probe | 5 | all five known answers hold |

- **Text controls.** B100 and K1 pass. The four **negative controls** each give
  exactly their one expected failure: `NC-W` text-dropped (row 50), `NC-L`
  text-repeated (row 50), `NC-R` row-lost (row 51) and `NC-F` text-dropped
  (free-after). E1 ... E4 behave on the footer-off base; **E5 ... E8 are not
  evaluable**, because the text oracle's own controls add simulated marks and
  evaluate without the footer; their own recorded result is reported beside them
  and not read as a failure of this instrument.
- **Slice transparency.** Every slice of every assembled report has its part's
  page count and the same text verdict, failure list and content hash as the
  text check on the part's own PDF (84 slices, 929 pages).
- **Agreement.** 31 documents read as one-part reports give exactly the pass/fail
  and failure pages of folder 02's rules 1-5 in folder 10's strict check.
- **One failing control per rule.** XC-1 (subtotal + 0.01), XC-2
  (brought-forward + 0.01, exactly the boundary before part 3 broken), XC-3 (a
  "(continues)" removed), XC-4 (a first row removed), XC-5 (part 3's footers
  rewritten as if its opening were 0.00, exactly the boundaries before parts 3
  and 4 broken), XC-6 (an amount + 0.01), GXR-1 (a plain header removed), GXR-2
  (a total + 0.01), GXR-3 (a total duplicated), GXR-4 (a total moved above its
  last row), GXR-5 (a part total + 0.01), GXR-6 (a continuation header naming
  another group), GXR-7 (a continuation header printed plain). Each fails
  exactly its set, nothing else.
- **Numbering controls.** NX-1: the unnumbered report verified as numbered fails
  numbering on every page and pixels inside the numbering area. NX-2: the
  numbered report verified against a manifest with parts 2 and 3 swapped fails
  order at the first global page of the swapped range, and pixels outside the
  numbering area on exactly those pages; structure, boxes and numbering pass.
- **Hand-over probe** (a three-part report, `XB-PROBE`, block limit 183, H1):
  part 2's first "Brought forward" equals part 1's last "Carried forward"
  (4 714 762.77), part 3's equals part 2's (9 003 118.49); G0002 prints its
  plain header once and its continuation header at the start of parts 2 and 3;
  G0001 and G0002 print one total each with their whole-group sums; the
  accounting rules pass; the verifier passes on the numbered and the unnumbered
  probe.

The controls of record are the ones run over the stage's own data
(`results/controls.json`). The earlier quick loop's own controls run is a
development artefact: it reported `trusted: false` because that loop's text
entries and strict accounting entries were `missing` for the stage-only runs,
its report bases were absent and the extraction-control file did not exist yet;
it is kept in `results/raw/quick-controls/` and is not evidence.

## 6. Results per case

Every part of every report is `ok` (97 of 97 runs), both verifications pass on
every report, every slice passes the text oracle, and every report's XA and RG
rules pass - except the two baseline reports, which are reported separately.

| Report | Pages | Outcome | G1 headers | G2 totals | G3 membership, part totals | G4 accounting | G5 numbering |
|--------|-------|---------|------------|-----------|----------------------------|---------------|--------------|
| `XB-L180-H1` | 23 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L181-H1` | 23 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L182-H1` | 23 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L183-H1` | 23 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L184-H1` | 24 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L185-H1` | 24 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-L201-H1` | 23 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-TL150-H1` | 27 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-TF150-H1` | 27 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-W250-H1` r1, r2, r3 | 50 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-G500-H1` | 83 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-PB-H1` | 51 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |
| `XB-ALL-H1` | 354 | confirmed | confirmed | confirmed | confirmed | confirmed | confirmed |

Per case, with the boundary chain (PB-7) and the behaviour counts:

| Case | Reports | Boundaries | PB-7 continuous / broken | Behaviour 1 at boundary (produced / not) | Behaviour 2 at boundary (produced / not) | Behaviour 3 at boundary (produced / not) |
|------|---------|------------|--------------------------|------------------------------------------|------------------------------------------|------------------------------------------|
| 4 - near a group's end | `XB-L180`, `XB-L181` | 6 | 6 / 0 | 6 / 0 | 6 / 0 | 2 / 0 |
| 4 - between groups | `XB-L182` | 3 | 3 / 0 | 3 / 0 | 2 / 0 (the between-groups boundary has no open group) | 1 / 0 |
| 4 - near a group's start | `XB-L183`, `XB-L184`, `XB-L185` | 9 | 9 / 0 | 9 / 0 | 9 / 0 | 2 / 0 |
| 5 - one-record group at a part end | `XB-L201` | 2 | 2 / 0 | 2 / 0 | 1 / 0 (one boundary is between groups) | 2 / 0 |
| 6 - over-tall rows at the boundary | `XB-TL150`, `XB-TF150` | 6 | 6 / 0 | 6 / 0 | 6 / 0 | 0 / 0 (no group closes at a boundary) |
| 7 - groups spanning whole blocks | `XB-W250` r1-r3, `XB-G500` | 19 | 19 / 0 | 19 / 0 | 19 / 0 | 0 / 0 |
| 8 - boundary on a page boundary | `XB-PB` | 5 | 5 / 0 | 5 / 0 | 5 / 0 | 0 / 0 |
| 11 - top of the bracket | `XB-ALL` | 9 | 9 / 0 | 9 / 0 | 9 / 0 | 0 / 0 |

Reading the table: where the situation arises (a group's last rows end a part or
open the next one), behaviour 3 was produced at every count shown; where no
group closes at a boundary the count is 0 / 0 and says nothing. Behaviour 1 was
produced at every boundary. Behaviour 2 was produced at every boundary with an
open group; the two `between` boundaries have no open group and therefore no
continuation header.

**Behaviour 2 is produced by data-level fixture text, not by the layout engine.**
The "(cont.)" header at a part start is written by the fixture builder from the
source data (H1, item 2 above); it is not a repeated header obtained by a layout
mechanism. On continuation pages **inside** a part no header is repeated at all:
0 produced against 341 (`XB-ALL`), 210 (case 7), 59 (case 4 near start), 19
(between groups), 38 (near end), 46 (over-tall rows), 20 (one-record group) and
45 (page boundary) pages that hold a group's rows without a header line.
**Nothing is claimed about the mechanisms that could produce behaviours 1 and
3**: this experiment only counts where they happen; a separate experiment on
group rules (folder 14) is where mechanisms are measured.

**Headers alone.** No part's last page ends with a group header without a row of
that group (`headersAloneOnAPartLastPage`: 0 in every case). Inside parts,
`XB-ALL-H1` holds 3 pages where a header is left alone at a page end (the same
shape `spikes/12-group-continuity` saw inside one block: the library leaves a
header alone when it fits and its first row does not); the other cases hold 0.
This is an observation inside a part, not at a block boundary.

**Totals separated from their last row.** 0 in every report and every case
(`totalsSeparated`): every group's total printed on the page of its last row,
including the groups that close in a part's last rows.

**Pages without detail.** 0 in every report (PB-5).

**The page-coinciding boundary.** `XB-PB-H1` part 1 ends at global page 8 with
98.9 % of the page area used; its content checks, chain and totals are exact,
and all five boundaries are continuous. The calibration that produced block
limit 242 is the documented exception of section 2.

## 7. The baseline: independently laid-out parts

The two H0 reports are the shared block construction with no hand-over. They are
**not expected to pass** and are kept out of every statement above; they show
what the checks see when parts are independent.

| Report | Outcome | Failure classes | PB-7 broken boundaries |
|--------|---------|-----------------|-------------------------|
| `XB-L185-H0` | baseline (independent parts): refuted | XA-3 p9; RG-1 header-duplicated (G0002, G0004); RG-2 total-wrong (G0002, G0004) | 3 of 3 |
| `XB-W250-H0` | baseline (independent parts): refuted | XA-3 p10; RG-1 header-duplicated (G0006); RG-2 total-wrong (G0006) | 5 of 5 |

In words: each part's chain restarts at 0.00, so the first page of every part
after the first breaks the report's chain (XA-3's first failing page is part 2's
first page: 9 and 10). A group that starts before a boundary prints its plain
header again in the next part - 3 plain header lines for G0002 and 2 for G0004 in
`XB-L185-H0`, and 6 for G0006 in `XB-W250-H0` - which RG-1 reports as
`header-duplicated`. A group that ends inside a part prints a total of that
part's rows only: G0002 prints 798 387.06 where its whole-group sum is
5 219 425.10; G0004 prints 722 468.42 against 4 274 354.93; G0006 prints
2 624 867.47 against 29 481 763.64. These are exactly the breaks the hand-over
removes in H1; they are the baseline, not a failure of this experiment.

Their text, numbering and part-total rules still pass: the parts are truthful
about their own rows, and the assembled numbering is exact; what breaks is the
continuity across the boundary.

## 8. Cost

Per run the record holds pages, the layout time, the Chromium process-tree peak
RSS and the container running peak (`cgroupMemoryPeakBytes`; it is the whole
container's peak up to that run, not a per-run figure - `oom_kill` is the
out-of-memory evidence). Per report, `results/summary.json` holds the sum and the
maximum of its parts' **total** run times; the layout-only sums are given here as
well, both in milliseconds:

| Report | Parts | Pages | Layout sum | Layout max | Total sum | Max Chromium peak | Max container running peak |
|--------|-------|-------|------------|------------|-----------|-------------------|----------------------------|
| `XB-L180-H1` | 4 | 23 | 944.2 | 347.8 | 5 876.1 | 469 MiB | 485 MiB |
| `XB-L181-H1` | 4 | 23 | 754.6 | 224.5 | 5 342.9 | 470 MiB | 485 MiB |
| `XB-L182-H1` | 4 | 23 | 761.4 | 213.5 | 5 164.6 | 470 MiB | 485 MiB |
| `XB-L183-H1` | 4 | 23 | 1 019.0 | 341.5 | 5 815.5 | 470 MiB | 485 MiB |
| `XB-L184-H1` | 4 | 24 | 1 097.9 | 358.6 | 6 494.0 | 471 MiB | 485 MiB |
| `XB-L185-H1` | 4 | 24 | 1 112.1 | 365.8 | 6 395.9 | 470 MiB | 485 MiB |
| `XB-L201-H1` | 3 | 23 | 899.3 | 333.6 | 4 880.6 | 482 MiB | 485 MiB |
| `XB-TL150-H1` | 4 | 27 | 978.1 | 313.2 | 5 759.8 | 469 MiB | 485 MiB |
| `XB-TF150-H1` | 4 | 27 | 876.5 | 294.2 | 5 714.1 | 465 MiB | 485 MiB |
| `XB-W250-H1` r1 | 6 | 50 | 1 764.8 | 380.6 | 9 168.2 | 485 MiB | 485 MiB |
| `XB-W250-H1` r2 | 6 | 50 | 1 750.0 | 358.7 | 9 001.4 | 484 MiB | 485 MiB |
| `XB-W250-H1` r3 | 6 | 50 | 1 869.1 | 355.6 | 8 675.6 | 484 MiB | 485 MiB |
| `XB-G500-H1` | 5 | 83 | 2 660.9 | 711.1 | 10 599.8 | 533 MiB | 485 MiB |
| `XB-PB-H1` | 6 | 51 | 1 851.9 | 369.0 | 9 161.0 | 477 MiB | 485 MiB |
| `XB-ALL-H1` | 10 | 354 | 8 057.2 | 974.4 | 26 027.4 | 540 MiB | 485 MiB |
| `XB-L185-H0` | 4 | 24 | 1 008.4 | 347.1 | 6 104.4 | 471 MiB | 485 MiB |
| `XB-W250-H0` | 6 | 50 | 1 867.8 | 359.8 | 9 483.3 | 484 MiB | 485 MiB |

Across all 97 runs: the largest layout was 1 022.9 ms (a 48-page parity run),
the longest run wall clock 3 276.9 ms (`XB-ALL-H1` part 3), the largest
Chromium process-tree peak 552 MiB (the same parity run), the largest container
running peak 485 MiB. There was no cap, no near-ceiling case, no out-of-memory
kill and no repeated-layout signal in any run.

The builder's hand-over computation took **11.09 ms** for the whole fixture set
(75 part fixtures, openings and whole-group totals from the 10 000-record
dataset). Assembly per report is 0.2-2.8 s numbered and 0.2-1.1 s unnumbered
(informational); verification of all pages takes 6-85 s.

The measured stage ran 97 runs in **228 s** of driver wall clock inside its
confirmed serial window (21:43:33-21:47:22). The whole run, from the first
pre-flight to the results, took about an hour of active delivery (20:57:44 to
21:56:58, then the post-processing after the resume); the phase estimate was
5-6 h, so the hand-over cost less than feared here - because everything it needs
is in the data, which is the caveat of section 3.

## 9. Parity with `spikes/12-group-continuity`

Six runs are the same fixtures as folder 12's runs (B1000, GE52, GE61, GS1200,
GTF300u, GTL300u), laid out with this folder's harness (the derived accounting
script included, with no opening attribute) and compared field by field with
folder 12's committed results: class, pages, text verdict and content hash,
accounting verdict and row-set hash, group failures and the four placement
counts.

**Result: 6 runs compared, 0 differences.** The derived script and the page
builder delegate therefore change nothing when no opening is handed over.

## 10. Reproducibility

`XB-W250-H1` was laid out three times (r1, r2, r3). For each of the six parts the
pages, the row-set hash and the text content hash are equal across the three
repetitions, and the three reports' XA/RG results and numbered-page word hashes
are equal. `results/summary.json` records `agree: true` with no difference.

The fixture set was built twice and both `SHA256SUMS` files are byte-identical;
folder 12's own fixture set, rebuilt through the linked builder, equals its
committed `fixtures.sha256` (`10267bf9...`). The pins of this experiment's
fixtures are in `results/fixtures.sha256`
(`c89ef6dfe95e6bf2e80800a6f73e158399f4e734b837d2910012713b20c9f640`), the same
file the stage verified before its first run.

## 11. What this experiment does not show

- **The harness parts are not the product's final shape.** Each part keeps the
  shared construction's title, column caption and its own "Block total", and
  **the parts print their own page counters** ("Page k of n" per part), which the
  assembly verifier requires. A product shape for a multi-block document would
  print no per-part page counter; that difference does not affect any result
  here, because the global number is checked on the numbered merge and the
  content on the unnumbered one.
- **Group totals are static text.** RG-2 judges the value the hand-over wrote
  against the source and its survival through pagination and assembly; it is not
  the layout engine computing a total. The engine check is XA-2/XA-3.
- **Behaviours 1 and 3 are counted, not explained.** No keep-with-next,
  keep-together or repeated-header mechanism was added; no stylesheet rule about
  groups or page breaks was added or changed. Behaviour 2 at a part start is
  data-level fixture text.
- **The numbered output is checked only through the verifier's order and pixel
  items** (and its numbering item for the stamp); the content checks read the
  unnumbered merge.
- **The page-coinciding block limit is a documented exception**, chosen from 22
  pre-measured layouts before the stage (section 2).
- **Table markup is excluded, even as a comparison**: folder 10 measured text
  and row loss in table markup, so a table comparison would measure a different
  defect, not group continuity.
- **No report larger than 10 000 records / 10 parts / 354 pages was measured.**
  The top of the bracket is the whole dataset; its memory is not extrapolated to
  larger reports.
- **The unsplit giant-row signature is not a group-continuity failure.** No
  unsplit run is planned; G200 runs only split, and the known row-loss signature
  of an unsplit giant row, had it appeared, would have been reported as such.

## 12. Open gaps

- Native layout with two-pass accounting stays an open path; nothing here
  measures it.
- The mechanism cost of the layout-dependent behaviours (a header kept with its
  first row, a total kept with its last row) is not measured; only the
  data-level hand-over cost is.
- Whether a product shape would print the per-part title, caption, block total
  and part page counter - and what that costs - is a product decision, not
  measured here.
- No group rule is chosen, written or recommended here. The results describe
  what printed; the rules go to their own user stories.

## 13. How to run

The two images are built from this folder's `Dockerfile` (harness, on the PDF
base by digest) and `Dockerfile.account` (checker: .NET runtime + poppler 24.02,
no browser, no PDF library):

```sh
docker build -f Dockerfile -t cross-block-groups-spike:local .
docker build -f Dockerfile.account -t cross-block-groups-check:local .
```

The harness (in the base image, one fresh process per run):

```sh
docker run --rm --init --network none --memory 4g --memory-swap 4g \
  cross-block-groups-spike:local versions
docker run --rm --init --network none --memory 4g --memory-swap 4g \
  -v "$PWD/results/raw:/out" cross-block-groups-spike:local \
  run --stage 1 --fixtures /fixtures --out /out --budget-seconds 3600
```

`run --matrix calibration` runs the 22 layouts of the page-coinciding boundary
and `run --matrix quick` the contract loop. Every command needs the fixtures
(see `part-fixtures` below) and writes one record per run.

The checker (in the checker image, reading only the assembled PDFs and the
fixtures; the paths below are the ones seen from inside the container - a host
without host path sharing copies the data in and the outputs out with
`docker cp`, as the recorded runs did):

```sh
CrossBlockCheck part-fixtures --dataset ... --sums ... --reference ... --tall ... --group-reference ... \
  --calibration results/calibration.json --out results/raw/build1/fixtures
CrossBlockCheck calibrate    --runs results/raw/calibration --out results/calibration.json
CrossBlockCheck set-manifest --fixtures results/raw/build1/fixtures --runs results/raw/stage1 --out results/raw/reports
CrossBlockCheck slices       --reports results/raw/reports --runs results/raw/stage1 --out results/raw/slices
CrossBlockCheck report       --fixtures results/raw/build1/fixtures --slices results/raw/slices \
  --reports results/raw/reports --out results/raw/report
CrossBlockCheck observe      --fixtures results/raw/build1/fixtures --slices results/raw/slices \
  --reports results/raw/reports --out results/raw/observe
CrossBlockCheck account      --fixtures results/raw/build1/fixtures --runs results/raw/stage1 \
  --integrity results/raw/ti-stage1/integrity.json --out results/raw/account
CrossBlockCheck controls     --fixtures ... --runs ... --slices ... --reports ... \
  --integrity ... --integrity-slices ... --extraction ... --accounting ... --report ... --probe ... --out results/raw/controls
CrossBlockCheck parity       --reference <folder 12 results> --fixtures ... --runs ... \
  --integrity ... --accounting ... --out results/raw/parity.json
CrossBlockCheck verdict      --fixtures ... --runs ... --reports ... --report ... --observations ... \
  --controls ... --parity ... --integrity ... --integrity-slices ... --accounting ... \
  --identity results/identity-start.txt,results/identity.txt --fixture-report ... --calibration ... \
  --summary results/summary.json
```

The assembly and verification use the folder 06 image unchanged
(`assemble --variant pdfsharp-merge-stamp`, `qpdf-args` + `qpdf @args`, and
`verify` with `--numbered true|false --pages all --box-rule
source-or-rounded|source-only`). The page images come from the folder 07 image
(`render --pdf ... --pages ... --out ...`, 100 dpi).

`verdict` exits 0 only when the instrument is healthy: identity at the start and
the end, the fixture pin, the builder's self-checks, the calibration, one record
and its entries per planned run, every report's assembly, verification, slices
and report entries, the controls trusted, no parity difference, the repetitions
agreeing, and SEM and escaping passing on every `ok` run. Every report outcome
is a result and never part of the exit code.

## 14. Results files

Committed:

- `results/summary.json` - the verdict, the outcomes per report, the statements
  per case, the boundary observations, the parity, the reproducibility, the
  costs and the calibration;
- `results/report.json`, `results/observations.json`, `results/numbering-area.json`;
- `results/controls.json`, `results/parity.json`, `results/probe.json`;
- `results/integrity.json` (97 stage runs), `results/integrity-slices.json`
  (84 slices), `results/text-controls.json`, `results/accounting.json`;
- `results/verify/<instance>/verify-numbered.json`,
  `verify-unnumbered.json`, and for `XB-W250-H1-r1` the numbering controls
  `nx1.json` and `nx2.json`;
- `results/report-fixtures.json`, `results/fixtures.sha256`,
  `results/calibration.json`, `results/plan.json`, `results/runs.csv`,
  `results/environment.json`;
- `results/identity-start.txt`, `results/identity.txt` (124 reused files each,
  identical);
- `results/png/` - 10 page images of the numbered reports: the last page of
  part 1 and the first page of part 2 of `XB-L180-H1` and `XB-L183-H1`, the
  first boundary of `XB-PB-H1`, and the boundary before part 2 of
  `XB-W250-H1` and `XB-W250-H0`;
- `results/pdf/` - 4 assembled numbered reports of the named instances.

`results/raw/` is the complete raw output (fixtures, 97 run records, part PDFs,
slices, both merges, verifier logs, text and accounting runs) and is ignored by
version control. `runs.csv` is derived from the stage's own `runs.jsonl` joined
to the text and accounting outputs; `environment.json` is derived from the
stage's summary, the registry and the image ids. Group and placement results are
per report in `report.json` and `observations.json`.

## 15. Conclusion

On this measured set, with the hand-over computed from the source data, every
one of the 15 H1 instances (13 distinct reports and their repetitions) kept its
group headers in source order, each closed group's total printed once with the
sum of all its rows from every part, every row inside its group, every part's
block total exact, and every page's subtotal and the brought/carried chain exact
across every block boundary (59 of 59 boundaries continuous over those
instances; 49 boundaries over the 13 distinct reports), with "Page i of N" on
every page of the assembled report. The 2 baseline reports, independently
laid-out parts, broke at every boundary in exactly the way the shared
construction predicts: a chain restarted at 0.00, a repeated plain header and a
partial total.

At the boundary, the three chosen behaviours were counted: a header not left
alone at a part's last page end in every case; the continuation header at every
part start with an open group, produced as fixture text; the group total on its
last row's page wherever a group closes at a boundary. This experiment reports
those counts and takes no decision: the group rules, the shape of a part and
any mechanism behind behaviours 1 and 3 are product decisions for their own
work.
