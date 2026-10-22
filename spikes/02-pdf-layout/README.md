# PDF layout experiment: page accounting with Paged.js in headless Chromium

Throwaway experiment. It lays out blocks of the grouped pilot dataset with
**Paged.js 0.4.3** inside the **headless Chromium 153** that Playwright 1.63.0
pins, on Linux, in a container whose runtime stage is the project's **PDF
base image** (the same Chromium, fonts and system libraries the PDF worker
runs). It checks the page subtotals against the dataset, measures memory and
time, characterises how the library handles one very tall row, and compares
Paged.js margin boxes with Chromium's native `@page` margin boxes. The code is
disposable; the measurements below are what is kept.

## Hypothesis

The experiment asks five questions:

1. How much memory does the Chromium process tree take to lay out and print
   one block, as the block grows (250 to 4 000 records)?
2. Can every page subtotal always equal the sum of the amounts visible on the
   page, with a row that spans pages counted once, **on the page where it
   ends** — a "(continues)" mark where it starts, its amount printed where it
   ends — and a brought-forward / carried-forward chain that closes on the
   block total? Where does the pinned library stop laying out a single tall
   row correctly?
3. Does writing the footer (and the marks) ever change which rows land on
   which page?
4. Can page headers and footers be produced both with Paged.js and with
   Chromium's native `@page` margin boxes, and how do the two compare in
   fidelity and effort?
5. What does this say about the maintenance risk of depending on the
   pagination library?

## How to run

### Prerequisites

- Docker with **BuildKit** (the buildx plugin; the legacy builder is not
  supported) and a Linux engine.
- The **PDF base image** in the local development registry
  (`127.0.0.1:5055`), built by the local composition's single start command.
  This experiment never builds or pushes it; it only reads it.
- The grouped pilot dataset `samples/datasets/header-items-v1.grouped.10k.json`
  — generate it if missing, and check it against `samples/datasets/SHA256SUMS`:

  ```powershell
  dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset grouped
  ```

### Resolve the PDF base digest

The image is built on the base **by tag and digest**; the digest is per
machine and per build of the base, so a reproduction elsewhere measures that
machine's base and must record its own digest. From the repository root, in
PowerShell:

```powershell
$accept = "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, " +
          "application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json"
$uri = "http://127.0.0.1:5055/v2/report-service-pdf-base/manifests/1.63.0-10.0.13-noble"
$head = Invoke-WebRequest -UseBasicParsing -Method Head -Uri $uri -Headers @{ Accept = $accept }
$pdfBaseDigest = @($head.Headers["Docker-Content-Digest"])[0]
if ($pdfBaseDigest -notmatch '^sha256:[0-9a-f]{64}$') { throw "PDF base digest not resolved: '$pdfBaseDigest'" }
$pdfBaseDigest
```

### Build and run

```powershell
docker buildx version
docker buildx build --progress=plain --load `
  --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/02-pdf-layout/Dockerfile -t pdf-layout-spike:local .
$name = "pdf-layout-spike-$(Get-Date -Format yyMMddHHmmss)"
docker run --name $name --init --shm-size=1g `
  -v "${PWD}/samples/datasets:/data:ro" `
  pdf-layout-spike:local run --dataset /data/header-items-v1.grouped.10k.json `
  --sums /data/SHA256SUMS --out /out --matrix full
docker cp "${name}:/out" spikes/02-pdf-layout/results/raw
docker rm $name
```

On an engine running in WSL 2 (for example Rancher Desktop), the Windows path
that `${PWD}/samples/datasets` produces is rejected (`invalid volume
specification`). Write the same read-only bind mount of the same folder in its
WSL form, derived from the current directory, and keep the rest of the
command unchanged:

```powershell
# Windows path X:\...\<repository> becomes /mnt/x/.../<repository>
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
docker run --name $name --init --shm-size=1g `
  -v "${wslRoot}/samples/datasets:/data:ro" `
  pdf-layout-spike:local run --dataset /data/header-items-v1.grouped.10k.json `
  --sums /data/SHA256SUMS --out /out --matrix full
```

- A build **without** `PDF_BASE_DIGEST` fails on purpose
  (`invalid reference format`); the BuildKit lint warning
  `InvalidDefaultArgInFrom` on that argument is expected.
- No port is published, the container name is unique, and nothing is pruned.
- `--matrix quick` (B250 and the giant-row block, primary configuration
  only, footer on and off) is the fast loop while changing the harness; the
  same verdict is applied to the runs it contains.
- Other commands: `versions` (prints the Playwright, Chromium and .NET
  versions) and `probe-native --out DIR` (a 3-page PDF with native
  `@top-center` text and `@bottom-right` "Page X of Y").
- **Exit codes of `run`:** 0 when every check on the ordinary blocks passes in
  every configuration that lays them out, no ordinary run logs `Layout
  repeated`, and the giant-row fixture G200 shows exactly its documented
  failure signature (see Results) in every configuration, with its
  displacement and escaping checks passing; 1 otherwise — an ordinary check
  failing, a run error, or G200 behaving in any other way, **a pass
  included**; 2 for invalid arguments. The tall-row series never sets the
  exit code, except through an escaping failure or a run error. Memory and
  time are measurements, never pass/fail. The last lines of the output give
  the verdict and every reason for it.
- **How the PDF header and counter checks were made:** the native probe and
  the comparison PDFs were checked by a text-and-position read of the PDFs —
  each page's text runs decoded through the fonts' character maps, with their
  page position from the PDF's transforms — using a throwaway reader written
  with the Python standard library only, kept outside the repository, with
  nothing installed. The Paged.js pages were also checked through the page
  images the harness writes.

### Where the outputs land

`results/raw/` (ignored by version control) holds everything one run writes:
`summary.json`, `runs.csv`, every PDF (`pdf/`), page images (`png/`) and the
per-run page layout (`layout/`). The committed copies are `results/summary.json`,
`results/runs.csv`, four PDFs in `results/pdf/` and eight page images in
`results/png/`.

### Paged.js source and hashes

Paged.js is vendored, never downloaded at run time. It was taken from
`https://registry.npmjs.org/pagedjs/-/pagedjs-0.4.3.tgz` (1 109 854 bytes),
whose SHA-512 equals the registry's integrity
`sha512-YtAN9JAjsQw1142gxEjEAwXvOF5nYQuDwnQ67RW2HZDkMLI+b4RsBE37lULZa9gAr6kDAOGBOhXI4wGMoY3raw==`
(checked with Python `hashlib`); `package.json` says version 0.4.3, licence MIT.
Vendored files, byte-identical to the tarball:

| File | SHA-256 |
|------|---------|
| `PdfLayoutSpike/assets/vendor/pagedjs-0.4.3/paged.polyfill.js` (33 251 lines) | `f59f361802416c770d549a647958649af2cf6601999924bc00e4f507dad5269f` |
| `PdfLayoutSpike/assets/vendor/pagedjs-0.4.3/LICENSE.md` | `f49bdde202ca66880e2d7cb7fd8103f43a917a60b61681484a66cf370e7647e0` |

## Results

### Environment

| Item | Value |
|------|-------|
| Date | 2026-10-21, run 15:29:28–15:31:30 (UTC−3) |
| Reference environment | `docs/reference-environment.md`, page revision 2 |
| Dataset | `header-items-v1.grouped.10k.json`, SHA-256 `d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753` (10 000 records, equal to `SHA256SUMS`) |
| Container | Ubuntu 24.04.5 LTS, 16 CPUs, 11.7 GiB visible (`MemTotal` 12 244 632 KiB), no memory or CPU limit (`memory.max` = `max`); `--init`, `--shm-size=1g`; the only other running container was the shared local registry |
| PDF base image | `localhost:5055/report-service-pdf-base:1.63.0-10.0.13-noble@sha256:bdc804d2f5a4a08d7e1a1387d4a7434f15e9c5d7d22b9e968a990659f23d9c5f` (plain image manifest, 7 layers) — the same digest before and after the measurements |
| Build stage | `mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa` |
| Layer parity | The base's 7 `rootfs.diff_ids` are, in order, the first 7 of the experiment image's 10 layers; the 3 on top are the working folder, the publish output and `/out` |
| Image user and runtime | uid 1654 (`app`); `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.0.13 only |
| Browsers path | `/ms-playwright` holds only `chromium_headless_shell-1243`, root-owned, nothing world-writable |
| Playwright driver | `/app/.playwright/node/linux-x64/node` `755 root:root` (made executable in the build stage; the publish output leaves it `744`) |
| Versions | Microsoft.Playwright 1.63.0, Chromium 153.0.8010.12 (headless shell, launched without a channel), .NET 10.0.13, Paged.js 0.4.3 |
| Font | `Liberation Sans` (named explicitly); `fc-match sans-serif` resolves to **WenQuanYi Zen Hei**, not Liberation Sans |

### Memory and time per block size (footer on, 3 repetitions — median / max)

Figures as the reference environment asks: managed heap peak, process peak
RSS of the harness, Chromium process-tree peak RSS and temporary disk peak,
plus Chromium tree PSS (summed RSS counts shared pages several times; PSS does
not) and the Playwright driver. Two series, side by side: the primary
configuration **table + keep**, and **grid + keep** (the table markup loses
its column widths from page 2, so it needs about twice the pages).

**table + keep (primary)**

| Block | Pages | Chromium tree peak RSS (MiB) | Chromium tree peak PSS (MiB) | Chromium processes | Driver peak RSS (MiB) | Harness peak RSS (MiB) | Managed heap peak (MiB) | Temporary disk peak (KiB) | Layout (s) | PDF (s) | PDF size (KiB) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| B250 | 21 | 490 / 490 | 257 / 257 | 6 | 172 / 172 | 317 / 322 | 303 / 330 | 77 / 77 | 0.49 / 0.52 | 0.10 / 0.10 | 75 |
| B500 | 42 | 535 / 536 | 302 / 302 | 6 | 174 / 176 | 265 / 266 | 269 / 273 | 77 / 77 | 0.84 / 0.86 | 0.19 / 0.24 | 124 |
| B1000 | 78 | 553 / 553 | 319 / 319 | 6 | 175 / 178 | 266 / 266 | 270 / 270 | 77 / 77 | 1.52 / 1.52 | 0.33 / 0.33 | 206 |
| B2000 | 154 | 650 / 653 | 415 / 417 | 6 | 174 / 176 | 272 / 275 | 273 / 273 | 77 / 77 | 2.98 / 3.15 | 0.78 / 0.79 | 380 |
| B4000 | 307 | 791 / 875 | 560 / 646 | 6 | 176 / 176 | 288 / 300 | 351 / 380 | 77 / 77 | 6.97 / 7.41 | 1.92 / 2.10 | 731 |

**grid + keep**

| Block | Pages | Chromium tree peak RSS (MiB) | Chromium tree peak PSS (MiB) | Chromium processes | Driver peak RSS (MiB) | Harness peak RSS (MiB) | Managed heap peak (MiB) | Temporary disk peak (KiB) | Layout (s) | PDF (s) | PDF size (KiB) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| B250 | 10 | 492 / 492 | 259 / 259 | 6 | 174 / 175 | 308 / 314 | 380 / 380 | 77 / 77 | 0.37 / 0.37 | 0.08 / 0.10 | 55 |
| B500 | 19 | 530 / 530 | 297 / 297 | 6 | 174 / 175 | 314 / 314 | 381 / 381 | 77 / 77 | 0.48 / 0.59 | 0.10 / 0.10 | 87 |
| B1000 | 36 | 548 / 550 | 315 / 316 | 6 | 175 / 178 | 316 / 318 | 381 / 382 | 77 / 77 | 0.79 / 0.87 | 0.20 / 0.24 | 143 |
| B2000 | 72 | 610 / 611 | 377 / 378 | 6 | 175 / 175 | 349 / 361 | 404 / 419 | 77 / 77 | 1.62 / 1.65 | 0.56 / 0.57 | 259 |
| B4000 | 143 | 815 / 816 | 582 / 582 | 6 | 176 / 176 | 402 / 428 | 455 / 515 | 77 / 77 | 3.94 / 4.09 | 0.96 / 1.02 | 493 |

- **Idle browser baseline** (browser started, no page): Chromium tree RSS
  269 MiB (275 044 KiB), PSS 116 MiB, 5 processes; driver 127 MiB; harness
  90 MiB; managed heap 4 MiB.
- The grid markup halves the pages and roughly halves the layout and PDF
  times; the Chromium tree peaks are close for both markups (at 4 000
  records, 791 MiB RSS / 560 MiB PSS with table, 815 / 582 MiB with grid).
- No block timed out (600 s limit) in either series.
- The harness RSS and managed heap figures are the running process's: the
  grid series runs after the table series in the same process, so its
  harness figures include what the process already held. The managed heap
  figure is `GC.GetGCMemoryInfo().HeapSizeBytes`, which includes
  fragmentation; it is reported as measured.
- Container cgroup `memory.peak` at the end of the whole matrix: 999 MiB.
- Sampling: every 50 ms from navigation until the PDF is written (`/tmp`
  every 500 ms). Temporary disk under `/tmp` stays at ~77 KiB.

### Page accounting and footer displacement on the ordinary block (B1000, footer and marks on; displacement compares on vs off)

| Configuration | Pages | Split rows | Rows not lost / duplicated | End-page subtotals | Brought / carried chain | Visible amounts | "Continues" marks | Split attributes agree | Geometry | Escaping and isolation | Footer does not displace rows | Split fragments keeping their columns |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| table + keep (primary) | 78 | 8 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS (78 pages identical) | 0/16 |
| grid + keep | 36 | 0 | PASS | PASS | PASS | PASS | PASS | n/a (no split) | PASS | PASS | PASS (36 pages identical) | — |
| table + split | 69 | 34 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS (69 pages identical) | 0/68 |
| grid + split | 35 | 13 | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS (35 pages identical) | 26/26 |

Every check also passed on every memory-series block, in both series (B250,
B500, B1000, B2000 and B4000, three times each), and the **layout was
deterministic**: a fourth B1000 run of the primary gave the same 78 pages
with the same ordered fragments as the first memory run. No ordinary run
logged anything on the page console.

**Rows spanning pages on the ordinary blocks** (an observation, never a
check): with table + keep, 8 rows span two pages on B1000 and 27 on B4000;
the longest description among them has 2 488 characters (B2000 and B4000),
and none spans more than 2 pages. With grid + keep no ordinary row spans
pages. All of these rows split with exact text and pass every check.

**What works.** On every ordinary block, in all four configurations, each
page's printed subtotal equals the exact decimal sum of the amounts of the
rows that end on it; a split row is counted once, on its last page, its
amount is printed only there and every earlier fragment carries
"(continues)"; pages where no row ends show `0.00` (8 of 78 on B1000 with
table + keep); the chain starts at `0.00` and ends at the block total. The
footer sits in the bottom page margin, which exists before layout, and never
moved a row: every on/off pair has identical page counts and fragment lists,
and no fragment extends below the content area. The accounting is done once
the whole block is laid out (money as integer cents), and the
`data-split-from` / `data-split-to` attributes Paged.js writes agree with the
end-page rule on every page (`data-split-to` is written on a page only while
the *next* page is laid out — per-page accounting during layout would need a
one-page lag).

**Column widths with the table markup.** Paged.js keeps the table's
`<colgroup>` widths only on page 1. From page 2 on, every page's rebuilt
table has four equal columns (the description column shrinks from ~120 mm to
~46 mm), so the same rows take more height: **78 pages for B1000 with the
table markup against 36 with the grid markup and 35 natively**, and rows of
about 2 000 characters split even under "keep". The grid markup keeps its
columns on every page, also in continuation fragments (26/26 on B1000 with
split).

**Keep-together.** `break-inside: avoid` is honoured on both markups: with
grid + keep no ordinary row split; with table + keep only rows that became
taller than the content area (because of the narrower columns) split.

### The giant-row fixture (G200): a characterised limit

G200 is the first 200 records with **record 50's description repeated to
12 171 characters** and record 120's to 30 047 characters. It is laid out
and checked by exactly the same rules as every other block; its expected
result is a **documented failure signature**, which each configuration's
footer-on run must show exactly:

- **S1** — the run completes with status `ok`: the layout signal is `done`,
  the library resolves `preview()`;
- **S2** — the console holds an error containing `Layout repeated`;
- **S3** — the row-coverage rule fails only by missing rows, and the missing
  rows are exactly every row after record 50 (sequences 51–200); nothing up
  to record 50 is missing, nothing is duplicated or unknown.

| Configuration | Pages | Rule lines as printed (footer on) | S1 | S2 | S3 | Displacement (on vs off) | Escaping | Result |
|---|---|---|---|---|---|---|---|---|
| table + keep | 22 | FAIL rows (150 sequences missing, first 51); PASS end-page subtotals; FAIL chain p22 (ends at 1305258.06, block total 5155412.18); FAIL visible amounts p22 (sequence 50 fragment 20/20 shows '', expected '46323.27'); PASS marks; FAIL split attributes p21; PASS geometry | holds | holds | holds | PASS (22 pages identical) | PASS | signature |
| grid + keep | 10 | FAIL rows (150 missing, first 51); PASS subtotals; FAIL chain p10; FAIL visible amounts p10 (fragment 8/8); PASS marks; FAIL split attributes p9; PASS geometry | holds | holds | holds | PASS (10 pages identical) | PASS | signature |
| table + split | 22 | the same lines as table + keep (pages 21–22) | holds | holds | holds | PASS (22 pages identical) | PASS | signature |
| grid + split | 10 | the same lines as grid + keep (pages 9–10) | holds | holds | holds | PASS (10 pages identical) | PASS | signature |

Console message, the same in all four: `error: Layout repeated at:
JSHandle@node`. From here on, "shown" means characters in the DOM fragments
the library laid out, not text printed in the PDF. For a 12 171-character
description, the text shown for record 50 is 49 749 characters (table) and
40 524 characters (grid): the layout repeats description text from page to
page until the library's own loop guard stops it. With the table markup it
also drops text at each page break: the PDF prints less than the DOM
fragments hold (the measured printed figures are under the tall-row series
below). The layout leaves the last page unfinished (no header, footer or page
counter on it — page images `…page022.png` and `…page010.png`) and still
resolves `preview()` as if the document were complete. The rows after record
50 are lost; record 120 is never reached, so its row has not been laid out.
The independent row-coverage check is what detects the loss; the in-page
accounting stays internally consistent on what was laid out. G200's `FAIL`
lines are kept here and in `results/summary.json` exactly as they came out.

### Calibration and the tall-row series (bracketing)

To see where the limit begins, one tall row is laid out at a time. The
**calibration** lays out one natural row once per markup, footer off: the
record with the longest description among the first 1 000 records (sequence
953, 2 395 characters, unchanged). It laid out as **a single fragment in both
markups** (fragment count 1). From its height an estimate *c* — description
characters that would fill one content area at page-1 column widths — sets
the target lengths of the tall rows; it is only a way to choose the sizes,
and the results below state the lengths and spans actually observed.

| Markup | Description length | Fragments | Row height (px) | Content-area height (px) | *c* = length × area ÷ row height |
|---|---|---|---|---|---|
| table + keep | 2 395 | 1 | 454.5 | 997.8 | 5 257.5 |
| grid + keep | 2 395 | 1 | 454.5 | 997.8 | 5 257.5 |

Each **tall-row fixture** is the first 100 records with only record 50's
description (178 characters) repeated, copies joined by one space, until it
has at least ⌈*k* × *c*⌉ characters; records 51–100 follow it. Each is laid
out once per markup, footer on. *k* is the target multiple used to choose the
size, not a measured height. The **span** is the number of pages that hold a
fragment of the tall row; for a failed run it counts **pages laid out,
including repeated text**, not the height of the row.

| Markup | Fixture | *k* | Description length | Characters shown (DOM) / length | Span | Result | Console |
|---|---|---|---|---|---|---|---|
| table + keep | T150 | 1.5 | 8 054 | 49 749 / 8 054 | 20 pages laid out, including repeated text (pages 3–22) | signature | `Layout repeated` |
| table + keep | T200 | 2 | 10 560 | 49 749 / 10 560 | 20 pages laid out, including repeated text (pages 3–22) | signature | `Layout repeated` |
| table + keep | T300 | 3 | 15 930 | 49 749 / 15 930 | 20 pages laid out, including repeated text (pages 3–22) | signature | `Layout repeated` |
| table + keep | T500 | 5 | 26 312 | 49 749 / 26 312 | 20 pages laid out, including repeated text (pages 3–22) | signature | `Layout repeated` |
| grid + keep | T150 | 1.5 | 8 054 | 8 054 / 8 054 | 2 pages (pages 3–4) | **pass** | none |
| grid + keep | T200 | 2 | 10 560 | 40 524 / 10 560 | 8 pages laid out, including repeated text (pages 3–10) | signature | `Layout repeated` |
| grid + keep | T300 | 3 | 15 930 | 40 524 / 15 930 | 8 pages laid out, including repeated text (pages 3–10) | signature | `Layout repeated` |
| grid + keep | T500 | 5 | 26 312 | 40 524 / 26 312 | 8 pages laid out, including repeated text (pages 3–10) | signature | `Layout repeated` |

- "signature" means S1–S3 hold with the tall record in place of G200's record
  50: status `ok`, the console message, and exactly rows 51–100 lost. No run
  was classed "other"; no run timed out.
- **grid + keep:** the largest tall row that passes is T150 — an 8 054-character
  description over 2 pages, its text shown exactly once, every check
  passing; its page 3 holds only the row's first fragment and correctly shows
  a page subtotal of `0.00`. The smallest that fails is T200 — a
  10 560-character description, 8 pages laid out, including repeated text.
  The outcomes form a single boundary.
- **table + keep:** none of the four tall rows passes; the smallest, T150
  (8 054 characters), already fails. With this markup every tall row, and
  G200's 12 171-character row, gives the same layout: pages 3–22, about
  2 490 characters on each in the DOM fragments, 49 749 characters shown
  whatever the length. The PDF prints less, measured on its text extraction:
  about 1 690 characters per page printed (1 680–1 711 on pages 3–22),
  33 862 in total on table T150, with 7–12 words skipped at each page break
  (the description's repeating unit is 23 words; G200's table + keep PDF
  prints the same figures). With this markup, text is therefore dropped at
  page breaks as well as repeated. The rows that pass with this markup are the ordinary rows of up to
  2 488 characters spanning 2 pages (see above). The data place the start of
  the limit for this markup between those two lengths, and do not bracket it
  more closely.
- These outcomes hold for Paged.js 0.4.3, Chromium 153.0.8010.12 and this
  markup; they are not generalised to another version, browser or markup.
- A row spanning three or more pages was not exercised by any passing run.

### Verdict of the evidence run

**Exit 0**, with no failure reason: every check on the ordinary blocks
passed in every configuration that laid them out (B1000 in all four, both
memory series, the determinism pair, the native runs), no ordinary run logged
`Layout repeated`, G200 showed exactly the signature in all four
configurations with displacement and escaping passing, and the calibration and
tall-row runs had no escaping failure and no run error. 60 runs, all status
`ok`; 516 `PASS` and 48 `FAIL` check lines, every `FAIL` on G200 (20) or a
tall-row run classed signature (28).

### Paged.js margin boxes vs Chromium's native margin boxes (B1000, same `@page` header and counter CSS)

| | Paged.js (table + keep) | Native Chromium 153 (table) |
|---|---|---|
| Header text (`@top-left`) on pages 1, 2 and last | yes — top left | yes — top left |
| "Page X of Y" (`@top-right`) on pages 1, 2 and last | yes — top right: 1, 2, 78 of 78 | yes — top right: 1, 2, 35 of 35 |
| Total pages | 78 (`flow.total`, equal to the PDF page objects) | 35 (PDF page objects) |
| Page subtotals in the footer | yes (brought forward / page subtotal / carried forward at the bottom left, centre and right, from the finished layout) | no — no hook sees which rows a page holds |
| "(continues)" marks | yes | no |
| Table header repeated on every page | no | yes |
| CSS needed | the 2 margin-box rules, plus 3 empty bottom-box rules | the 2 margin-box rules |
| Script needed | 33 251-line library + 280-line layout script + a 1-line configuration | none |
| Navigation / layout / PDF time (median of 3) | 0.20 s / 1.52 s / 0.33 s | 0.05 s / — / 0.21 s |
| Chromium tree peak RSS / PSS (median) | 553 / 319 MiB | 499 / 266 MiB |

Native margin-box probe (`probe-native`, 3 pages): Chromium 153 prints
`@top-center` "Native margin box probe" at the top centre of every page and
`@bottom-right` "Page 1 of 3", "Page 2 of 3", "Page 3 of 3" at the bottom
right — native margin boxes and the `page`/`pages` counters work in the
pinned Chromium. These checks were made by the text-and-position read of the
PDFs described under How to run.

### Other observations

- **Isolation and escaping:** in all 60 runs, 0 requests left the packaged
  assets, 0 dialogs opened, and the page held exactly the scripts the harness
  wrote; the hostile strings of the dataset (template syntax, markup) print as
  plain text.
- **Repeated table headers:** Paged.js does not repeat `<thead>` on later
  pages; native printing does.
- **Group headers:** with Paged.js the start row of group G0004 is the last
  line of its page on B1000 (page 32 of 78 with the table markup, page 15 of
  36 with grid), its first item on the next page; natively no page ends with
  a group start row. No keep-with-next rule was applied — observed, not
  solved.
- **Fonts:** only `Liberation Sans` is used; the generic `sans-serif` maps
  to a CJK font in this base, so templates must name their family.
- **Page count heuristic:** the count of `/Type /Page` objects in the PDF
  equalled Paged.js `flow.total` in every run.
- **Playwright version:** the `Microsoft.Playwright` 1.63.0 assembly's
  informational version reads `1.0.0+<commit>`; the package version is in
  its file version.
- **Driver process:** the Playwright driver's process name is `MainThread`
  (the name of Node's main thread), not `node`; it is found by its
  executable path.

## Conclusion

- **The page-subtotal requirement is met on every ordinary block**: in all
  four configurations, and in both memory series, the printed subtotals
  equal the rows visible on each page under the end-page rule, the marks and
  the chain match the dataset exactly, the footer never moves a row, and the
  layout is deterministic — including the rows of 2 500 characters or fewer
  that span pages (up to 2 488 characters over 2 pages here).
- **It is met with one documented limit.** With the pinned library, a
  12 171-character description (G200 record 50) makes the layout stop with a
  `Layout repeated` console message while `preview()` reports completion,
  and the rows after it are lost. The independent row-coverage check is what
  detects the loss; nothing in the library's API reports it.
- **Where the limit begins, as far as the data show:** with grid + keep, an
  8 054-character description laid out correctly over 2 pages, and a
  10 560-character description failed (8 pages laid out, including repeated
  text). With table + keep, rows of up to 2 488 characters over 2 pages laid
  out correctly and an 8 054-character description already failed (20 pages
  laid out, including repeated text). No threshold beyond these measured
  lengths is claimed, for this or any other library version, browser or
  markup.
- **The table markup loses its column widths after page 1** under Paged.js,
  roughly doubling the page count; the grid markup keeps them. In this
  series the smallest failing tall row was shorter with the table markup
  than with grid, but that the lost widths cause it is unproven: the series
  does not isolate it.
- **Native margin boxes are a working option for headers, page numbers and
  "Page X of Y"** in Chromium 153, with no script, lower memory and time, and
  native table-header repetition — but not for page subtotals or marks.
- Memory per block grows with the block above the idle browser; at 4 000
  records the Chromium tree peaks at ~0.8 GiB RSS (~0.56–0.58 GiB PSS) with
  either markup.

No decision is taken here; these are the measurements for it.

## Packages and licences

| Component | Version | Licence | Note |
|-----------|---------|---------|------|
| Microsoft.Playwright (NuGet) | 1.63.0 | MIT | the only package; transitive Microsoft.Bcl.AsyncInterfaces 6.0.0 and System.ComponentModel.Annotations 5.0.0 (MIT); no known vulnerability. Its bundled driver runs as a separate process |
| Chromium headless shell | 153.0.8010.12 (revision 1243), via Playwright | BSD-3-Clause | its own bundled third-party notices, `LICENSE.headless_shell` (2 257 005 bytes, SHA-256 `b92247f7a44c14627ef5cbbe0aa6dcca4e4422b7c05e6f2c660054061a5e3da7`), are **inventoried, not gated**: they include GNU Lesser General Public License texts (12) — FFmpeg compiled into Chromium among them (13 mentions) — Mozilla Public License texts (20) and many BSD-style notices |
| Paged.js | 0.4.3 | MIT | `LICENSE.md` shipped in the tarball and vendored |
| Fonts (content of the PDF base, from the Ubuntu archive) | `fonts-liberation` 1:2.1.5-3 (SIL-OFL-1.1), `fonts-freefont-ttf` 20211204+svn4273-2 (GPL-3+), `fonts-ipafont-gothic` 00303-21ubuntu1 (IPA-1), `fonts-noto-color-emoji` 2.047-0ubuntu0.24.04.1 (Apache-2.0), `fonts-tlwg-loma-otf` 1:0.7.3-1 (GPL-2+), `fonts-unifont` 1:15.1.01-1build1 (GPL-2+), `fonts-wqy-zenhei` 0.9.45-8 (GPL-2 with font embedding exception) | per package `copyright` file | aggregated under their own licences; only Liberation Sans is used in the PDFs |
| PDF base image | `report-service-pdf-base:1.63.0-10.0.13-noble` | project-owned | the experiment adds no package, font or browser to it |
| .NET images | `sdk:10.0.402-noble` (build stage), `aspnet:10.0.13-noble` (under the base) | MIT | |

The harness is original code.

## Maintenance risk of the pagination library — observations

- **The serious failure is silent.** With Paged.js 0.4.3 in Chromium 153 and
  this markup, a single tall row (an 8 054-character description with the
  table markup, a 10 560-character one with grid, G200's 12 171-character
  one with both) makes the library repeat text (with the table markup it
  also drops text at each page break), log `Layout repeated`, stop
  its layout loop without finishing the last page, and resolve `preview()`
  as if complete; every following row is lost. Nothing in its API reports
  the loss; only the console and an independent coverage check reveal it.
- **Table support is shallow:** the `<colgroup>` is dropped on rebuilt
  tables (page 2 on), and `<thead>` is not repeated.
- **The accounting depends on 0.4.3 internals:** the `data-split-from` /
  `data-split-to` attributes (the latter written one page late by a built-in
  handler), the `.pagedjs_page` / `.pagedjs_area` / `.pagedjs_margin-*`
  element structure, the rule that `content: ""` creates a margin box, and
  the `afterRendered` hook running inside the flow, before `preview()`
  resolves. None of these is a documented, versioned contract.
- **Workarounds needed in this experiment:** none to the library itself
  (it is vendored unmodified); the harness reads the console to record the
  library's own failure messages, and checks row coverage independently.
- **What the native fallback covers:** headers, page numbers and "Page X of
  Y" (verified in Chromium 153), with table-header repetition — but no page
  subtotals and no "(continues)" marks.
