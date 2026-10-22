# 03 — PDF assembly: ordered merge and global page numbering (measurement)

A throwaway harness that takes hundreds of seeded synthetic partial PDFs,
merges them in manifest order and stamps "Page X of Y" with global numbers
into a reserved footer band, with **PDFsharp 6.2.4** (Core build, DejaVu
fonts served by a font resolver) as the main candidate and **qpdf** as the
merge alternative, on Linux, in a container. It measures memory, time,
temporary disk and output size at 1 000, 5 000 and 10 000 pages, and checks
every output independently: structure (qpdf), page order and numbering on
every page (text extracted by poppler) and a strict pixel comparison
(rendered by poppler).

The code is disposable; the measurements below are kept. No decision is
taken here.

## Hypothesis

- **H1** — a single-pass PDFsharp merge (and merge + stamp) grows with the
  page count, because imported pages stay alive until the output is saved.
- **H2** — the staged variant (batches of 25 parts merged and numbered to
  temporary disk, then merged) keeps PDFsharp's peak memory bounded.
- **H3** — qpdf merges with bounded memory.
- **H4** — stamping changes nothing outside the numbering area.

"Bounded" is a reporting convention fixed before measuring, not a target:
a variant is **bounded** when its median peak RSS at 10 000 pages is at most
1.5 × its median peak RSS at 1 000 pages (for the stamp-then-qpdf variant,
the larger of its two steps). A failed or killed 10 000-page run is "not
bounded". The raw figures are always published.

## Variants

| Id | Name | What it does |
|----|------|--------------|
| V1 | `pdfsharp-merge` | One output document; every part opened for import in manifest order, every page added, the source closed; one save at the end. No numbers |
| V2 | `pdfsharp-merge-stamp` | As V1, and each added page is stamped with its global number in the same pass |
| V3 | `pdfsharp-staged` | Batches of 25 parts merged and stamped into temporary files, then the batch files merged by import (V1 code) |
| V4 | `qpdf-merge` | `qpdf @argfile` with `--empty --pages <every part in manifest order> --`. No numbers |
| V5 | `pdfsharp-stamp-qpdf-merge` | Step a: every part opened, stamped with its global numbers and saved to temporary disk (PDFsharp). Step b: qpdf merge of the stamped parts (V4 code) |

The stamp is "Page X of Y" in DejaVu Sans 8 pt, centred in the bottom 15 mm
of the A4 page, which every partial PDF leaves empty.

## How to run

From the repository root, PowerShell.

1. The qpdf release: the latest stable release at the time of the run was
   **12.4.2** (`https://api.github.com/repos/qpdf/qpdf/releases/latest`,
   `prerelease: false`, published 2026-09-27). Its Linux binary
   `qpdf-12.4.2-bin-linux-x86_64.zip` has the SHA-256
   `db367d897829f22c4198ce1094143c9d467bd6ee7dfabc44ba6f02056b24f8b1`, the
   value the release publishes (asset digest and `qpdf-12.4.2.sha256`). The
   image build checks it and fails on any other value.
2. Build the image. Docker with BuildKit (buildx) is required: the
   per-Dockerfile ignore file `Dockerfile.dockerignore` is a BuildKit
   feature, and `--progress=plain` makes a legacy-builder build fail at once.

   ```powershell
   docker build --progress=plain -f spikes/03-pdf-assembly/Dockerfile `
     --build-arg QPDF_VERSION=12.4.2 `
     --build-arg QPDF_SHA256=db367d897829f22c4198ce1094143c9d467bd6ee7dfabc44ba6f02056b24f8b1 `
     -t pdf-assembly-spike:local .
   ```

3. Run the full matrix (3 sizes × 5 variants × 3 repetitions, each measured
   step a fresh process under GNU `time -v`):

   ```powershell
   New-Item -ItemType Directory -Force spikes/03-pdf-assembly/results/raw | Out-Null
   $ts = Get-Date -Format yyMMddHHmmss
   docker run --rm --init --name "pdf-assembly-spike-$ts" `
     --memory 8g --memory-swap 8g --cpus 4 `
     -v "${PWD}/spikes/03-pdf-assembly/results/raw:/out" `
     pdf-assembly-spike:local matrix --out /out --work /work --repetitions 3
   ```

   `--memory-swap` equal to `--memory` disables swap, so pages cannot leave
   RAM and hide memory growth. No port is published. From Git Bash the
   bind-mount source must take the engine VM's form (for example
   `/mnt/d/<path>/spikes/03-pdf-assembly/results/raw:/out`, with
   `MSYS_NO_PATHCONV=1`); the `C:\…:/out` form fails with "invalid mode".
4. The fast loop: add `--quick` (1 000 pages, one repetition, full
   verification).
5. The checks on their own, inside one container (sets under `/work`):

   ```text
   generate --pages 1000 --out /work/sets/1000
   generate --pages 1000 --out /work/sets/1000-repeat
   repro    --manifest /work/sets/1000/manifest.json --repeat-manifest /work/sets/1000-repeat/manifest.json --temp /work/repro --report /out/repro.json
   controls --manifest /work/sets/1000/manifest.json --temp /work/controls --report /out/controls.json
   probe    --out /out/probe
   versions
   ```

Before measuring anything, `matrix` (also with `--quick`) generates the
three sets, generates the 1 000-page set a second time and compares the two
generations (`repro`), runs the controls of the structural check
(`controls`), and checks every source part of every set. Any unexpected
result writes `summary.json` and `verification.json` with
`stoppedBeforeMeasuring` and exits 1 with no measurement taken. Exit codes:
0 success and every check passes, 1 a check failed or a run errored, 2
invalid arguments. Memory, time and disk are measurements and never decide
the exit code.

Outputs: `results/raw/` (ignored) receives `runs.csv`, `summary.json` and
`verification.json`; the copies in `results/` are the ones published here.
`results/samples/` holds the first partial PDF of the 1 000-page set and the
first three parts merged and numbered by V2 (49 pages). Both are fresh
generations: their raw bytes differ from the run's, their masked hash does
not (see *Reproducibility*).

## Results

Run of 2026-10-21, 14:06:52 to 14:12:42 (UTC−03:00), exit 0. Every one of
the 45 size × variant × repetition combinations ran to completion; no
timeout, error or out-of-memory kill.

### Environment

As [`docs/reference-environment.md`](../../docs/reference-environment.md)
asks:

| Item | Value |
|------|-------|
| Date | 2026-10-21 |
| Reference environment page | revision 2 |
| Machine | Intel Core i9-14900, 8 cores / 16 threads, 24 GiB; Windows 11 Pro 10.0.26300 |
| Container runtime | Rancher Desktop, Docker Engine 29.4.0-ce `linux/amd64`, WSL 2 VM with 16 CPUs and 11.7 GiB; BuildKit v0.29.0 through buildx v0.35.0 |
| Container limits | `--memory 8g --memory-swap 8g --cpus 4` — cgroup `memory.max` 8589934592, `memory.swap.max` 0, `cpu.max` 400000 100000; 4 processors seen by .NET |
| Base images | `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` `sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa` (build stages) · `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble` `sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac` (runtime stage) |
| Runtime in the image | .NET 10.0.13, Ubuntu 24.04.5 LTS, x64, workstation GC; the container runs as uid 1654 |
| Libraries and tools | PDFsharp 6.2.4 (assembly 6.2.4.0); qpdf 12.4.2; poppler-utils 24.02.0-1ubuntu9.10 (pdftotext, pdftoppm 24.02.0); GNU time 1.9-0.2build1; fonts-dejavu-core 2.37-8 |
| Image | `pdf-assembly-spike:local`, 114 080 842 bytes (`docker image inspect … {{.Size}}`, the CONTENT SIZE column of `docker image ls`), DISK USAGE 405 MB; clean build 38.1 s, incremental build after a source change ≈ 12 s |

**The four memory figures:** (1) managed heap peak — sampled every 20 ms
inside the .NET process (`n/a` for qpdf); (2) process peak RSS — GNU `time`
"Maximum resident set size" of the fresh process, the authoritative figure
(`VmHWM` is the cross-check in `runs.csv`); (3) **Chromium process-tree peak
RSS — n/a, no browser runs**; (4) temporary disk used — the peak size of the
run's temporary folder, sampled every 100 ms from outside the measured
process.

**Input — synthetic partial PDFs, not a pilot dataset.** Seed 20261021; 50 /
250 / 500 parts of 10–30 pages for 1 000 / 5 000 / 10 000 pages (3.9 / 19.4 /
38.8 MB). Each page carries a heading, a marker `[[Pnnnn.kkk]]` (part,
page), the part's own 64 × 64 BMP logo, two or three paragraphs and a 12 × 4
table. File names are hash-like, so alphabetical order is not manifest
order. The reproducible identity of each set is its **masked set digest**
(see *Reproducibility*); the raw manifest hash is given beside it only to
show that it changes at every generation:

| Pages | Parts | Masked set digest (reproducible) | Raw manifest SHA-256 of this run (changes at every generation) |
|-------|-------|----------------------------------|----------------------------------------------------------------|
| 1 000 | 50 | `8a4de9c5256af329b75187b2824100788fdec3d133d21d38e3cf121542c35bcd` | `57a65a4386e92a18592fcaf75dba3fce1e6e7e6aa0e29723cc7d8a29c126f9ec` |
| 5 000 | 250 | `dda0f43d7584d00e7fdd57f5b63852c2efef2789fe947e8f8a224c97a34ea397` | `9afd32226000d58db1555d418a97292d40ce676190e4899dda0d88143501369e` |
| 10 000 | 500 | `11a53a858f9053b8ca62d5445e2e62c129a2320865b9b59405765a5bc3f38990` | `3252132b304951bedc77f47af264f7e2fa5312b57a70ef2474389c37fd24058b` |

The 1 000-page masked set digest was the same in all six generations of
the day on this image (two each in the standalone check, the quick run and
the full run), while each generation had its own raw manifest hash.

### Memory, time, disk and size — median (max) of 3 fresh processes

| Variant | Pages | Peak RSS MiB | Managed heap peak MiB | Run time s | Temporary disk peak MiB | Output MiB |
|---------|-------|--------------|-----------------------|------------|-------------------------|------------|
| V1 pdfsharp-merge | 1 000 | 75.3 (75.5) | 24.6 (25.7) | 0.50 (0.54) | 0 | 3.61 |
| | 5 000 | 117.3 (117.6) | 60.4 (60.5) | 1.88 (1.92) | 0 | 18.08 |
| | 10 000 | 166.0 (166.3) | 105.0 (105.0) | 2.93 (2.99) | 0 | 36.23 |
| V2 pdfsharp-merge-stamp | 1 000 | 85.7 (86.4) | 30.9 (31.5) | 0.64 (0.66) | 0 | 3.83 |
| | 5 000 | 149.3 (150.7) | 89.3 (89.3) | 2.48 (2.61) | 0 | 19.08 |
| | 10 000 | 221.2 (222.8) | 158.0 (158.5) | 4.72 (4.89) | 0 | 38.20 |
| V3 pdfsharp-staged | 1 000 | 97.8 (99.8) | 34.5 (35.8) | 1.11 (1.16) | 3.84 | 3.85 |
| | 5 000 | 151.0 (152.8) | 71.6 (74.9) | 4.10 (4.14) | 19.22 | 19.28 |
| | 10 000 | 208.1 (209.5) | 128.9 (131.5) | 6.22 (6.57) | 38.47 | 38.63 |
| V4 qpdf-merge | 1 000 | 29.8 (29.8) | n/a | 0.14 (0.16) | 0 | 3.66 |
| | 5 000 | 109.3 (109.4) | n/a | 0.64 (0.74) | 0 | 18.31 |
| | 10 000 | 208.9 (209.0) | n/a | 1.60 (1.77) | 0 | 36.71 |
| V5 stamp parts + qpdf merge | 1 000 | 67.3 (67.7) | 19.6 (20.6) | 0.82 (0.94) | 4.96 | 4.87 |
| | 5 000 | 144.4 (144.5) | 21.6 (21.7) | 3.11 (3.32) | 24.79 | 24.39 |
| | 10 000 | 279.8 (279.8) | 22.4 (22.4) | 6.26 (6.31) | 49.62 | 48.85 |

V5's peak RSS is the larger of its two steps and its run time their sum.
Its two steps separately (median peak RSS): step a, the per-part stamping
in PDFsharp, 67.3 / 72.1 / 77.5 MiB; step b, the qpdf merge, 36.8 / 144.4 /
279.8 MiB. Every run of every combination is in `results/runs.csv`.

**"Bounded" by the convention above (ratio of median peak RSS, 10 000 to
1 000 pages):**

| Variant | RSS ratio | Managed heap ratio (informational) | Label |
|---------|-----------|------------------------------------|-------|
| V1 pdfsharp-merge | 2.20 | 4.26 | not bounded |
| V2 pdfsharp-merge-stamp | 2.58 | 5.11 | not bounded |
| V3 pdfsharp-staged | 2.13 | 3.73 | not bounded |
| V4 qpdf-merge | 7.01 | — | not bounded |
| V5 stamp parts + qpdf merge | 4.16 | 1.15 | not bounded |

### Verification

Every output was checked; repetition 1 fully (structure, page count, text of
every page, pixels), repetitions 2 and 3 by structure and page count.

| Check | Result |
|-------|--------|
| Page count | equal to the target for all 45 outputs |
| Order — exactly one marker per page, equal to the manifest's (part, page) | PASS on every page of every fully checked output (1 000, 5 000 and 10 000 pages) |
| Numbering — exactly one `Page i of N` per page (V2, V3, V5), none (V1, V4) | PASS on every page of every fully checked output |
| Pixels — 72 dpi greyscale, zero tolerance, against the source page | PASS: **0 changed pixels outside the numbering area on every checked page**; inside the area 237–323 (1 000 pages), 244–338 (5 000), 265–378 (10 000) changed pixels on numbered outputs, 0 on merge-only outputs. All 1 000 pages checked at 1 000 pages, 30 sampled pages at 5 000 and 10 000 (the list is in `verification.json`) |
| Structure (`qpdf --check`, see below) | 45 of 45 outputs `pass-exempt`, 0 `pass-clean`, 0 fail |

Structural check warning count per output (identical in all three
repetitions):

| Pages | V1 | V2 | V3 | V4 | V5 |
|-------|----|----|----|----|----|
| 1 000 | 2 150 | 4 151 | 4 152 | 2 150 | 4 200 |
| 5 000 | 10 750 | 20 751 | 20 760 | 10 750 | 21 000 |
| 10 000 | 21 500 | 41 501 | 41 520 | 21 500 | 42 000 |

Source parts, once per set before measuring: 50 / 250 / 500 parts, every
one `pass-exempt`, 23–63 warning lines per part (sums 2 150 / 10 750 /
21 500) — the merge-only outputs carry exactly the warnings of their inputs.

### Structural check finding

Every file PDFsharp 6.2.4 writes makes `qpdf --check` exit 3 (warnings): one
warning per compressed stream, always the zlib message "input stream is
complete but output may still be valid". The data decodes, but the stream
does not end with a proper zlib end-of-stream marker. This happens with
every PDFsharp compression mode. poppler reads and renders the files
unchanged. A qpdf-only merge and a plain qpdf rewrite inherit the warning
from their inputs, because they copy the streams; uncompressed streams give
exit 0.

The check used here therefore accepts **exactly that one message and
nothing else**: exit 0, or exit 3 when every `WARNING:` line names the
checked file and ends, after its offset, with exactly that text (ordinal,
full-tail comparison), the only other line is qpdf's closing line, last, and
no line contains "error". Exit 2, any other warning text, an error line or
output that does not have this shape fail. Every checked file records its
exit code, verdict and warning counts in `verification.json`.

**The limit of the check.** The tolerated message is also the text qpdf
prints for a compressed stream cut cleanly at its end, so the check cannot
tell that case from the PDFsharp trait. It is **not** a proof of stream
integrity. The page count, the text of every checked page and the strict
pixel comparison carry that weight, and none of them was relaxed.

For any pipeline that would use `qpdf --check` (or another strict
validator) as a gate on PDFsharp output, this means: it needs the same
exact-message narrowing, uncompressed streams, or another check.

The controls of the check run before every measurement and all had their
expected verdict (18 of 18):

| Control | Input | Expected | Actual (exit, warnings / exempt) |
|---------|-------|----------|----------------------------------|
| PC-1 | the untouched first part | pass-exempt | pass-exempt (3, 27 / 27) |
| PC-2 | its `qpdf --stream-data=uncompress` rewrite | pass-clean | pass-clean (0, 0 / 0) |
| NC-1 | 8 bytes `0xFF` into the first stream's data | fail | fail — exit 2 |
| NC-2 | the first half of the file | fail | fail — other warning (3, 34 / 25) |
| NC-3 | the first `/Length` falsified to 5 | fail | fail — other warning (3, 33 / 27) |
| NC-4.i–xiii | thirteen edited transcripts (extra text after the tail, another warning, no warnings, an error line, no closing line, another path, a case change, exit 0 with warnings, exit 0 with an error line, a foreign or missing `checking` line, the closing line not last, a warning on stdout) | fail | fail, each with its reason in `verification.json` |

### Reproducibility

**PDFsharp 6.2.4 does not produce byte-reproducible output through its
public API.** Even with fixed document dates and document IDs, every save
writes a random XMP document ID and instance ID, and every embedded font
gets a random six-letter subset tag. The option that controls the XMP
metadata exists only in the 7.0 previews, which are under AGPL-3.0 and not
usable here. Two generations of the same input therefore differ in some
tens of raw bytes per part — in this run's two generations of the 1 000-page
set, **74 to 84 bytes per part** (77–85 in the standalone check and 76–85 in
the quick run of the same day; never more than 86, the size of the fields, and the count varies
with the random draw) — all inside those fields.

The experiment therefore compares two generations by masking **exactly
those two fields, in memory only**, and requiring every other byte to be
equal. It never treats the raw hashes as reproducible.

| Mask | Pattern (on the raw bytes read one byte per character) | Masked | Constant |
|------|--------------------------------------------------------|--------|----------|
| M-TAG | `/(?:BaseFont\|FontName)/(?<m>[A-Z]{6})\+DejaVu#20Sans(?:,Bold)?(?=[\x00\t\n\f\r /\[\]<>(){}%])` | the 6 tag letters | `XXXXXX` |
| M-XMP | `<xmpMM:(?<k>DocumentID\|InstanceID)>uuid:(?<m>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})</xmpMM:\k<k>>` | the 36 UUID characters | `00000000-0000-0000-0000-000000000000` |

(`\|` is the table's escape of the regex `|`.) A pair of files is
**masked-identical** only when, in this order: both have the expected spans
(exactly one document ID and one instance ID; an even number, at least two,
of tag spans, each distinct tag exactly twice); the lengths are equal; the
spans are at the same offsets; every offset where the raw bytes differ lies
inside a masked span; and the masked SHA-256 values are equal. Each part's
`manifest.json` entry keeps the raw `sha256` (the file's identity) beside
the compared `maskedSha256`. The **masked set digest** is the SHA-256 of the
lines `order|file|pages|firstGlobalPage|maskedSha256` of every part in
manifest order, joined by LF — the figure a reader can reproduce.

Result in the full run: 50 of 50 part pairs masked-identical; 4 tag spans
(2 distinct tags) and 2 metadata ID spans in every part; 0 differing bytes
outside the spans; equal structure; both raw manifest hashes recorded and
different (`57a65a43…26f9ec`, `6b3c368e…9e6175`). After the controls the two
base files still had their manifest hashes, so masking never wrote a file.

The controls of the comparison run before every measurement and all had
their expected verdict (18 of 18). The base is the first part of the first
generation (A) and the same part of the second (B); each changed copy is a
copy of B changed one way:

| Control | Change | Expected | Actual |
|---------|--------|----------|--------|
| MC-1 | none — A against the real B | masked-identical, raw hashes different, ≥ 1 differing byte, all inside spans | as expected (82 differing bytes) |
| MC-2 | A against a byte copy of A | masked-identical, raw hashes equal, 0 differing bytes | as expected |
| MC-3 | one byte XOR `0x01` in the first stream's data | fail — outside-span | as expected |
| MC-4 | the first FontName tag's letters shifted by one | fail — tag-pairing (the masked hash alone would be equal) | as expected |
| MC-5 | the last letter of the first BaseFont tag replaced by `1` | fail — span-count | as expected |
| MC-6 | a letter of the document ID made uppercase | fail — span-count | as expected |
| MC-7 | one LF byte appended | fail — length-differs | as expected |
| MC-8 | one byte of the trailer `/ID` XOR `0x01` | fail — outside-span | as expected |
| MC-9 | one digit of the information `/CreationDate` XOR `0x01` | fail — outside-span | as expected |
| MC-10 | a non-hex character (`g`) in the document ID | fail — span-count | as expected |
| MC-11 | the document ID one character longer | fail — span-count | as expected |
| MC-12 | the family of a FontName tag changed to `DejaVu#20Serif` | fail — span-count | as expected |
| MC-13 | a BaseFont tag of seven letters | fail — span-count | as expected |
| MC-14 | one extra tag occurrence | fail — span-count | as expected |
| MC-15 | one extra metadata ID occurrence | fail — span-count | as expected |
| MC-16 | one tag occurrence removed | fail — span-count | as expected |
| MC-17 | the instance ID element removed | fail — span-count | as expected |
| MC-18 | one tag occurrence moved (same length, same counts, other offsets) | fail — span-offsets | as expected |

This matters for any product need for byte-identical re-rendering, caching
by content hash, or comparison of signed outputs: with PDFsharp 6.2.4 it
would need a masked comparison like this one, a rewrite after saving, or
another library.

### Library behaviour checked before measuring

- **Text extraction:** with PDFsharp's default `Automatic` font encoding,
  pdftotext returns exactly one marker and one "Page X of 6" per page of a
  stamped 6-page probe; the fonts carry no `/ToUnicode` entry (5 WinAnsi
  TrueType fonts), and extraction works without one.
- **What stamping adds:** the page's content goes from 1 to 2 content
  streams; the original stream is not wrapped in save/restore, and still 0
  pixels change outside the band (175 inside).
- **Closing the sources:** each imported document can be closed right after
  its pages are added, and becomes collectable while the output is still
  open; all variants measured that pattern.
- **Images:** the 24-bit BMP logo loads and renders (2 224 of 2 704 pixels
  of the logo area non-white).

## Conclusion

No decision is taken here; these are the figures the assembly choice can
use.

- **H1 holds.** Single-pass PDFsharp memory grows with the page count:
  peak RSS 75 → 117 → 166 MiB (merge) and 86 → 149 → 221 MiB (merge +
  stamp) from 1 000 to 10 000 pages, managed heap ×4.3 and ×5.1. The growth
  is about 10 MiB (merge) and 15 MiB (merge + stamp) per 1 000 pages.
- **H2 does not hold.** The staged variant is not bounded (×2.13). Its peak
  is the final merge of the batch files, which imports every page again; it
  is a little below merge + stamp at 10 000 pages (208 vs 221 MiB) and
  costs a temporary-disk copy of the whole output and about 30 % more time.
- **H3 does not hold.** qpdf is the lightest at 1 000 pages (30 MiB) and the
  fastest at every size (1.6 s at 10 000 pages), but its memory grows
  fastest (×7.0, 209 MiB at 10 000 pages).
- **Per-part stamping is flat.** Step a of V5 (each part opened, numbered
  and saved on its own) stays at 67–78 MiB with a 20–22 MiB managed heap at
  every size. The combined variant is "not bounded" only because of its qpdf
  merge step (280 MiB at 10 000 pages).
- **H4 holds.** Stamping changed 0 pixels outside the numbering area on
  every checked page of every numbered output, and page order and "Page X
  of Y" were correct on every checked page.
- **None of the five variants is bounded** by the convention fixed before
  measuring, and none of the PDFsharp-based whole-document variants (V1–V3)
  is. In absolute terms every variant stayed below 0.3 GiB of peak RSS and
  7 s at 10 000 pages under the 8 GiB / 4 CPU limit, with no failed run;
  memory is proportional to the page count, not to the input bytes held at
  once.
- The structural-check finding and the reproducibility finding above are
  properties of PDFsharp 6.2.4's output, independent of the variant.

## Packages and licences

| Component | Version | Licence | Where |
|-----------|---------|---------|-------|
| PDFsharp | 6.2.4 (assembly 6.2.4.0; runtime assets from `lib/net10.0/`; no licence-enforcement assembly) | MIT | the harness |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 (dependency of PDFsharp) | MIT | the harness |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 (dependency of the above) | MIT | the harness |
| System.Security.Cryptography.Pkcs | **8.0.2, pinned** | MIT | the harness |
| qpdf | 12.4.2, upstream Linux binary, SHA-256 verified | Apache-2.0 | the experiment's image only |
| poppler-utils | 24.02.0-1ubuntu9.10 | GPL-2 / GPL-3 (copyright file) | the experiment's image only — the independent checker |
| GNU time | 1.9-0.2build1 | GPL-3+ / GPL-2+ (copyright file) | the experiment's image only — the peak-RSS measurement |
| fonts-dejavu-core | 2.37-8 | Bitstream Vera–derived free licence for the fonts (copyright file) | the experiment's image only |
| .NET base images | `sdk:10.0.402-noble`, `aspnet:10.0.13-noble` | MIT | build and runtime stages |
| ca-certificates, curl, unzip | 20260816ubuntu0.24.04.1, 8.5.0-2ubuntu10.16, 6.0-28ubuntu4.1 | MPL-2.0, curl licence, Info-ZIP licence | the discarded download stage only |

- **The Pkcs pin and why:** PDFsharp 6.2.4 declares
  System.Security.Cryptography.Pkcs 8.0.1, which carries a High
  (GHSA-846f-f764-76mx) and a Moderate (GHSA-2fmv-rv2c-59x7) advisory.
  8.0.2, the next patch of the same minor version (MIT, no new dependency on
  `net10.0`), clears the audit; the harness pins it centrally
  (`Directory.Packages.props`, transitive pinning). With the pin,
  `dotnet list package --vulnerable --include-transitive` reports no
  vulnerable package. A frozen PDFsharp line keeps declaring the outdated
  dependency.
- **qpdf's bundled libraries:** the release archive ships no licence or
  notice files. It bundles gnutls, nettle/hogweed, libidn2, libunistring,
  libtasn1, p11-kit, libffi and libjpeg; **their licences were not verified
  from the archive.** The binary never leaves this throwaway image; any
  product use of it needs those licences confirmed from upstream first.
- poppler-utils and GNU time are GPL tools used only to check and measure
  inside this experiment's image; nothing in the product references,
  links or ships them, and the image is never published.

## Limitations

- The partial PDFs are generated by PDFsharp, not by Chromium: fonts,
  object streams and image encoding differ, so whether browser-made parts
  pass the structural check clean, and how they behave in the merge, is not
  measured here.
- One machine, one day, three repetitions per combination; the figures are
  medians and maxima of three fresh processes.
- The structural check tolerates one exact zlib warning text and is not a
  proof of stream integrity (see *Structural check finding*).
- The reproducibility of the inputs is shown under two exact masks, not as
  byte identity (see *Reproducibility*).
- Not measured: re-rendering with page offsets instead of stamping, and
  qpdf overlay as a stamping alternative.
- The managed heap is sampled every 20 ms, so a shorter peak can be missed;
  the process peak RSS from GNU `time` is exact.
