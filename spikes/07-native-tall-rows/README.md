# Native pagination of tall rows and long free text

Throwaway experiment. It prints blocks of the grouped pilot dataset with
**Chromium's own print layout** (no pagination script, nothing running in the
page) inside the PDF base image, and reads every printed PDF back with an
**independent text-integrity checker** that compares the printed text with
the source data, row by row and character by character.

The experiment also provides a **fixture series** and a **text-integrity
check** that are written to be reused unchanged by later pagination
experiments.

## Hypothesis

1. When a detail row is taller than a page, does native print layout keep
   **every row and every character**, in both detail markups (table and
   grid), from ordinary sizes up to the 1 MiB record-size hypothesis of the
   design, including rows that span three or more pages?
2. Does it keep every character of **long free-text paragraphs placed before
   and after the detail table**, up to the 1 MiB header-size hypothesis?
3. How many **pages**, how much **memory** and how much **time** does each
   run take, and how do they compare with the earlier layout experiment
   (`spikes/02-pdf-layout`)?
4. Do the page header and "Page X of Y", printed by native `@page` margin
   boxes, stay correct on every page?
5. Where does a **bottom-aligned amount** cell print when its row is split
   across pages?

## How to run

### Prerequisites

- Docker with a Linux engine and **BuildKit / buildx** (Rancher Desktop on
  WSL 2 was used).
- The **PDF base image** in the local development registry
  (`localhost:5055/report-service-pdf-base:1.63.0-10.0.13-noble`), started
  with the repository's local composition. Resolve its digest with:

  ```powershell
  $pdfBaseDigest = (Invoke-WebRequest -Method Head `
    -Headers @{ Accept = "application/vnd.docker.distribution.manifest.v2+json" } `
    http://127.0.0.1:5055/v2/report-service-pdf-base/manifests/1.63.0-10.0.13-noble).Headers["Docker-Content-Digest"]
  ```

- The grouped dataset `samples/datasets/header-items-v1.grouped.10k.json`
  (generate it as `samples/datasets/README.md` describes); its SHA-256 is
  checked against `samples/datasets/SHA256SUMS` before anything is generated.

### Commands (PowerShell, from the repository root)

On an engine running in WSL 2 the bind-mount source must be the WSL form of
the path:

```powershell
# Windows path X:\...\<repository> becomes /mnt/x/.../<repository>
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')
```

With Docker Desktop, use `${PWD}` instead of `$wslRoot`.

```powershell
# Images (not measurements)
docker buildx build --progress=plain --load -f spikes/07-native-tall-rows/Dockerfile.check -t text-integrity-check:local .
docker buildx build --progress=plain --load --build-arg "PDF_BASE_DIGEST=$pdfBaseDigest" `
  -f spikes/07-native-tall-rows/Dockerfile -t native-tall-rows-spike:local .

# Fixtures (checker image), copied out to results/raw/fixtures
$ts = Get-Date -Format yyMMddHHmmss
docker run --name "text-integrity-check-$ts" --init -v "${wslRoot}/samples/datasets:/data:ro" `
  text-integrity-check:local fixtures --dataset /data/header-items-v1.grouped.10k.json --sums /data/SHA256SUMS --out /out/fixtures
docker cp "text-integrity-check-${ts}:/out/fixtures" spikes/07-native-tall-rows/results/raw/fixtures
docker rm "text-integrity-check-$ts"

# Measured matrix (quiet environment only: no other container running but the registry)
$name = "native-tall-rows-spike-$(Get-Date -Format yyMMddHHmmss)"
docker run --name $name --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/spikes/07-native-tall-rows/results/raw/fixtures:/fixtures:ro" `
  native-tall-rows-spike:local run --fixtures /fixtures --out /out --matrix full
docker cp "${name}:/out" spikes/07-native-tall-rows/results/raw/layout
docker rm $name

# Text-integrity check, controls and verdict (checker image, memory-capped; not measurements)
$check = "text-integrity-check-$(Get-Date -Format yyMMddHHmmss)-check"
docker run --name $check --init --memory 2g --memory-swap 2g `
  -v "${wslRoot}/spikes/07-native-tall-rows/results/raw:/raw:ro" --entrypoint sh text-integrity-check:local -c `
  "dotnet TextIntegrityCheck.dll check --fixtures /raw/fixtures --runs /raw/layout --out /out/check && dotnet TextIntegrityCheck.dll controls --fixtures /raw/fixtures --runs /raw/layout --out /out/check && dotnet TextIntegrityCheck.dll verdict --runs /raw/layout --integrity /out/check/integrity.json --controls /out/check/controls.json --matrix full --summary /out/check/summary.json"
docker cp "${check}:/out/check" spikes/07-native-tall-rows/results/raw/check
docker rm $check
```

`--matrix quick` (B100 in both markups, T300u table, F26K-b table and one
negative control) is the development loop; it is not evidence.

No port is published; containers have unique names and are removed by name;
nothing is ever pruned.

### Caps and outcome classes

Every run is a **fresh process**. Caps: 300 s of wall clock inside the run
(the design's starting hypothesis for a block's render timeout), the run's
whole process tree killed at 330 s, and the container's memory capped at
4 GiB with swap off; an out-of-memory kill is read from the cgroup's
`oom_kill` counter before and after each run.

Every run gets exactly one outcome class in `runs.jsonl`: `ok` (PDF
written), `timeout` (300 s), `killed-wall-clock` (330 s), `oom-killed`,
`crashed` (browser or page crash), `error` (a harness defect) or `not-run`
with its reason. A capped run makes the larger sizes of its series and the
remaining repetitions of its fixture `not-run (after cap at <run>)`; runs not
started within the 3-hour matrix budget are `not-run (matrix budget)`. If the
container itself dies, `run ... --resume` skips the runs already recorded.

### Outputs

- `results/raw/` (ignored): fixtures, PDFs, `runs.jsonl`, `plan.json`, the
  checker's working files.
- `results/` (committed): `summary.json`, `runs.csv`, `integrity.json`,
  `controls.json`, `fixtures.sha256`, a few small PDFs and page images.

## The fixture series and the text-integrity check

### Fixtures

One UTF-8 JSON file per fixture plus `SHA256SUMS`, generated by the checker
program (`fixtures` command) from the grouped dataset. Generation is
deterministic: generating twice gives byte-identical files. A **row fixture**
is the first 100 records of the dataset with only record 50's description
replaced, so a loss after the tall row shows as missing rows; group-start and
group-end rows are those of the dataset (a group ends inside the block only
when the next record of the whole dataset belongs to another group). The block
always ends with a block-total row.

Three kinds of text:

- **natural** — the dataset's own text;
- **rep** — record 50's own 178-character description repeated, copies joined
  by one space, to at least ⌈k × 5 257.5⌉ characters: the derivation of the
  earlier layout experiment (8 054 / 10 560 / 15 930 / 26 312 characters for
  T150 / T200 / T300 / T500; G200 repeats record 50's and record 120's own
  descriptions to 12 171 and 30 047);
- **uniq** — words from a fixed list of 48 (ligature-prone words such as
  `office`, `affine`, `waffle`; accented words such as `año`, `información`,
  `über`, `Ñandú`; plain words), chosen by a SplitMix64 generator seeded with
  20261022 XOR the FNV-1a hash of `<fixture id>/<field>`; every 6th token is a
  counter `n0000001`, `n0000002`, … unique within the text, so no two printed
  lines are equal and a repeated line or a dropped unit cannot hide. A cut
  that would end in a space takes the next character instead, so lengths are
  exact.

| Fixtures | What they hold |
|----------|----------------|
| `B1000`, `B100` | the first 1 000 / 100 records, natural text |
| `K1` | record 953 alone (the longest description of the first 1 000) |
| `T150` … `T500`, `G200` | the earlier experiment's tall rows (rep text) |
| `T150u` … `T500u` | the same lengths with unique text |
| `X64K` … `X1M` | record 50 with unique text such that the record, as a compact UTF-8 JSON item of the dataset, is within 64 bytes below 64 KiB … 1 MiB |
| `T300u-ab`, `X64K-ab` | as T300u / X64K (own seed) with the amount aligned to the bottom of its cell |
| `F8K` … `F1M` (`-b` before, `-a` after, `-ba` both; `-1p` one paragraph) | title, free text, the first 20 records, block total, free text; free text is unique text split into paragraphs at the first word boundary after every 2 000 characters; byte sizes are measured on the stored paragraphs |
| `W2a`–`W2c`, `H1`–`H3`, `S080`–`S125` | two tall cells in one row, hard line breaks, and a sweep around one page — defined for later experiments, not run here |

The generator asserts that no text contains a caption (`Seq Date Description
Amount`, `Block total`, `Group total`, with whitespace removed), the mark text
`(continues)`, a soft hyphen or a control character.

`results/fixtures.sha256` lists the fixture files; a later experiment
regenerates the fixtures and checks them against it.

### Text-integrity check

The checker (`TextIntegrityCheck`, in its own image: the .NET runtime plus
poppler-utils, **no browser**) reads only the printed PDF and the fixture
file (checked against `SHA256SUMS`); from the run record it reads only the run
identity, the outcome, the marks mode, the footer mode and, for a control run,
the declared mutation. It never reads the page, the engine's layout result or
anything a page script computed.

- **Extraction:** `pdfinfo` (page count) and `pdftotext -bbox -enc UTF-8`
  (every word with its page and box), parsed as a stream. Each word is placed
  in the header band (above 15 mm), the content area or the footer band (below
  279 mm) by its box; content words are grouped into lines (same vertical
  centre ± 2 pt) and read page by page, top to bottom, left to right — the
  checker's own reading order. Words are assigned to the columns of the
  declared template geometry by the centre of their box.
- **Normalisation N**, applied identically to printed and expected text:
  Unicode NFKC (ligature glyphs back to letters), then every whitespace
  character removed — nothing else: no case folding, no punctuation mapping,
  no de-hyphenation (`hyphens: manual` and no soft hyphens in the fixtures, so
  a printed hyphen the source lacks is an alteration).
- **Stated limits:** a difference in whitespace only (two words joined or one
  split) is invisible to N; it is reported as an observation. In rep text a
  dropped whole repeating unit plus a repeated one elsewhere cancel out; the
  unique-text fixtures close that gap.
- **Rules**, all must pass: TI-1 structure (one block-total line, a header row,
  no word across the content area's edges, an empty footer band); TI-2 every
  item row exactly once, in order; TI-3 each description equal to the source;
  TI-4 date and amount (amount exactly once); TI-5 group rows and block total;
  TI-6 free text before and after; TI-7 no `(continues)` mark (or, for a layout
  that prints them, exactly one per page a row continues on, in the row's
  amount cell or in the footer); TI-8 header text and "Page i of N" on every
  page. A failure names its class (`row-lost`, `row-duplicated`, `row-order`,
  `text-dropped`, `text-repeated`, `text-altered`, `structure-lost`,
  `mark-wrong`, `header-wrong`, `unmapped-glyph`), the row or region, the page,
  the offset of the first difference and the text around it.
- **Observations** (never failing): span of every multi-page row, where its
  amount printed, repeated header rows, word-boundary differences, ligature
  code points, where group-start rows land, header and footer text per page.
- **Controls** — the checker is trusted only if all behave: B100 and K1 pass in
  both markups; HTML-level mutations (a dropped word, a repeated 80-character
  line, an omitted row, a dropped free-text word) fail with the expected class
  at the expected place; extraction-level mutations applied to the words of a
  passing multi-page row at its page breaks (E1 delete a word, E2 repeat a
  line, E3 delete a row, E4 swap two words, E5–E8 marks placed right and wrong)
  behave as stated.
- **Verdict** (exit code): 0 only when the instrument is healthy — controls
  behave, every planned run has a record and an integrity entry, B1000 passes
  in both markups and its three repetitions give the same pages and the same
  extracted text, no dialog, aborted request or script, no harness error.
  Integrity failures of the tall-row and free-text fixtures are results, never
  part of the exit code.

### Contract for other layout harnesses

A harness writes, in its output folder, `plan.json` (`matrix`, `runs[]` with
`runId`, `fixtureId`, `markup`, `repetition`, `mutation`) and one
`runs.jsonl` line per run with at least `runId`, `fixtureId`, `markup`
(`table` | `grid`), `repetition`, `mutation` (null or `NC-W`, `NC-L`, `NC-R`,
`NC-F`), `marks` (`none` | `expected`), `footer` (`none` | `accounting`),
`outcome`, `reason`, `pdf` (path relative to the folder) and the escaping
counters `dialogs`, `abortedRequests`, `scripts`.

## Results

### Environment

| Item | Value |
|------|-------|
| Date | 2026-10-22, matrix 23:57:10 – 23:59:05 (UTC−03:00; the results files give times in UTC) |
| Reference environment | `docs/reference-environment.md`, page revision 2 (Intel Core i9-14900, 24 GiB, Rancher Desktop on WSL 2) |
| Dataset | `header-items-v1.grouped.10k.json`, SHA-256 `d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753` |
| Fixtures | 49 files; `SHA256SUMS` has SHA-256 `c1576a4ec8b335bc9a7525dbe762933f7079b1a48882ea06b1fffe7d67190316` (copied as `results/fixtures.sha256`) |
| PDF base image | `localhost:5055/report-service-pdf-base:1.63.0-10.0.13-noble@sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d` |
| Versions | Playwright 1.63.0, Chromium headless shell 153.0.8010.12, .NET 10.0.13, Ubuntu 24.04.5 LTS (container), Liberation Sans |
| Checker | poppler-utils 24.02.0-1ubuntu9.11 (`pdftotext` and `pdfinfo` 24.02.0) |
| Container caps | 16 CPUs visible, memory 4 GiB with swap 0, `/dev/shm` 1 GiB; 300 s per run, kill at 330 s |
| Quiet environment | only the local registry container running before and after the matrix |

### Verdict

`verdict --matrix full` exits **0**: the checker is trusted, all 90 planned
runs have a record and an integrity entry, B1000 passes in both markups with
identical pages and extracted text across its three repetitions, there is no
dialog, aborted request or script, and no harness error. All 90 runs ended
`ok`: none hit a cap, there was no out-of-memory kill and no browser process
was left behind. The whole matrix took 114 s of run wall-clock time.

### Controls

| Control | Mutation | Expected | Behaved |
|---|---|---|---|
| positive: B100-table | none | pass TI-1 … TI-8 | yes |
| positive: B100-grid | none | pass TI-1 … TI-8 | yes |
| positive: K1-table | none | pass TI-1 … TI-8 | yes |
| positive: K1-grid | none | pass TI-1 … TI-8 | yes |
| NC-W on B100 (HTML level) | see the fixture section | text-dropped at row 50 | yes |
| NC-L on B100 (HTML level) | see the fixture section | text-repeated at row 50 | yes |
| NC-R on B100 (HTML level) | see the fixture section | row-lost at row 51 | yes |
| NC-F on F26K-a (HTML level) | see the fixture section | text-dropped at region free-after | yes |
| NC-W on T300u (HTML level) | see the fixture section | text-dropped at row 50 | yes |
| NC-L on T300u (HTML level) | see the fixture section | text-repeated at row 50 | yes |
| NC-R on T300u (HTML level) | see the fixture section | row-lost at row 51 | yes |
| E1 (extraction level, base T300u-table-r1) | delete the first description word of row 50 on page 4 | text-dropped at row 50, page 4 | yes |
| E2 (extraction level, base T300u-table-r1) | copy the last line of page 3 (row 50) to the top of page 4 | text-repeated at row 50 | yes |
| E3 (extraction level, base T300u-table-r1) | delete every line of row 50 | row-lost at row 50 | yes |
| E4 (extraction level, base T300u-table-r1) | swap two adjacent description words of row 50 on page 4 | text-altered at row 50 | yes |
| E5 (extraction level, base T300u-table-r1) | amount of row 50 on its last page, a mark in its amount band on pages 3,4,5 | TI-7 pass (and TI-4 pass) | yes |
| E6 (extraction level, base T300u-table-r1) | as E5, plus a mark on the row's final page 6 | mark-wrong at row 50 | yes |
| E7 (extraction level, base T300u-table-r1) | amount of row 50 on its last page, a mark in the footer band of pages 3,4,5 | TI-7 pass (and TI-4 pass) | yes |
| E8 (extraction level, base T300u-table-r1) | as E5, with the mark of page 3 dropped | mark-wrong at row 50 | yes |

### Every run

Pages are `pdfinfo`'s count. "Tall row" is the replaced row (row 50, and
row 120 in G200) when it spans two or more pages; "Amount page" is the page
where its amount printed (the row's first, last or another page). Memory
figures are the peak over the run (the maximum over repetitions); the harness
figure is the `VmHWM` of the fresh run process. Every passing run compared as
many words as the source holds (144 177 for X1M). There were no word-boundary
differences in any run, and no ligature code points in the raw extraction.
Every page of every run carried the fixture title and the correct "Page i of
N" (TI-8). In table markup the header row repeated once on every page.

| Fixture | Markup | Runs | Outcome | Integrity | Pages | Tall row: pages (span) | Amount page | Chromium tree peak RSS / PSS (MiB) | Driver / harness (MiB) | Managed heap (MiB) | Navigation (ms) | PDF (ms) median / max |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| B100 | table | 1 | ok | pass | 4 | — | — | 419 / 193 | 130 / 68 | 0 | 59 | 42 / 42 |
| B100 | grid | 1 | ok | pass | 4 | — | — | 422 / 196 | 130 / 65 | 0 | 61 | 46 / 46 |
| K1 | table | 1 | ok | pass | 1 | — | — | 403 / 180 | 130 / 65 | 0 | 50 | 17 / 17 |
| K1 | grid | 1 | ok | pass | 1 | — | — | 402 / 179 | 130 / 65 | 0 | 59 | 20 / 20 |
| NC-W on B100 | table | 1 | ok | fail text-dropped | 4 | — | — | 418 / 193 | 129 / 65 | 0 | 54 | 34 / 34 |
| NC-L on B100 | table | 1 | ok | fail text-repeated | 4 | — | — | 419 / 192 | 130 / 65 | 0 | 60 | 39 / 39 |
| NC-R on B100 | table | 1 | ok | fail row-lost | 4 | — | — | 419 / 193 | 130 / 65 | 0 | 56 | 39 / 39 |
| NC-F on F26K-a | table | 1 | ok | fail text-dropped | 5 | — | — | 416 / 189 | 130 / 66 | 0 | 59 | 37 / 37 |
| B1000 | table | 3 | ok | pass | 36 | — | — | 486 / 260 | 131 / 73 | 17 | 91 | 268 / 330 |
| B1000 | grid | 3 | ok | pass | 36 | — | — | 503 / 277 | 132 / 73 | 17 | 87 | 305 / 381 |
| T150 | table | 1 | ok | pass | 6 | row 50: 3–4 (2) | 3 (first) | 420 / 194 | 130 / 66 | 0 | 54 | 40 / 40 |
| T200 | table | 1 | ok | pass | 6 | row 50: 3–5 (3) | 3 (first) | 419 / 194 | 129 / 65 | 0 | 61 | 48 / 48 |
| T300 | table | 1 | ok | pass | 8 | row 50: 3–6 (4) | 3 (first) | 420 / 195 | 129 / 66 | 0 | 60 | 44 / 44 |
| T500 | table | 1 | ok | pass | 10 | row 50: 3–8 (6) | 3 (first) | 422 / 197 | 130 / 66 | 0 | 57 | 55 / 55 |
| G200 | table | 1 | ok | pass | 17 | row 50: 3–5 (3); row 120: 9–14 (6) | 3 (first); 9 (first) | 432 / 205 | 130 / 67 | 5 | 73 | 84 / 84 |
| T150 | grid | 1 | ok | pass | 6 | row 50: 3–4 (2) | 3 (first) | 423 / 196 | 130 / 66 | 0 | 57 | 44 / 44 |
| T200 | grid | 1 | ok | pass | 6 | row 50: 3–5 (3) | 3 (first) | 422 / 197 | 130 / 65 | 0 | 58 | 45 / 45 |
| T300 | grid | 1 | ok | pass | 8 | row 50: 3–6 (4) | 3 (first) | 423 / 198 | 130 / 66 | 0 | 58 | 50 / 50 |
| T500 | grid | 1 | ok | pass | 10 | row 50: 3–8 (6) | 3 (first) | 426 / 199 | 130 / 66 | 0 | 55 | 66 / 66 |
| G200 | grid | 1 | ok | pass | 17 | row 50: 3–5 (3); row 120: 9–14 (6) | 3 (first); 9 (first) | 438 / 210 | 130 / 67 | 5 | 73 | 93 / 93 |
| T150u | table | 1 | ok | pass | 6 | row 50: 3–4 (2) | 3 (first) | 419 / 194 | 130 / 65 | 0 | 56 | 39 / 39 |
| T200u | table | 1 | ok | pass | 6 | row 50: 3–5 (3) | 3 (first) | 420 / 194 | 130 / 65 | 0 | 59 | 46 / 46 |
| T300u | table | 1 | ok | pass | 7 | row 50: 3–6 (4) | 3 (first) | 421 / 194 | 130 / 66 | 0 | 75 | 42 / 42 |
| T500u | table | 1 | ok | pass | 9 | row 50: 3–8 (6) | 3 (first) | 422 / 196 | 130 / 66 | 0 | 56 | 53 / 53 |
| T150u | grid | 1 | ok | pass | 6 | row 50: 3–4 (2) | 3 (first) | 424 / 197 | 130 / 65 | 0 | 58 | 55 / 55 |
| T200u | grid | 1 | ok | pass | 6 | row 50: 3–5 (3) | 3 (first) | 423 / 197 | 130 / 65 | 0 | 58 | 47 / 47 |
| T300u | grid | 1 | ok | pass | 7 | row 50: 3–6 (4) | 3 (first) | 423 / 198 | 130 / 65 | 0 | 61 | 51 / 51 |
| T500u | grid | 1 | ok | pass | 9 | row 50: 3–8 (6) | 3 (first) | 425 / 200 | 130 / 66 | 0 | 56 | 61 / 61 |
| NC-W on T300u | table | 1 | ok | fail text-dropped | 7 | row 50: 3–6 (4) | 3 (first) | 421 / 195 | 130 / 66 | 0 | 56 | 49 / 49 |
| NC-L on T300u | table | 1 | ok | fail text-repeated | 7 | row 50: 3–6 (4) | 3 (first) | 420 / 195 | 129 / 66 | 0 | 59 | 44 / 44 |
| NC-R on T300u | table | 1 | ok | fail row-lost | 7 | row 50: 3–6 (4) | 3 (first) | 421 / 195 | 130 / 66 | 0 | 65 | 54 / 54 |
| T300u-ab | table | 1 | ok | pass | 7 | row 50: 3–6 (4) | 3 (first) | 419 / 194 | 130 / 65 | 0 | 56 | 47 / 47 |
| X64K-ab | table | 1 | ok | pass | 17 | row 50: 3–15 (13) | 3 (first) | 427 / 200 | 131 / 66 | 5 | 62 | 88 / 88 |
| T300u-ab | grid | 1 | ok | pass | 9 | row 50: 3–8 (6) | 6 (other) | 424 / 197 | 130 / 66 | 0 | 57 | 51 / 51 |
| X64K-ab | grid | 1 | ok | pass | 28 | row 50: 3–26 (24) | 15 (other) | 432 / 205 | 131 / 67 | 5 | 65 | 98 / 98 |
| X64K | table | 3 | ok | pass | 17 | row 50: 3–15 (13) | 3 (first) | 427 / 202 | 135 / 67 | 5 | 69 | 75 / 80 |
| X128K | table | 3 | ok | pass | 29 | row 50: 3–27 (25) | 3 (first) | 435 / 208 | 131 / 70 | 9 | 73 | 115 / 138 |
| X256K | table | 3 | ok | pass | 54 | row 50: 3–52 (50) | 3 (first) | 449 / 224 | 132 / 74 | 20 | 76 | 204 / 244 |
| X512K | table | 3 | ok | pass | 104 | row 50: 3–102 (100) | 3 (first) | 481 / 256 | 135 / 83 | 35 | 93 | 349 / 454 |
| X1M | table | 3 | ok | pass | 203 | row 50: 3–201 (199) | 3 (first) | 546 / 318 | 153 / 100 | 74 | 134 | 682 / 841 |
| X64K | grid | 3 | ok | pass | 17 | row 50: 3–15 (13) | 3 (first) | 430 / 204 | 131 / 67 | 5 | 63 | 84 / 91 |
| X128K | grid | 3 | ok | pass | 29 | row 50: 3–27 (25) | 3 (first) | 439 / 211 | 131 / 70 | 9 | 74 | 125 / 126 |
| X256K | grid | 3 | ok | pass | 53 | row 50: 3–51 (49) | 3 (first) | 454 / 226 | 132 / 73 | 20 | 72 | 206 / 206 |
| X512K | grid | 3 | ok | pass | 102 | row 50: 3–100 (98) | 3 (first) | 485 / 258 | 134 / 82 | 35 | 96 | 371 / 382 |
| X1M | grid | 3 | ok | pass | 200 | row 50: 3–198 (196) | 3 (first) | 548 / 322 | 153 / 101 | 74 | 141 | 710 / 854 |
| F8K-b | table | 1 | ok | pass | 2 | — | — | 414 / 188 | 129 / 65 | 0 | 61 | 29 / 29 |
| F26K-b | table | 1 | ok | pass | 5 | — | — | 415 / 189 | 130 / 66 | 0 | 57 | 40 / 40 |
| F64K-b | table | 1 | ok | pass | 9 | — | — | 418 / 192 | 130 / 67 | 5 | 59 | 58 / 58 |
| F256K-b | table | 1 | ok | pass | 33 | — | — | 430 / 204 | 132 / 71 | 18 | 73 | 170 / 170 |
| F1M-b | table | 3 | ok | pass | 130 | — | — | 484 / 258 | 153 / 93 | 57 | 121 | 614 / 640 |
| F8K-a | table | 1 | ok | pass | 2 | — | — | 412 / 188 | 130 / 65 | 0 | 56 | 23 / 23 |
| F26K-a | table | 1 | ok | pass | 5 | — | — | 415 / 190 | 130 / 65 | 0 | 54 | 36 / 36 |
| F64K-a | table | 1 | ok | pass | 9 | — | — | 418 / 192 | 130 / 67 | 5 | 67 | 56 / 56 |
| F256K-a | table | 1 | ok | pass | 33 | — | — | 431 / 205 | 132 / 70 | 18 | 71 | 173 / 173 |
| F1M-a | table | 3 | ok | pass | 129 | — | — | 484 / 257 | 153 / 94 | 60 | 127 | 638 / 650 |
| F26K-ba | table | 1 | ok | pass | 5 | — | — | 416 / 189 | 130 / 65 | 0 | 63 | 35 / 35 |
| F26K-1p-b | table | 1 | ok | pass | 4 | — | — | 416 / 190 | 130 / 66 | 0 | 57 | 35 / 35 |
| F1M-1p-b | table | 1 | ok | pass | 124 | — | — | 512 / 287 | 152 / 98 | 71 | 150 | 613 / 613 |
| F26K-1p-a | table | 1 | ok | pass | 4 | — | — | 415 / 191 | 130 / 65 | 0 | 64 | 33 / 33 |
| F1M-1p-a | table | 1 | ok | pass | 124 | — | — | 513 / 287 | 152 / 99 | 74 | 125 | 603 / 603 |
| F26K-ba | grid | 1 | ok | pass | 5 | — | — | 416 / 189 | 129 / 66 | 0 | 54 | 39 / 39 |
| F1M-1p-b | grid | 1 | ok | pass | 124 | — | — | 512 / 288 | 152 / 97 | 71 | 124 | 586 / 586 |

### The ordinary block compared with the earlier experiment

| Figure | Earlier experiment, native table (`spikes/02-pdf-layout`) | Here, table (median of 3) | Here, grid (median of 3) |
|--------|------|------|------|
| Pages | 35 | 36 | 36 |
| Chromium tree peak RSS / PSS | 499 / 266 MiB | 486 / 260 MiB | 502 / 277 MiB |
| Driver peak RSS | — | 132 MiB | 132 MiB |
| Harness peak (`VmHWM`) / managed heap | accumulated over its whole matrix | 72 / 18 MiB | 73 / 18 MiB |
| `/tmp` peak | — | 78 KiB | 77 KiB |
| Navigation | — | 91 ms | 87 ms |
| PDF (layout and print) | 0.21 s | 0.27 s (max 0.33 s) | 0.30 s (max 0.38 s) |

The method differs from the earlier experiment:

- one fresh browser process per run here (the earlier experiment reused one
  browser);
- a 4 GiB memory cap;
- every block has a caption header row and a block-total row, and a one-line
  title (hence 36 pages against 35);
- the PDF base image was rebuilt since (`sha256:20a7c39a…` here,
  `sha256:bdc804d2…` there), with the same Chromium and Playwright versions.

### Free text

Every F fixture passed. This covers paragraphs before and after the detail,
split into 2 000-character paragraphs or kept as one paragraph, up to 1 MiB.
In table markup F1M-b took 130 pages, F1M-a 129, and F1M-1p-b and F1M-1p-a
124 each; in grid markup F1M-1p-b took 124. Every character printed and was
read back.

The single-paragraph 1 MiB runs used the most memory of the free-text
fixtures. Their Chromium tree peaks, RSS / PSS:

- F1M-1p-b, table: 512.84 / 287.38 MiB;
- F1M-1p-a, table: 513.75 / 287.68 MiB;
- F1M-1p-b, grid: 512.71 / 288.16 MiB.

The maxima are therefore **513.75 MiB RSS** and **288.2 MiB PSS**. The
"Every run" table rounds memory down to whole MiB, so it shows these runs as
512–513 / 287–288.

### Bottom-aligned amount

The amount printed exactly once in every run (TI-4), and every character of
the row printed (TI-3). Where the amount printed, and what the split row
looks like, depends on the markup.

The `-ab` fixtures have their own seed, so their text is not the text of
T300u or X64K; it has the same length (T300u-ab) or the same byte bracket
(X64K-ab). The like-for-like comparison is therefore the same `-ab` fixture in
the two markups, which hold the same text.

- **Table markup (`vertical-align: bottom`):** the amount printed on the
  row's **first** page (T300u-ab: page 3 of 3–6; X64K-ab: page 3 of 3–15).
  Every page of the row carries row text; there are no blank pages. The row
  spans 4 and 13 pages (7 and 17 pages in the document).
- **Grid markup (`align-self: end`):** the row's text starts on page 3. It is
  followed by whole **blank pages**, which carry only the page header (the
  fixture title and "Page i of N") and nothing in the content area:
  - T300u-ab: pages 4–5, which is 2 of the row's 6 pages;
  - X64K-ab: pages 4–14, which is 11 of the row's 24 pages.

  The text resumes on the next page, which is the amount's page (T300u-ab
  page 6, X64K-ab page 15). There the amount prints at the **top** right of
  its band, beside the first line of the resumed text and below an empty band
  (`results/png/X64K-ab-grid-r1-page015.png`). The row then continues to
  page 8 (T300u-ab) and page 26 (X64K-ab).
- **Why the grid row is longer:** the blank pages are the whole difference.
  Without them, the grid row has 4 and 13 pages with text, the same as the
  same `-ab` row in table markup. The grid documents have 9 and 28 pages
  against 7 and 17, which is 2 and 11 more: exactly the blank pages.

The blank pages are read from the checker's per-page count of content-area
words (`pageWordCounts` in `results/integrity.json`), which is 0 on exactly
those pages. The page header and "Page i of N" passed TI-8 on them as on every
other page.

### Page images and PDFs

- `results/png/`: the first, a middle and the last page of X1M's row 50 in
  both markups, T300u-ab table page 3 and X64K-ab grid page 15.
- `results/pdf/`: B1000 table, T300u table, T300u-ab grid and X64K-ab grid.

## Conclusion

These findings hold within the measured brackets, on this machine, with
Chromium 153.0.8010.12, Liberation Sans 9 pt and the two markups used here:

- **Every row and every character survived.** Native print layout kept them
  for every tall row measured, in both table and grid markup. That covers the
  earlier experiment's tall rows (8 054 to 30 047 characters), their
  unique-text twins, and records from 64 KiB to 1 MiB. Row 50 of X1M spanned
  199 pages in table markup and 196 in grid markup. Rows spanning three or
  more pages printed whole, every row after them was present and in order,
  and three repetitions of each record size gave identical text.
- **Long free text survived.** Text before and after the detail printed whole
  up to 1 MiB, in many paragraphs or in one.
- **Headers and page counters were correct.** The native margin boxes gave the
  right header and "Page X of Y" on every page of every run, up to 203 pages.
- **The cost stayed small.** It grew with size: for a 1 MiB record (203 pages)
  the Chromium tree peaked at about 546 MiB RSS and the PDF took under 0.9 s.
  No run came near the 300 s or 4 GiB caps.
- **A bottom-aligned amount never printed on the row's last page.** In table
  markup (`vertical-align: bottom`) it printed on the row's first page, and
  the row had no blank pages.
- **In grid markup (`align-self: end`), bottom alignment printed whole blank
  pages inside the split row.** Those pages carried only the page header and
  came before the amount's page: T300u-ab pages 4–5, and X64K-ab pages 4–14
  (11 of the row's 24 pages). The text resumed on the amount's page, with the
  amount at the top right of its band. The grid row's extra length (6 and 24
  pages against 4 and 13 for the same `-ab` text in table markup) is exactly
  those blank pages. No text was lost.

These results are not extended beyond these brackets, or to another Chromium
version, font or markup, and they decide nothing.

## Provenance

The dataset loading, grouping and tall-row derivation are adapted from
`spikes/02-pdf-layout/PdfLayoutSpike/BlockSource.cs`; the process-tree
sampler, the route-and-abort isolation and the results writer from
`ProcessTreeSampler.cs`, `LayoutRunner.cs` and `ResultsWriter.cs` of the same
folder. Code was copied and adapted here; the earlier experiment is unchanged.
Everything else is original.

## Packages and licences

| Component | Licence | Where |
|-----------|---------|-------|
| Microsoft.Playwright 1.63.0 | MIT | harness |
| Chromium headless shell 153.0.8010.12 | BSD-3-Clause (bundled third-party notices inventoried, not gated) | PDF base image |
| Fonts (Liberation Sans) | PDF base image content | PDF base image |
| poppler-utils 24.02.0-1ubuntu9.11 | GPL-2 or GPL-3 (per the package's copyright file in the image) | checker image only, never shipped |
| .NET 10 runtime and SDK images | MIT | both images |
