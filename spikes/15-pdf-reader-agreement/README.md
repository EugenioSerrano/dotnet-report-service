# Spike: a PDF text-and-position reader the product may ship

Every check the earlier PDF experiments made on a printed PDF read it with
poppler-utils (`pdftotext -bbox`, `pdfinfo`), which lives only in their test
images and is GPL, so it can never ship. The pagination decision makes a
post-render verification of every PDF block mandatory: row coverage and text
read back from the printed PDF on both layout paths, and page accounting on
the accounting path. This experiment measures whether a reader whose licence
passes the platform's licence policy reads words, their pages and their
positions from the earlier experiments' PDFs accurately enough for those
checks to reach the same verdicts, and at what cost.

**Nothing is decided here.** No reader is chosen; the checks, the earlier
reference reader and every test strategy stay as they are.

## Hypothesis

A managed PDF reader with a permissive licence, fed to the **unchanged** rules
of the earlier checks, reaches the same row-coverage, text-completeness and
page-accounting verdicts as the earlier reference reading on every measured
PDF, detects every known-bad PDF under the same rule, and reads a page in a
time and memory comparable to the reference reader.

## What is measured

| | Reader | Version | Licence | Role |
|--|--------|---------|---------|------|
| Reference | poppler-utils (`pdftotext -bbox -enc UTF-8`, `pdfinfo`) in `text-integrity-check:local` | 24.02.0-1ubuntu9.11 | GPL-2 or GPL-3 | **test-only reference, never a candidate, never shipped** |
| Candidate | PdfPig | 0.1.16 | Apache-2.0 | positioned-text reader, two configurations (below) |
| Candidate | PDFsharp | 6.2.4 | MIT | what it can read at all, through its public API |

PdfPig configurations, fixed before any run:

- **pdfpig-a "as shipped"**: `page.GetWords()` (the library's default word
  extractor) and each word's `BoundingBox` (glyph-tight), converted from PDF
  user space (origin bottom left) to the checks' frame (origin top left, y
  downwards).
- **pdfpig-b "baseline box"**: the same words and horizontal extent; the
  vertical extent from the first letter's baseline and the word's largest point
  size (`yMax = page height − baseline`, `yMin = yMax − point size`). Glyph-tight
  boxes move a word's vertical centre with its letters' shapes, and the checks
  group lines by vertical centre at ±2 pt; this configuration shows whether that
  alone explains a disagreement.

PDFsharp is measured with its **public members only**: page count, page boxes,
the content stream's text-showing operators and their string operands. Writing
a character-map parser, a font-program reader or a text-matrix engine is not
allowed: that would be building a reader, not measuring PDFsharp.

### Why the comparison is fair

Every rule of the earlier checks takes the same input: the page count, the page
sizes and every word with its page and box. The text rules (`IntegrityRules`),
the reading of rows and footers (`PdfReading`) and the strict accounting
judgement (`Account.Judge`) are linked here **unchanged**, with every file they
need, exactly as `spikes/12-group-continuity` links them. Each reader only has
to produce that word list; the same rule code then judges every reader. The
reader is the only variable.

## How the PDFs were chosen

### What was retained

`results/inventory.json` lists the 33 PDFs committed by the earlier experiments
(32 distinct: `06-pdf-assembly-chromium-parts/fixtures/pdfsharp-part.pdf` is
byte-identical to `03-pdf-assembly/results/samples/partial-part-0001.pdf`) with
their hashes, and what each is used for here.

| Folder | Committed PDFs | Reference entry for them | Used here |
|--------|----------------|--------------------------|-----------|
| 02 | 4 | none from a PDF reading (their checks ran on the page) | agreement only |
| 03 | 2 | none | agreement only; the stamp observation |
| 06 | 3 (one duplicate) | none | agreement only; the stamp observation |
| 07 | 4 | `integrity.json` | judged |
| 08 | 4 | `integrity.json` (run ids prefixed `s2-`) | judged |
| 09 | 6 | `integrity.json` for 4 | 4 judged, 2 agreement only |
| 10 | 4 | `integrity.json`, `accounting.json` | judged |
| 11 | 2 | `summary.json` only (links read with qpdf) | agreement only |
| 12 | 4 | `integrity.json`, `accounting.json`, `groups.json` | judged |

### Regenerated

Ten PDFs the comparison needs were not committed. They were regenerated with
the **original harnesses and their original images**, unchanged, and each is
accepted only if the reference reader, run here on the regenerated file,
reproduces the recorded page count, failure classes and content hash of its
run id. Their reference is the earlier experiments' recorded entry, never a
candidate.

| Run id (as recorded) | Harness | Why it is needed |
|----------------------|---------|------------------|
| `NC-W-B100-table-r1` | native layout (`spikes/07-native-tall-rows`) | known-bad: a word dropped |
| `NC-L-B100-table-r1` | native layout | known-bad: a line repeated |
| `NC-R-B100-table-r1` | native layout | known-bad: a row lost |
| `NC-F-F26K-a-table-r1` | native layout | known-bad: a free-text word dropped |
| `F1M-a-table-r1` | native layout | long free text (129 pages) |
| `X1M-table-r1` | native layout | the largest native table (203 pages) |
| `s1-grid-s1-B1000-split-t1.00-f0.90-on-r1` | Paged.js grid (`spikes/10-pagedjs-table-strict-accounting`, stage 1) | ordinary block with page accounting |
| `s1-grid-s1-NC-R-B100-split-t1.00-f0.90-on-r1` | Paged.js grid | known-bad: a row lost, accounting broken |
| `s1-grid-s1-NC-W-B100-split-t1.00-f0.90-on-r1` | Paged.js grid | known-bad: a word dropped, accounting intact |
| `s1-grid-s2-F1M-a-split-t1.00-f0.90-on-r1` | Paged.js grid | long free text (129 pages) |

### The measured set

42 PDFs (`results/pdf-set.json`): 30 judged (20 committed, 10 regenerated) and
12 compared on words, pages and positions only.

| Class | PDFs |
|-------|------|
| Ordinary blocks | native table B1000; Paged.js grid B1000 (regenerated); native two-pass B100; groups GE52, GE61 |
| Tall rows split into parts | Paged.js grid G200, W2a, X1M, T300u; native T300u (table), T300u-ab, X64K-ab (grid); native two-pass T300u (three) |
| Long free text | F1M-a native and Paged.js (regenerated) |
| Groups across pages | GTF300u, GTL300u (and GE52, GE61) |
| Largest PDFs | Paged.js X1M (224 pages); native X1M table (203); F1M-a (129, both paths) |
| Known-bad: lost rows | Paged.js grid G200 without the split; Paged.js table T150; NC-R native and Paged.js |
| Known-bad: lost or altered text | Paged.js table B1000 and T300u; NC-W native and Paged.js; NC-L; NC-F |
| Agreement only | the four page-checked PDFs of folder 02; four assembly samples; two first-pass two-pass PDFs; two row-end link PDFs |

### Fixtures

The rules compare the printed text with the fixture each run printed. The
fixtures are rebuilt here, on the host, from
`samples/datasets/header-items-v1.grouped.10k.json` with the earlier builders
linked unchanged (the series of `spikes/07-native-tall-rows`, the ordinary
blocks of `spikes/09-two-pass-page-subtotals`, the group fixtures of
`spikes/12-group-continuity`). Their three `SHA256SUMS` files are byte-identical
to the ones the earlier harnesses used (`c1576a4e…`, `db1ce1a7…`, `10267bf9…`).

## Judged rules and their controls

The judged verdicts are the checks that need a reader:

| Check | Rule (unchanged code) | Known-bad PDF that fails it under the reference reading |
|-------|----------------------|----------------------------------------------------------|
| Row coverage | TI-2 (every row once, in order) | NC-R native and Paged.js; Paged.js grid G200 without the split; Paged.js table T150 |
| Row coverage | strict rule 1 (rows not lost or duplicated) | NC-R Paged.js; G200 without the split; table T150 |
| Text completeness | TI-3 (description and note) | NC-W native and Paged.js; NC-L; table B1000 and T300u |
| Text completeness | TI-4 (date and amount) | table B1000 and T300u |
| Text completeness | TI-6 (free text before and after) | NC-F |
| Page accounting | strict rule 2 (each page's subtotal) | G200 without the split; table T150 |
| Page accounting | strict rule 3 (the brought/carried chain) | NC-R Paged.js; G200 without the split; table T150 |

A verdict is `pass` or `fail`; two failing verdicts agree only with the same
failure classes and the same first failing page. TI-1, TI-5, TI-7, TI-8 and
strict rules 4–6 are run and reported, never judged here. Rules 7 (footer
twins) and 8 (escaping) do not read the PDF and are not run.

**Order:** the reference reader runs alone first; its readings must reproduce
every recorded entry, and every judged rule must have a known-bad PDF that
fails it. Only then (`results/oracle.json`, `trusted: true`) does the candidate
stage start: the driver refuses to start it otherwise.

## Agreement measure (fixed before any run)

On each side, that reader's own words go through the checks' own geometry
(header band above 15 mm, footer band below 279 mm, lines grouped at ±2 pt,
declared columns by box centre); "equal text" means equal after the checks'
normalisation (NFKC, whitespace removed).

| Measure | Agreement when |
|---------|----------------|
| Pages | equal page count |
| Band text | per page and band, the band's text equal |
| Word match | every reference word paired with a candidate word of equal text (nearest box) — words left unpaired only where the page's band texts are equal (a difference in word splitting) |
| Placement | every matched word in the same band, line ordinal and declared column |
| Horizontal position | left and right edges within **1.0 pt** |
| Vertical position | vertical centre within **3.0 pt** |

Why these values: the checks consume page, band, line, column and reading
order, never box heights. 1.0 pt (0.35 mm) is far below the 1 mm cell padding
the column observation uses. The body text is 9 pt with `line-height: 1.25`
(`spikes/02-pdf-layout/PdfLayoutSpike/assets/layout.css`, linked unchanged by
the Paged.js harnesses, and `native.css` of the native harnesses), a line pitch
of 11.25 pt: 3.0 pt is about a quarter of it and well under half, so a word
cannot change line within it, while it admits the known difference between
glyph-tight boxes and the reference reader's font-metric boxes. The spread of
every difference (max, p99, median) is reported beside the result.

## How to run

Prerequisites: the .NET SDK of `global.json`; Docker with a Linux engine
(Rancher Desktop on WSL 2 was used); the local images
`text-integrity-check:local`, `native-tall-rows-spike:local`,
`pagedjs-markup-spike:local` and `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble`;
the dataset of `samples/datasets/`. Commands from the repository root, in
PowerShell; `$wslRoot` is the WSL form of the current directory.

```powershell
$here = "spikes/15-pdf-reader-agreement"
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
$tool = "$here/work/artifacts/publish/ReaderAgreement/release"

# Host (no container, not measurements). The artifacts path keeps every build output under work/.
dotnet build $here/ReaderAgreement/ReaderAgreement.csproj --artifacts-path $here/work/artifacts -c Release
dotnet $here/work/artifacts/bin/ReaderAgreement/release/ReaderAgreement.dll selftest
dotnet $here/work/artifacts/bin/ReaderAgreement/release/ReaderAgreement.dll fixtures --repo . --out $here/work/fixtures
dotnet $here/work/artifacts/bin/ReaderAgreement/release/ReaderAgreement.dll inventory --repo . --results $here/results
sh $here/check-identity.sh                       # every reused file unchanged
dotnet $here/work/artifacts/bin/ReaderAgreement/release/ReaderAgreement.dll freeze --results $here/results
dotnet publish $here/ReaderAgreement/ReaderAgreement.csproj --artifacts-path $here/work/artifacts -c Release

# Regeneration with the original harnesses (quiet engine). Their output is copied out to work/regenerated.
docker run --name native-tall-rows-spike-regen --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "$wslRoot/$here/work/fixtures/series:/fixtures:ro" native-tall-rows-spike:local run --fixtures /fixtures --out /out --matrix full
docker cp native-tall-rows-spike-regen:/out "$here/work/regenerated/07-native-tall-rows"; docker rm native-tall-rows-spike-regen
docker run --name pagedjs-markup-spike-regen --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "$wslRoot/$here/work/fixtures/shared:/fixtures:ro" pagedjs-markup-spike:local run --stage 1 --fixtures /fixtures --out /out --budget-seconds 3600
docker cp pagedjs-markup-spike-regen:/out "$here/work/regenerated/10-pagedjs-table-strict-accounting"; docker rm pagedjs-markup-spike-regen
dotnet $tool/ReaderAgreement.dll regenerated --repo . --dir $here/work/regenerated --results $here/results --images "<harness image ids>"

# Reference stage (measured; quiet engine): the earlier checker image, used as it is, entrypoint overridden.
docker run --name reader-agreement-reference --init --network none --memory 4g --memory-swap 4g `
  -v "${wslRoot}:/repo:ro" -v "$wslRoot/${tool}:/tool:ro" --entrypoint dotnet text-integrity-check:local `
  /tool/ReaderAgreement.dll drive --stage oracle --repo /repo --work /out/work --results /repo/$here/results
docker cp reader-agreement-reference:/out/work/oracle "$here/work/oracle"; docker rm reader-agreement-reference
dotnet $tool/ReaderAgreement.dll reference --repo . --work $here/work --fixtures $here/work/fixtures --results $here/results

# Candidate stage (measured; quiet engine): the plain .NET runtime image, no poppler in it.
docker run --name reader-agreement-candidates --init --network none --memory 4g --memory-swap 4g --user 1654:1654 `
  -v "${wslRoot}:/repo:ro" -v "$wslRoot/${tool}:/tool:ro" --entrypoint dotnet mcr.microsoft.com/dotnet/aspnet:10.0.13-noble `
  /tool/ReaderAgreement.dll drive --stage candidates --repo /repo --work /out/work --results /repo/$here/results
docker cp reader-agreement-candidates:/out/work/candidates "$here/work/candidates"; docker rm reader-agreement-candidates
dotnet $tool/ReaderAgreement.dll compare --repo . --work $here/work --fixtures $here/work/fixtures --results $here/results
```

No port is published; containers have fixed names and are removed by name;
nothing is pruned.

### Caps, classes and ceilings

- **Caps per run:** every (reader, PDF, repetition) is a fresh process; 300 s
  in the run's own process, its whole process tree killed at 330 s; the
  container `--memory 4g --memory-swap 4g`; the cgroup `oom_kill` counter read
  before and after each run.
- **Classes:** `ok`, `timeout`, `killed-wall-clock`, `oom-killed`, `crashed`
  (the reader threw), `error` (harness defect), `not-run` (with its reason).
- **Ceilings:** a PDF whose first repetition took over 240 s, or whose process
  tree exceeded 3.2 GiB, has its remaining repetitions `not-run (near
  ceiling)`; once a reader hits a cap, every larger PDF of the same layout path
  is `not-run (after cap)` for that reader; no run starts once `work/` exceeds
  4 GiB; 1 h per stage. Nothing above the largest PDFs of the set (224 pages) is
  measured. A cap is a result, never retried.
- **Memory:** the summed resident memory of the run's whole process tree,
  sampled every 50 ms (it includes poppler's own processes for the reference
  reader); the run process's `VmHWM`; the managed heap peak; the container's
  running peak (labelled as such, not a per-run figure).
- **Repetitions:** three for the reference reader and PdfPig, one for PDFsharp;
  the three extraction files of a reader and PDF must be byte-identical.

## Results

### Environment

| Item | Value |
|------|-------|
| Date | 2026-10-31, measured window 21:19:03 – 21:31:47 (UTC−03:00; the run records give times with their offset) |
| Reference environment | `docs/reference-environment.md`, page revision 2 (Intel Core i9-14900, 24 GiB, Rancher Desktop on WSL 2) |
| Engine during the window | only the local registry container; idle build servers shut down before the first run |
| Reference image | `text-integrity-check:local` `sha256:9e013c3b…` (poppler-utils 24.02.0-1ubuntu9.11), used as it is |
| Candidate image | `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble` `sha256:8af87eba…` — no poppler in it |
| Harness images (regeneration) | `native-tall-rows-spike:local` `sha256:b03a90f1…`; `pagedjs-markup-spike:local` `sha256:138266c7…` (the id its experiment recorded); none rebuilt |
| Tool | published once, mounted read-only in both stages (`12004ff9…`); .NET 10.0.13 in the containers |
| Caps | 300 s per run, tree killed at 330 s, 4 GiB with swap off, no network |

`results/environment.json` holds the full record.

### Runs

| Stage | Runs | Outcome | Time |
|-------|------|---------|------|
| Regeneration, native harness (`run --matrix full`) | 90 | 90 `ok` | 114 s (with copy-out) |
| Regeneration, Paged.js harness (`run --stage 1`) | 95 | 95 `ok` | 308 s (with copy-out) |
| Reference reader | 126 (42 PDFs × 3) | 126 `ok` | 117 s |
| Candidates | 168 (PdfPig 42 × 3, PDFsharp 42 × 1) | 168 `ok` | 151 s |

No cap, ceiling or out-of-memory event. A first candidate-stage attempt
stopped before any reader started: the plain runtime image has no output
folder writable by the non-root user, which the checker image has; the stage
was rerun with its work folder under `/tmp`.

### The reference reading (before any candidate result)

`results/oracle.json`, decided 21:28:36, **trusted**:

- every one of the 20 committed judged PDFs matches its recorded entry, and all
  10 regenerated PDFs are identical to theirs: verdict, pages, failure count,
  failure classes, content hash and every TI rule — 429 recorded fields equal;
  15 strict-rule fields have no record to compare with (strict rules 1–3 of the
  three committed Paged.js grid PDFs with the split, and of the two second-pass
  native PDFs); for the Paged.js grid G200 without the split, the record of the
  same run content in `spikes/10-pagedjs-table-strict-accounting` is used, and
  is equal;
- the three repetitions are byte-identical for all 42 PDFs;
- every judged rule has known-bad PDFs that fail it (`results/oracle-controls.json`):
  TI-2 4, TI-3 5, TI-4 2, TI-6 1, strict 1 3, strict 2 2, strict 3 3.

The first candidate run started at 21:29:10, after that decision; the driver
refuses the candidate stage until the reference reading is trusted.

### PdfPig 0.1.16

| | pdfpig-a "as shipped" | pdfpig-b "baseline box" |
|--|----------------------|-------------------------|
| Judged verdicts equal to the reference (TI-2, TI-3, TI-4, TI-6 on 30 PDFs; strict 1–3 on 18) | **174 of 174** | **174 of 174** |
| Known-bad checks detected under the same rule | **20 of 20** | **20 of 20** |
| Words read (all 42 PDFs) | 847 176, every one paired with a reference word of equal text | the same |
| Word splitting | identical to the reference on every page | identical |
| Page counts | equal on all 42 | equal on all 42 |
| Band text | equal on every page of every PDF | equal except the two assembly samples of folder 03 |
| Horizontal edges (max difference) | 0.34 pt (median 0) | 0.34 pt |
| Vertical centre (max / largest p99) | 1.83 pt / 0.83 pt | 2.16 pt / 2.16 pt |
| PDFs agreeing under the full measure | 41 of 42 | 40 of 42 |
| Repetitions byte-identical | all | all |
| **Label** | **verdicts agree, extraction differs** | **verdicts agree, extraction differs** |

Where the extraction differs, and why (no position is outside the tolerance):

- **pdfpig-a**, Paged.js table T300u (a known-bad PDF), page 5: the glyph-tight
  box of a "(continues)" mark places its vertical centre far enough from its
  neighbours for the checks' ±2 pt line grouping to start a separate line, so
  38 words of that page sit one line ordinal lower than in the reference
  reading. The verdicts of that PDF are unchanged (all equal to the reference).
- **pdfpig-b**, the two assembly samples of folder 03 (produced by PDFsharp,
  not by the browser): their title sits at the 15 mm header boundary; the
  baseline box raises every word's centre by 1.5–2.2 pt and moves the title
  into the header band. No verdict is computed for these PDFs.

So neither configuration is **sufficient on the measured set** as the measure
was fixed: each fails the placement row on one or two PDFs. Every judged
verdict and every known-bad detection is equal to the reference in both.

**Extra observation (never a verdict input), stamped page numbers:** on every
stamped page of the three numbered assembly samples (110 pages), both
configurations read the exact "Page i of N" the reference reads, in its area,
and the text outside that area is equal to the reference's. One unstamped part
has no stamp to read.

### PDFsharp 6.2.4

**It cannot read positioned text through its public API.** On all 42 PDFs it
opened the file, gave the page count equal to the reference and the page boxes,
and its content reader listed every text-showing operator (271 595) with its
string operand. Those operands are the font's glyph codes, not text: on the
native B1000 table the first operand is the two bytes `00 33`, drawn with a
font whose dictionary has a `/ToUnicode` entry, but no public member maps an
operand to Unicode or gives its position on the page. Getting either would
mean writing a character-map parser and a text-matrix engine — a reader of
our own. Label: **not evaluable** for words, positions and verdicts.

### Cost (`results/cost.csv`, median of three fresh processes per PDF)

| Reader | Time per page, median over PDFs | Time per page, worst PDF | Largest PDFs (203–224 pages): time per page | Process tree peak |
|--------|---------------------------------|---------------------------|---------------------------------------------|-------------------|
| Reference (poppler) | 24 ms | 68 ms | 23 ms (5.2 s for 224 pages) | 125 MiB |
| PdfPig (both configurations from one reading) | 48 ms | 164 ms | 7–8 ms (1.6 s for 224 pages) | 182 MiB |
| PDFsharp (capability probe only) | 17 ms | 93 ms | 5 ms | 72 MiB |

PdfPig has a fixed start cost per process of about 0.4 s (opening the
library and the first document), so on PDFs of up to 10 pages it takes a median
83 ms per page against the reference's 33 ms. Per PDF, PdfPig took 1.1–2.1
times the reference's time up to 36 pages, about the same at 49–78 pages
(0.68–0.98), and less from 129 pages on (0.53–0.63 at 129, 0.32–0.36 at 203–224,
about three times faster).
Its managed heap peaked at 110 MiB; its process tree never exceeded 182 MiB,
far below the 4 GiB cap and the 3.2 GiB near-ceiling.

## Conclusions

Bounded to what was measured: these 42 PDFs, this font, Chromium 153 and
PDFsharp-stamped samples, PdfPig 0.1.16 and PDFsharp 6.2.4.

1. **A shippable reader reached the same verdicts as the reference.** Fed to the
   unchanged rules, PdfPig 0.1.16's words gave the same row-coverage,
   text-completeness and page-accounting verdict as poppler on every judged PDF
   (174 of 174, in both configurations), and caught every known-bad PDF under
   the same rule (20 of 20), including lost rows, dropped and repeated words,
   lost free text and broken page subtotals and chains.
2. **Its extraction is not identical under the measure fixed before the run.**
   Text, word splitting, pages and positions agree everywhere; what differs is
   which line or band a word is assigned to in three PDFs, from box shape (one
   page with a mark in configuration a, a title on a band edge in
   configuration b). Neither configuration meets the "sufficient on the
   measured set" label; both are "verdicts agree, extraction differs". The
   decision on whether that difference matters for the product is not taken
   here.
3. **PDFsharp 6.2.4 cannot serve as the reader**: it exposes glyph codes, not
   text or positions.
4. **Cost is of the same order as the reference**: up to about twice
   poppler's time on PDFs of up to 36 pages (a fixed start cost per process),
   about equal at 49–78 pages, about three times faster on the largest; under
   200 MiB in every run.
5. **What a reader decision still has to weigh** (not decided here): whether the
   checks should depend on box shape at all (line grouping by vertical centre is
   the reference reader's convention, and the baseline box removed the one
   line-ordinal difference while creating a band-edge one); the notices of the
   resources PdfPig bundles, including the ICC profile whose terms the package
   does not state; and that the reader must fit the PDF worker image policy —
   a managed library inside the worker, as PdfPig is, or a change to that
   policy.
6. **No reader is chosen, and nothing here changes the earlier experiments'
   checks or their reference reader**, which stays test-only.

### Limits

- One positioned-text candidate (PdfPig) and one capability candidate.
- One font family in the browser PDFs; the assembly samples use the stamping
  library's font.
- The 665-page table PDF of the table experiment was not measured; the largest
  measured PDF has 224 pages.
- Time per page includes process start; a reader kept warm in a worker
  process would pay the start cost once.

## Packages and licences

`results/licences.json` holds the full record.

| Component | Version | Licence | Where |
|-----------|---------|---------|-------|
| PdfPig | 0.1.16 (latest stable on nuget.org on 2026-10-31; later only 0.1.17 alphas) | Apache-2.0; no transitive package for `net10.0` | candidate |
| — bundled in PdfPig | Adobe Core 14 AFM metrics with `MustRead.html`; Adobe Glyph List; 92 Adobe CMaps; the ICC `sRGB2014.icc` profile | Adobe's AFM terms (free use, notices kept); BSD-3-Clause text; BSD-3-Clause text; the profile's copyright tag only, no terms stated in the package | inside the candidate's assemblies |
| PDFsharp | 6.2.4 | MIT | candidate |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 (dependency of PDFsharp) | MIT | candidate |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 (dependency of the above) | MIT | candidate |
| System.Security.Cryptography.Pkcs | 8.0.2 (pinned over the requested 8.0.1, as the earlier experiments did) | MIT | candidate |
| poppler-utils | 24.02.0-1ubuntu9.11 | GPL-2 or GPL-3 | reference reader only, in the earlier checker image; never shipped |

No package of the resolved graph is reported vulnerable
(`dotnet list package --vulnerable --include-transitive`). The PdfPig package
passes `dotnet nuget verify --all` (nuget.org repository signature). iText
8.0.1 is in the machine's package cache under an AGPL licence and is not
measured.

## Files

- `ReaderAgreement/` — the measuring program (linked earlier files under
  `Linked/` in the project, unchanged).
- `check-identity.sh` — hashes of every reused file and result
  (`results/identity.sha256`).
- `results/` — `inventory.json`, `pdf-set.json`, `licences.json`,
  `freeze.json`, `identity.sha256`; after the runs, `regenerated.json`,
  `oracle.json`, `oracle-controls.json`, `agreement.json`, `verdicts.json`,
  `controls.json`, `observations.json`, `cost.csv`, `summary.json`,
  `environment.json`; `pdf/regenerated/` — the ten regenerated PDFs (2.3 MB),
  each with the hash recorded in `regenerated.json`.
- `work/` — ignored: build output, fixtures, regenerated PDFs, extraction files.
