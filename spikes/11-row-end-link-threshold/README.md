# Row-end link of a split row under native Chromium layout

**Status:** measured. One round of 492 runs, every result read back by tools
independent of the browser. No decision is taken here.

## Question

The earlier two-pass spike (`spikes/09-two-pass-page-subtotals/`) marks every
item row with invisible link annotations: a start link around the sequence
number, an **end link around the last word of the description**, and an amount
link. With native Chromium print layout, it found the following.

- **Table markup:** a split row spanning 13 pages kept its end link, and rows
  spanning 25 pages or more had none. The span in between was not measured.
- **Grid markup:** long rows kept the link, except in the layout with a
  bottom-aligned amount.

This experiment measures:

1. at which row span the end link disappears in table markup, to one page —
   or whether the loss is not monotonic, or not reproducible;
2. whether the result is stable across repetitions;
3. whether it depends on the span alone;
4. whether grid markup has a limit in the same range;
5. for every run with an end link, on which page and at which position it lies.

**Hypothesis going in:** above some span the end link is lost, whatever the
row holds. **The measurements refute it for table markup** (see Results).

## What was measured, and what was not

- **The block:** the shared row fixture of `spikes/07-native-tall-rows/` — the
  first 100 records of the grouped dataset, with group rows as they fall. Only
  record 50's description is replaced, so the tall row sits inside an ordinary
  block.
- **Sizes:** record 50, serialised as a compact UTF-8 JSON item, from 64 KiB to
  144 KiB in 2 KiB steps (41 sizes). It is sized by the shared series' own
  rule: within [*S* − 64, *S*] bytes.
- **Two row contents:**
  - `uniq` — the shared `X` series' text: seeded unique tokens. `X64K` and
    `X128K` are the shared files themselves, reproduced byte for byte.
  - `rep` — record 50's own description, repeated and cut to the same byte
    window.
- **Markups:**
  - **table:** header row repeated by native print, cells `vertical-align: top`.
  - **grid:** each row `display: grid` with the **amount top-aligned**. The
    bottom-aligned-amount layout is **not measured**: it is already known to
    print header-only pages and to lack the end link.
- **Amount strategy:** `cell` — the amount stays in its cell, with its amount
  link. Nothing is stamped and no subtotal is computed.
- **Repetitions:** three per fixture and markup, spread in time (repetition 1
  of everything, then 2, then 3). 82 fixtures × 2 markups × 3 = **492 runs**.
- **Not measured:** other Chromium versions or fonts, other block shapes,
  rows other than row 50, Paged.js, page subtotals. No workaround and no other
  marker are tried or proposed.

## Environment (the result holds for this version and platform only)

- Chromium **153.0.8010.12**: the Playwright 1.63.0 headless shell, build
  `chromium_headless_shell-1243`, inside the PDF base image
  `sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d`
  (Ubuntu 24.04.5, .NET 10.0.13). The text uses Liberation Sans.
- linux/amd64 under WSL 2, kernel 6.18.33.2-microsoft-standard-WSL2.
  Container engine 29.4.0-ce (openSUSE Leap 16.0, 16 CPUs, cgroup v2), on
  Windows 11 Pro build 26300.
- Every run printed with Chromium 153.0.8010.12; no other version appeared.
  Details: `results/environment.json`.

## How it works

1. **`fixtures`** (on the host) builds the sweep with the shared fixture
   library's public API. Before writing anything, the same builder must
   reproduce the shared `B100`, `X64K`, `X128K`, `T150`, `T200`, `T300` and
   `T500` files byte for byte against `spikes/07-native-tall-rows/results/fixtures.sha256`.
2. **`drive`** runs inside the earlier experiment's harness image,
   **`two-pass-spike:local`, unchanged**
   (`sha256:0237b3c5f187aeded03caffe509e9d035f9eb9dfd18a65204e61b128b610c808`).
   - It is a framework-dependent build of this folder, mounted read-only and
     started on the image's own .NET runtime as the image's non-root user.
   - Per run it starts the image's own `layout-one` (native print with the
     link markers), then `stamp-one --read-only` (the harness's own reading of
     the links; nothing is stamped or saved). Each is a fresh process, with
     exactly the command line of the earlier experiment's driver.
   - `argv.jsonl` logs every command line, and `report` checks it.
   - Caps, out-of-memory detection and leftover-process cleanup are copied from
     the earlier driver.
   - The run records use the earlier formats, so the two readers below read
     them unchanged.
3. **Reading, independent of the browser** — two images that hold no browser:
   - the shared text check (`text-integrity-check:local`, unchanged) gives
     each PDF's pages and the row's **printed span** from poppler's words;
   - the earlier accounting checker's split-row probe
     (`page-accounting-check:local`, unchanged) gives every link annotation
     with its page and rectangle from **qpdf**, and the end-rectangle fields
     against poppler's word boxes.
4. **`report`** (on the host) joins the records with both readings. It then
   applies, all fixed before the runs:
   - the instrument checks;
   - the reproduction checks against the earlier PDFs and probe;
   - the repeat agreement and the coverage rule;
   - one classification per markup and content;
   - a span-alone verdict per markup.

### Commands (product repository root, PowerShell; Rancher Desktop uses the `/mnt/<drive>/...` path)

```powershell
$wslRoot = "/mnt/" + $PWD.Drive.Name.ToLowerInvariant() + ($PWD.Path.Substring(2) -replace '\\', '/')   # Docker Desktop: ${PWD}
$s = "spikes/11-row-end-link-threshold"
dotnet build "$s/RowEndSweep" -c Release
dotnet "$s/RowEndSweep/bin/Release/net10.0/RowEndSweep.dll" fixtures --dataset samples/datasets/header-items-v1.grouped.10k.json `
  --sums samples/datasets/SHA256SUMS --reference-sums spikes/07-native-tall-rows/results/fixtures.sha256 --out "$s/results/raw/fixtures"
dotnet publish "$s/RowEndSweep/RowEndSweep.csproj" -c Release -o "$s/out/sweep"
# The measured round: one container, nothing else running on the machine
$name = "row-end-sweep-$(Get-Date -Format yyMMddHHmmss)-round1"
docker run --name $name --init --shm-size=1g --memory 4g --memory-swap 4g `
  -v "${wslRoot}/$s/results/raw/fixtures:/fixtures:ro" -v "${wslRoot}/$s/out/sweep:/sweep:ro" `
  --entrypoint dotnet two-pass-spike:local /sweep/RowEndSweep.dll drive --fixtures /fixtures --out /out --round 1
docker cp "${name}:/out" "$s/results/raw/round-1/runs"; docker rm $name
# The two readings (Git Bash or any POSIX shell)
sh spikes/11-row-end-link-threshold/read-round.sh <repository root as the engine sees it> round-1
dotnet "$s/RowEndSweep/bin/Release/net10.0/RowEndSweep.dll" report --rounds "$s/results/raw/round-1" --fixtures "$s/results/raw/fixtures" `
  --reference "$s/results/raw/reference" --reference-split-rows spikes/09-two-pass-page-subtotals/results/probe-split-rows-4.json --out "$s/results"
sh spikes/11-row-end-link-threshold/check-identity.sh   # every line: two equal hashes
```

`results/raw/reference/` holds the earlier experiment's own `X64K` and
`X128K` table PDFs. They are copied from its ignored run folder, each matching
that folder's recorded hash list, and are read by the same two images.

## Results

### The instrument

- 492 of 492 runs printed (`ok`). There was no cap, no out-of-memory kill, no
  dialog, no aborted request and no script.
- Pass-1 wall time: median 1.1 s, maximum 3.4 s. Chromium tree peak at most
  443 MiB.
- The text check passed 492 of 492. No text was lost or repeated, so every
  printed span can be trusted.
- There was no unknown annotation. Every row other than 50 had its start, end
  and amount links. Row 50 always had exactly one start and one amount link.
- The harness's own reading agreed with qpdf on the end link's presence and
  page in **all 492 runs**.
- Every command line equals the earlier driver's.
- **All 164 fixture–markup groups gave identical results in their three
  repetitions** (pages, span, presence, the end rectangle's page and
  coordinates, the start and amount pages, the annotation count). The result
  is deterministic.
- **Coverage** is met in every markup and content: spans from 13 to 28 or 29
  pages, consecutive sizes never more than one page apart.

### The earlier observations, reproduced

| Check | Fixture | Result |
|-------|---------|--------|
| table, 13 pages | `X64K` | **reproduced**: 17 pages, row 50 on pages 3–15. End link on page 15 at `[201 301.16998 232.5 311.66998]`, identical to the earlier PDF; 300 annotations; the end-rectangle fields equal to the earlier probe's |
| table, 25 pages | `X128K` | **reproduced**: 29 pages, row 50 on pages 3–27, **no end link**. Row 50's start and amount links identical to the earlier PDF's; 299 annotations |
| grid, 13 pages | `X64K` | **reproduced**: row 50 on pages 3–15, end link on page 15, the end-rectangle fields equal to the earlier probe's |
| grid, 25 pages | `X128K` | new: end link present on page 27 |

### Table markup — no span threshold

`+` = end link present, `-` = absent, in all three repetitions. Each entry is
*size in KiB: span in pages*.

- **`uniq`:** 64:13+ 66:13- 68:14+ 70:14+ 72:14- 74:15+ 76:15- 78:16+ 80:16+
  82:16- 84:17+ 86:17+ 88:18+ 90:18+ 92:18- 94:19+ 96:19+ 98:19- 100:20+
  102:20- 104:21+ 106:21+ 108:21- 110:22+ 112:22- 114:23+ 116:23+ 118:23-
  120:24+ 122:24- 124:25+ 126:25+ 128:25- 130:26+ 132:26- 134:26- 136:27+
  138:27- 140:28+ 142:28- 144:28-
  — 24 sizes present, 17 absent.
- **`rep`:** 64:13+ 66:14+ 68:14+ 70:14- 72:15+ 74:15+ 76:15- 78:16+ 80:16-
  82:17+ 84:17+ 86:17- 88:18+ 90:18- 92:19+ 94:19+ 96:19- 98:20+ 100:20-
  102:21+ 104:21+ 106:21- 108:22+ 110:22- 112:23+ 114:23+ 116:23- 118:24+
  120:24- 122:25+ 124:25+ 126:25- 128:26+ 130:26- 132:27+ 134:27+ 136:27-
  138:28+ 140:28- 142:29+ 144:29-
  — 25 sizes present, 16 absent.

- **Class, both contents: `mixed`.** At almost every span from 13 to 29
  pages, one row length keeps the end link and another loses it. There is no
  span below which the link is always written and above which it never is: **the
  limit cannot be bracketed, because it does not exist as a span limit.**
- Where one span holds both outcomes, the **shorter row lengths keep the link
  and the longer ones lose it** — never the reverse in these runs. At two spans
  every measured length kept it (`uniq` 17 pages, `rep` 13 pages). Two
  examples: `U066` spans 13 pages and has no end link, while `X64K` spans 13
  and has one; `R124` spans 25 pages and keeps it.
- **Span-alone verdict (table): not span alone.** The two contents differ at
  spans 13 and 17, and both contents are mixed within a span. The earlier "13
  pages present, 25 absent" was a property of those two row lengths, not of the
  span.
- `results/pdf/X64K-table-c-r1.p1.pdf` (13 pages, end link) and
  `results/pdf/U066-table-c-r1.p1.pdf` (13 pages, no end link) show the two
  outcomes at the same span.

### Grid markup (amount top-aligned) — no limit in range

- Both contents: the end link is **present in every run**, spans 13 to 28
  (`uniq`) and 13 to 29 (`rep`). **Class: no limit in range.**
- **Span-alone verdict (grid): consistent with span alone** for these two
  texts. Nothing in the measured range depended on the span or on the content.

### Where the end link lies (Q5) — an observation, not a rule

- Every end link found (49 table runs, 82 grid runs in repetition 1) lies on
  the **row's last page**, the page where poppler prints the last word.
- **Grid:** the rectangle's centre is 0.12 pt from the word's centre in every
  run.
- **Table:** the rectangle lies **below** its word by about 15 pt for every page
  the row has already crossed. Measured: 14.995 to 15.050 pt per page;
  180.12 pt at the 13th fragment, 420.87 pt at the 29th.
- This repeats, on more rows, what the earlier experiment saw. It is tabulated
  in `results/summary.json` (`offsetObservation`) and is not used to classify
  anything, to predict a loss, or to correct a position.

## Conclusions (bounded to what was measured)

1. In **table markup**, under Chromium 153.0.8010.12 on this platform:
   - native print **does not have a row-span threshold** for the end link of a
     split row. Between 13 and 29 pages the link is kept by some row lengths
     and lost by others at the same span;
   - where a span holds both outcomes, the loss falls on its longer row
     lengths;
   - the outcome is deterministic: the same row gives the same outcome every
     time.
2. In **grid markup with a top-aligned amount**, the end link was written on
   every row from 13 to 29 pages, in both contents.
3. The earlier two observations are reproduced exactly, with the same files,
   the same row context and the same image. Read alone, they suggested a
   threshold that the sweep shows does not exist.
4. What makes Chromium omit the link in table markup is **not established**
   here. The 15 pt-per-page offset of the rectangles that are written is
   recorded as an observation only.

## Limits

- One Chromium build (153.0.8010.12), one font (Liberation Sans), one
  platform.
- One block shape: row 50 of an ordinary 100-record block, four-column
  geometry.
- Two contents.
- Sizes 64 to 144 KiB only (spans 13 to 29 pages).
- Strategy `cell` only; the bottom-aligned grid layout is excluded.

## Files

| Path | What |
|------|------|
| `RowEndSweep/` | The program: `SweepFixtures.cs` (sweep and self-check), `SweepPlan.cs` (the plan), `SweepDriver.cs` (the driver run inside the harness image, adapted from `spikes/09-two-pass-page-subtotals/TwoPassSpike/MatrixDriver.cs`), `SweepReport.cs` (checks and classification), `Reused/RecordTypes.cs` (run-record declarations copied byte for byte from that folder's `TwoPassSpike/LayoutRun.cs`, lines 12–173), and `ProcessTreeSampler.cs` from that folder, linked unchanged |
| `check-identity.sh` | Expected and actual SHA-256 of every reused, copied or identified file |
| `read-round.sh` | The two readings of a round, one named container at a time |
| `results/identity.txt` | Identity at the start (38 lines, all equal); unchanged at the end |
| `results/environment.json` | Versions, image IDs, engine and host |
| `results/fixtures.sha256` | The sums of the 82 sweep fixtures |
| `results/summary.json` | Instrument checks, reproduction, repeat agreement, coverage, classes, span-alone verdicts, the offset observation |
| `results/runs.csv` | One row per run: size, content, markup, repetition, times, pages, span, end link and rectangles, reading, text check |
| `results/pdf/` | `X64K` and `U066` table PDFs (13 pages each; end link present / absent) |
| `results/raw/` | Not committed: fixtures, the run folders with every PDF and record, both readings, the reference folder, the container list |
