# Two-pass page subtotals over native layout

**Status: measured and judged.** The probes, the measured matrix (one serial
run, 94 runs) and its judgement are complete. The judgement was made on the
recorded matrix files without measuring again; the files were shown unchanged
by their hashes before and after. This report states what was built, how to
run it, and what was measured and judged — and nothing more.

## Hypothesis

A block is laid out once by Chromium's own print layout. Each item row carries
invisible markers: link annotations with an unroutable address, wrapped around
text the row prints anyway. A second pass then reads the markers back from the
PDF, maps every row to the page where it starts and the page where it ends,
computes the page subtotals and the brought/carried-forward chain from the
block's source data, and stamps them into the empty bottom margin. The layout
is never re-run, so no row can move. The questions are:

- **H1** Do the printed subtotals and chain equal, on every page, the values
  computed independently from the source data?
- **H2** Do the markers come back reliably — every row, the correct page, the
  correct order — on every fixture?
- **H3** Is the PDF unchanged outside the reserved area?
- **H4** Is the two-pass result deterministic?
- **H5** What do both passes cost in time and memory, per block size?
- **H6** What happens on pages that hold no detail text?
- **H7** Where does each row's amount print: placed on the row's last line by the
  second pass, or left in the table cell where native layout puts it?

## How to run

Prerequisites: Docker with BuildKit; the PDF base image in the local registry
(resolve its digest with a `HEAD` request on
`/v2/report-service-pdf-base/manifests/1.63.0-10.0.13-noble`); the grouped pilot
dataset in `samples/datasets/`; and the text-integrity checker image built from
`spikes/07-native-tall-rows/Dockerfile.check`.

The commands below run from the repository root, in PowerShell. With Rancher
Desktop (WSL 2) use the `/mnt/<drive>/...` form of the repository path in
mounts. With Docker Desktop use `${PWD}`.

```powershell
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
docker buildx build --progress=plain --load -f spikes/09-two-pass-page-subtotals/Dockerfile.account -t page-accounting-check:local .
docker buildx build --progress=plain --load --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/09-two-pass-page-subtotals/Dockerfile -t two-pass-spike:local .

# Fixtures: the shared series, then the supplementary ordinary blocks (B250 … B4000)
docker run --name "text-integrity-check-$ts-fixtures" --init -v "${wslRoot}/samples/datasets:/data:ro" `
  text-integrity-check:local fixtures --dataset /data/header-items-v1.grouped.10k.json --sums /data/SHA256SUMS --out /out/fixtures07
docker run --name "page-accounting-check-$ts-ordinary" --init -v "${wslRoot}/samples/datasets:/data:ro" `
  -v "${wslRoot}/spikes/09-two-pass-page-subtotals/results/raw/fixtures07:/reference:ro" page-accounting-check:local `
  ordinary-fixtures --dataset /data/header-items-v1.grouped.10k.json --sums /data/SHA256SUMS --reference /reference --out /out/fixtures

# Marker probe: first pass and a read-only reading; then the text check and the probe judgement
docker run --name "two-pass-spike-$ts-probe-markers" --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/spikes/09-two-pass-page-subtotals/results/raw/fixtures:/fixtures:ro" two-pass-spike:local probe markers --fixtures /fixtures --out /out
docker run --name "page-accounting-check-$ts-probe-markers" --init --memory 2g --memory-swap 2g `
  -v ...fixtures:/fixtures:ro -v ...probe-markers:/runs:ro -v ...integrity.json:/integrity.json:ro -v ...07 integrity.json:/reference.json:ro `
  page-accounting-check:local probe markers --fixtures /fixtures --runs /runs --integrity /integrity.json --reference-integrity /reference.json --out /out --work /work

# Stamp probe, quick loop and measured matrix (run in a quiet window: no other container but the registry)
docker run ... two-pass-spike:local probe stamp --fixtures /fixtures --out /out
docker run ... two-pass-spike:local run --fixtures /fixtures --out /out --matrix quick|full
# Then: the text check (07's image: check, controls), and this folder's checker: account, controls, verdict
```

Every container is named and removed by name after `docker cp`. Caps per pass
are 300 s inside the child process, with the process tree killed at 330 s, and
4 GiB memory with swap off. Every pass ends with a recorded class: `ok`,
`timeout`, `killed-wall-clock`, `oom-killed`, `crashed`, `error`,
`guard-failed` or `not-run`. `check-identity.sh` prints the hashes of every
reused file.

## The two passes

- **Pass 1 (`TwoPassSpike`, PDF base image, Chromium 153.0.8010.12).** The
  document is the shared fixture HTML with markers added:
  - `s` around the sequence number;
  - `e` around the last word of the description;
  - `a` around the amount, in strategy `cell` only.

  Strategy `stamped` leaves the amount cell empty. The bottom margin stays empty.
- **Pass 2 (`TwoPassSpike`, PDFsharp 6.2.4).** Opens the pass-1 PDF and reads
  the link annotations with the library's low-level dictionary layer. It checks
  them (every row, exactly one start marker, at least one end marker; at most
  one row continuing across a break) and fails explicitly otherwise. It then
  computes the values in exact decimals under the end-page rule and stamps the
  footer `Brought forward: x · Page subtotal: y · Carried forward: z`, with
  `(continues)` on every page of a row's span except its last. In strategy
  `stamped` it also stamps each row's amount on the row's last line. Finally it
  removes the markers and saves.

## The checks

- **Text:** the text-integrity check of `spikes/07-native-tall-rows`, unchanged.
- **Accounting (`PageAccountingCheck`):** reads the PDFs with poppler and qpdf,
  never with the PDF library under test. It runs:
  - the accounting rules of `spikes/02-pdf-layout`, linked unchanged and fed
    from the PDF;
  - the read-back judged against its own reading of the pass-1 PDF;
  - the PDF unchanged outside the reserved area, by pixels at 72 dpi, by words
    and by page boxes;
  - positive and negative controls.

## Results

Environment: the reference environment (16 CPUs), PDF base
`sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d`,
Playwright 1.63.0, Chromium 153.0.8010.12, .NET 10.0.13, PDFsharp 6.2.4,
poppler 24.02.0, qpdf 11.9.0.

**Marker probe** (B100 and T300u, both markups, amounts in the cell, first pass
only; `results/probe-markers.json`, `results/probe-markers-integrity.json`):

| Check | B100 table | B100 grid | T300u table | T300u grid |
|-------|------------|-----------|-------------|------------|
| (a) only row-marker links; every row one `s`, ≥ 1 `e`, one `a` | pass | pass | pass | pass |
| (b) text check passes the first-pass PDF (no marker text extracted) | pass | pass | pass | pass |
| (c) pages, per-page word counts, row spans as without markers | pass (4) | pass (4) | pass (7; row 50 on 3–6) | pass (7; row 50 on 3–6) |
| (d) each start and end rectangle holds its word (± 1 pt) | pass | pass | **fail** | pass |

The second pass's own reading passed its check on all four PDFs.

**The failure (d).** In table markup, the end marker of row 50 — the row that
runs from page 3 to page 6 — has its rectangle on page 6 at x 240–276.75 pt,
y 147.75–158.25 pt (from the top). The word it wraps, `informaci`, is printed
at x 240.16–276.67 pt, y 102.85–112.91 pt. The rectangle is 45 pt lower than
the word: it lies on the line of row 53. Every other rectangle of the four
PDFs holds its word, including the same row's end rectangle in grid markup
(y 54.0 against 54.1) and row 51's start rectangle on the same page. Evidence:
`results/pdf/T300u-table-c-r1.p1.pdf`, and the listing of every annotation in
`results/probe-markers.json`.

**Split-row probe** (second probe run: every shared fixture with a split row —
T150, T200, T300, T500, their `u` twins, T300u-ab, X64K and X64K-ab — both
markups, amounts in the cell, first pass only; `results/probe-split-rows.json`
holds every split row's fields; the four-PDF probe was repeated identically,
`results/probe-markers-2.json`):

- The text check passes all 22 first-pass PDFs, and layout and pages equal the
  runs without markers.
- **Amount rectangles:** all 22 lie on the page and line of their amount
  (offset 0.12 pt).
- **End rectangles, grid:** all 9 that exist hold their word (offset 0.12 pt),
  including end words deep in their last fragment.
- **End rectangles, table:** all 11 lie on the right page and at the right x,
  but too low by exactly 15 pt for each page the row has already spanned:

  | Fixture | Pages of row 50 | Last fragment | Offset |
  |---------|-----------------|---------------|--------|
  | T150, T150u | 3–4 | 2nd of 2 | 15 pt |
  | T200, T200u | 3–5 | 3rd of 3 | 30 pt |
  | T300, T300u, T300u-ab | 3–6 | 4th of 4 | 45 pt |
  | T500, T500u | 3–8 | 6th of 6 | 75 pt |
  | X64K, X64K-ab | 3–15 | 13th of 13 | 180 pt |

  The offset does not follow the word's position inside its fragment: at the
  same offset the word sits on very different lines (35 and 39 lines down at
  15 pt; 4, 5 and 12 lines down at 45 pt). It does follow the
  number of pages the row has already crossed; 15 pt is close to the height of
  the repeated header row (13.5 pt between the header's top and the next
  line's top). That is an observation, not a model.
- **The stop:** in grid markup with a bottom-aligned amount (T300u-ab and
  X64K-ab — the layout with whole header-only pages inside the row), the row's
  **end marker is missing from the PDF**: only its start and amount links
  exist, no other annotation does, and the word itself prints and is extracted
  normally. The second pass's own check refuses both PDFs for the same reason
  (one start, no end, one amount marker).
- Coverage: no split row has its end word on the first line of its last
  fragment in either markup; that case is not covered by the series.

**Third probe run** (`results/probe-markers-3.json`,
`results/probe-split-rows-3.json`): the two marker probes, repeated on rebuilt
images, give the same result — the same rectangles, offsets and missing end
markers.

**Bottom-aligned fixtures with the amount stamped, grid markup**
(`results/probe-stamped-ab.json`):
- With the amount cell left empty, the layout changes: T300u-ab has 8 pages
  instead of 9, and X64K-ab 18 instead of 28; row 50 spans pages 3–7 and 3–16.
- The row's **end marker is still missing**; only its start link exists. The
  second pass refuses both PDFs, so no total is printed for them.

**Stamp probe** (B100, grid markup, both strategies, both passes;
`results/probe-stamp.json`):
- Every stamp word is extracted exactly.
- The 100 stamped amounts sit on their rows' last lines, 0.12 pt from the last
  word's centre, so no offset is needed.
- Pixels outside the reserved area are unchanged and no stamp covers ink. This
  is judged against the reference with the page box rounded as the PDF library
  writes it (`[0 0 594.96 841.92]`).
- The pass-2 PDF has two content streams instead of one.
- **Not accepted:** saving through the PDF library adds a key to every page
  dictionary, `/Group << /CS /DeviceRGB /S /Transparency >>` (a page
  transparency group), which the pass-1 page did not have. The probe accepts
  only changes to the content, resources, annotations and page box, so this
  stops the run until the change is judged.

**Stamp probe, repeated with stricter checks** (`results/probe-stamp-2.json`;
B100, grid markup, both strategies):
- Each page dictionary changes in exactly this way:
  - `/Annots` removed (the markers, as intended);
  - `/Contents` and `/Resources` changed;
  - the page box rewritten as `[0 0 594.96 841.92]`;
  - `/Group << /CS /DeviceRGB /S /Transparency >>` added.

  The PDF library adds the transparency group, with an opaque graphics state,
  whenever it draws on a page; it is an observed side effect of the library.
  Nothing removes it afterwards.
- Neither PDF has a soft mask, a blend mode other than Normal, an alpha below
  1, a colour space or a pattern resource.
- Rendered in colour at 72 dpi with poppler, no pixel changes outside the
  reserved area and no stamp covers ink.
- `qpdf --check`: exit 3, and every warning is the zlib message "input stream
  is complete but output may still be valid" that this library's compressed
  streams produce. The first-pass PDFs check clean.
- **What this shows:** no visible change in poppler on these black-on-white
  pages.
- **What it cannot show:** other viewers or printers, colour-managed or CMYK
  output, and pages with real transparency or colour. It is not a claim that
  the transparency group is harmless in general.

**Measured matrix** (the amounts kept in the cell in table markup; in grid
markup kept in the cell and stamped):
- 94 runs ran and 46 were recorded as not run:
  - table `stamped` everywhere, because the end rectangle is too low there;
  - 35 of them were replaced by the same fixture in `cell`.
- Pass 1 ended `ok` in all 94. The longest pass 1 took 3.9 s, and no pass 2
  took more than 1.15 s.
- The second pass refused 11 PDFs:
  - one negative control by construction (a row deliberately removed);
  - the four bottom-aligned grid runs, as the probes showed;
  - **six table `cell` runs of the record-size series:** X128K, X256K, X512K
    and X1M (three repetitions).
- In those six PDFs, row 50 spans 25 to 199 pages. It has its start and amount
  links on page 3, but **no end link on any page**: 299 annotations where 300
  are expected.
- X64K in table markup (row 50 over 13 pages) has its end link, and so do the
  grid `stamped` runs of the same series (X128K: end link on page 27).

**Judgement of the measured matrix** (`results/summary.json`,
`results/accounting.json`, `results/integrity.json`, `results/controls.json`,
`results/accounting-controls.json`, `results/runs.csv`):

- **Both oracles are trusted.**
  - The text check's controls behave: the three printed negative controls, and
    the eight extraction controls on T300u in table markup.
  - Every accounting control behaves: eight wrong values or marks, three wrong
    readings, the page-box controls and the mark-placement controls, each
    failing exactly as stated in advance.
  - The colour check sees the two controls that paint outside the stamped area
    exactly as the grayscale check does.
  - Every second-pass PDF checks `pass-exempt` with qpdf.
- **Accounting (H1)** — subtotals, brought and carried forward, end-page rule
  — is exact on every PDF the second pass produced.
  - **Table, amount in the cell** (46 runs): 28 confirmed. 9 are partial only
    because a split row's amount prints in the cell on the row's first page,
    not its last. 6 are refused: the long rows without an end marker.
  - **Grid, amount stamped** (40 runs, 11 of them controls): 27 confirmed. 2
    are refused: bottom-aligned, end marker missing.
  - **Grid, amount in the cell** (8 runs): 5 confirmed, 1 partial (the amount
    on the first page), 2 refused (bottom-aligned).
  - No run printed a wrong value.
- **Reading back the markers (H2)** holds on every row of every judged run,
  except where an end marker is missing: T300u-ab and X64K-ab in grid markup,
  and X128K, X256K, X512K and X1M in table markup. There the second pass
  refused to stamp.
  - Row 50 spans 25 pages in X128K, 50 in X256K, 100 in X512K and 199 in X1M
    (pages 3–27, 3–52, 3–102 and 3–201).
  - At 13 pages (X64K) the end marker is present.
  - The threshold between 13 and 25 pages was **not measured**.
- **Nothing moved (H3).** On all 70 judged pairs, no row moved, no word
  changed and no pixel changed outside the stamped area. Every page box was
  rewritten as `[0 0 594.96 841.92]` by the PDF library, which the check
  allows.
- **Determinism (H4).** The three repetitions of B1000 (table `cell`, grid
  `cell`, grid stamped), X1M (table `cell`, grid stamped), F1M-b and F1M-a are
  identical: pages, word counts, marker reading, stamped footers, text and
  page renders. For X1M in table markup the second pass refused every
  repetition, so only the first-pass figures and the reading compare.
- **Cost (H5)** — first pass / second pass, one fresh process each:

  | Fixture | Pages | First pass: PDF time, Chromium peak RSS | Second pass: open + read + compute + stamp + save, peak RSS, output |
  |---------|-------|------------------------------------------|---------------------------------------------------------------------|
  | B100 grid stamped | 4 | 49 ms, 419 MiB | 45 + 20 + 8 + 46 + 24 ms, 52 MiB, 51 KB |
  | B1000 grid stamped | 36 | 337 ms, 494 MiB | 115 + 28 + 12 + 86 + 34 ms, 77 MiB, 167 KB |
  | B1000 table cell | 36 | 437 ms, 493 MiB | 154 + 50 + 8 + 46 + 33 ms, 74 MiB, 176 KB |
  | B2000 grid stamped | 71 | 876 ms, 573 MiB | 208 + 39 + 13 + 72 + 49 ms, 85 MiB, 292 KB |
  | B4000 grid stamped | 142 | 1.74 s, 687 MiB | 333 + 117 + 34 + 117 + 87 ms, 107 MiB, 543 KB |
  | B4000 table cell | 145 | 1.97 s, 813 MiB | 636 + 158 + 33 + 63 + 85 ms, 124 MiB, 581 KB |
  | X1M grid stamped (one 1 MiB row) | 200 | 779 ms, 552 MiB | 67 + 22 + 8 + 83 + 102 ms, 73 MiB, 709 KB |
  | F1M-b table cell (1 MiB free text) | 130 | 701 ms, 483 MiB | 53 + 21 + 10 + 70 + 118 ms, 71 MiB, 686 KB |

  The second pass grows with the number of rows and annotations, not with the
  size of a row. Every figure is in `results/runs.csv`.
- **Pages without detail text (H6):** 21 runs with free-text pages (1 to 128
  per run). On every such page the subtotal is 0.00 and carried forward
  equals brought forward.
- **Where the amount prints (H7):**
  - stamped (grid): on the row's last line in all 17 split rows;
  - in the cell: on the row's first page in all 11 split rows (table 10, grid 1).
- **Not measured:**
  - strategy stamped in table markup (the end rectangle is too low there);
  - the end-marker threshold between 13 and 25 pages;
  - a split row whose end word sits on the first line of its last fragment;
  - other Chromium versions or fonts;
  - group continuity across blocks;
  - real transparency or colour.

## Conclusion

Measured only this far, in this Chromium version, with these fixtures:
- The second pass reproduced exact page subtotals and the brought/carried
  chain from the source data on every document it produced. No row moved,
  nothing outside the stamped area changed, and the results repeat exactly.
- Where the markers it needs were not written, it refused to stamp rather
  than print a total. The end marker is missing:
  - in grid markup with a bottom-aligned amount (both strategies);
  - in table markup for rows of 25 pages or more (threshold unmeasured).
- In table markup the end rectangle of a continued row is also 15 pt too low
  per page crossed, so an amount can be stamped from it only in grid markup.
- With the amount left in its cell, a split row's amount prints on its first
  page; stamped in grid markup, it prints on its last line.
- The PDF library adds a page transparency group, rewrites the page box, adds
  a content stream and writes compressed streams that `qpdf --check` reports
  with its known zlib warning. On these black-on-white pages poppler shows no
  visible change outside the stamped area; that says nothing about other
  viewers or colour output.

Nothing here recommends a pagination stack, a marker carrier or a markup.

## Provenance

- `TwoPassSpike/HtmlBuilder.cs`, `LayoutRun.cs`, `MatrixDriver.cs`,
  `ProcessTreeSampler.cs`, `ResultsWriter.cs`, `Program.cs` and
  `assets/native.css`: adapted copies from `spikes/07-native-tall-rows`.
- `LiberationFontResolver.cs`: adapted from `spikes/03-pdf-assembly`.
- The fixture library and four reading files of the text check: referenced
  unchanged from `spikes/07-native-tall-rows`.
- The accounting rules and their types: linked unchanged from
  `spikes/02-pdf-layout`. `Reused/LayoutTypes.cs` is a byte copy of its result
  types.
- The PGM reader: linked unchanged from `spikes/03-pdf-assembly`.

## Packages and licences

- Microsoft.Playwright 1.63.0 (MIT).
- PDFsharp 6.2.4 (MIT), with its resolved dependencies, all MIT:
  Microsoft.Bcl.AsyncInterfaces 6.0.0, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2,
  Microsoft.Extensions.Logging.Abstractions 8.0.3, System.ComponentModel.Annotations 5.0.0,
  System.Security.Cryptography.Pkcs 8.0.2 (pinned).
- Chromium headless shell 153.0.8010.12 (BSD-3-Clause) and the Liberation fonts:
  content of the PDF base image.
- poppler-utils 24.02.0 (GPL) and qpdf 11.9.0 (Apache-2.0): in the checker image
  only, never shipped or composed.
- The .NET images (MIT).
