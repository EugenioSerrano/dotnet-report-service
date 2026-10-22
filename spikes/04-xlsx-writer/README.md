# 04 — Raw XLSX writer: bounded memory and exact per-column typing

A throwaway console program that writes the high-volume pilot dataset
(1 000 000 records, 30 % unique strings) to XLSX with two libraries —
**MiniExcel 1.46.0** and the **Open XML SDK 3.5.1 `OpenXmlWriter`** — and
measures memory, checks that every value is typed exactly as its column
declares, that formula-like text stays text, that the sheet row limit is
handled explicitly and that Excel opens the files without repair.

The Open XML SDK is driven three ways, because its package layer and its
writer behave differently:

| Name | What it is |
|------|------------|
| `miniexcel` | MiniExcel 1.46.0, default configuration (`OpenXmlConfiguration`, fast mode off), fed a lazy sequence of dictionaries; synchronous `MiniExcel.SaveAs(stream, ...)` |
| `openxml-default` | `SpreadsheetDocument.Create(path)` — the SDK's usual path — with every part written once by `OpenXmlWriter` |
| `openxml-writeonly` | the same writing code on a write-only `System.IO.Packaging.Package`, so the package layer could stream |
| `openxml-zip` | `OpenXmlWriter` over the entries of a BCL `ZipArchive`; the package parts (`[Content_Types].xml`, relationships) are written by this program |
| `read-only` | reads and types every record and writes nothing: the reader's own cost |

## Hypothesis

Both libraries can write 1 M rows with flat memory (no growth with the row
count), with exact per-column typing — numbers that a spreadsheet cannot hold
exactly fail with `OUTPUT_LIMIT_EXCEEDED` instead of being rounded, text
columns keep every character —, with formula-like values written as text, and
with the 1 048 576-row sheet limit handled by an explicit error or a sheet
split, never by truncation.

## The typing rule

The column profile (`profiles/*.json`) decides the cell type of each column:

| Column type | Value | Cell |
|-------------|-------|------|
| `integer` / `decimal` | at most 15 significant digits and exactly representable | numeric cell, invariant number text (scale kept, never an exponent, `-0` written as `0`) |
| `integer` / `decimal` | more than 15 significant digits, a value that underflows or overflows the decimal type, or more than 28 decimal places | the export fails with `OUTPUT_LIMIT_EXCEEDED` (exit code 3) and the partial file is deleted |
| `integer` | a fraction or exponent, or a non-number | type mismatch, exit code 1 |
| `string` | any JSON string or number | text cell with the exact source text (numbers keep their raw token: `9007199254740994`, `1234.567890123456`) |
| `string` | more than 32 767 characters | `OUTPUT_LIMIT_EXCEEDED` (never truncated) |
| any | `null` | no cell |

Significant digits are counted on the **raw JSON token**, never on a parsed
value, and the parsed decimal is compared back with the token, so a value can
never be rounded silently (`1e-30` and `0.00000000000000000000000000001` fail
instead of becoming `0`). No cell is ever written with a formula element.

The three profiles differ only in two columns:

| Profile | `quantity` | `reference` | Use |
|---------|------------|-------------|-----|
| `volume-strict.json` | `decimal` | `integer` | the types declared by the pilot template `samples/templates/pilot-volume/template.yaml` |
| `volume-strict-reference.json` | `string` | `integer` | shows the `reference` failure |
| `volume-exact-text.json` | `string` | `string` | the measurement profile: the only way to keep both columns exact |

In the volume dataset every `quantity` has 16 significant digits and every
`reference` is above 2^53 (16 digits), so with the template's declared types
**the very first record fails** with `OUTPUT_LIMIT_EXCEEDED`. That is the rule
working as intended; the template's declaration and the rule cannot both hold
for this dataset.

## How to run

All commands run from the repository root. `<T>` is a scratch folder outside
the repository (for example `<OS temp>/xlsx-writer-spike`); datasets and XLSX
outputs are never written inside the repository.

1. Build the dataset generator into a private artifacts folder and generate
   the volume datasets (default seed `20261021`):

   ```sh
   dotnet build samples/datasets/ReportService.Samples.DatasetGenerator -c Release --artifacts-path <T>/generator
   GEN=<T>/generator/bin/ReportService.Samples.DatasetGenerator/release/ReportService.Samples.DatasetGenerator
   for n in 100000 500000 1000000 1100000; do $GEN generate --dataset volume --count $n --output <T>/datasets; done
   for f in 100k 500k 1m 1100k; do $GEN check <T>/datasets/header-items-v1.volume.$f.json; done
   ```

   The `1m` hash must equal its line in `samples/datasets/SHA256SUMS`.

2. Build the spike (0 warnings, 0 errors expected):

   ```sh
   dotnet build spikes/04-xlsx-writer/XlsxWriterSpike -c Release
   X=spikes/04-xlsx-writer/XlsxWriterSpike/bin/Release/net10.0/XlsxWriterSpike
   ```

3. Typing on the real dataset, for each writing candidate `<c>`
   (`miniexcel`, `openxml-default`, `openxml-writeonly`, `openxml-zip`):

   ```sh
   $X write --candidate <c> --input <T>/datasets/header-items-v1.volume.1m.json --columns spikes/04-xlsx-writer/profiles/volume-strict.json --output <T>/out/strict.xlsx
   $X write --candidate <c> --input <T>/datasets/header-items-v1.volume.1m.json --columns spikes/04-xlsx-writer/profiles/volume-strict-reference.json --output <T>/out/strict.xlsx
   ```

   Expected: exit code 3, `OUTPUT_LIMIT_EXCEEDED` at record 1 (`quantity`,
   then `reference`), no output file.

4. Small-scale checks of the typing rule, formula-like text and the row limit
   (run it twice; the output must be identical):

   ```sh
   $X typing-check --work <T>/work
   ```

   It keeps the outputs of cases `C1`, `C2` and `T16`, each with a probe file,
   under `<T>/work/typing/kept/`.

5. Measurements (Windows): one child process per run, 3 repetitions, sizes
   100k, 500k and 1m, every candidate:

   ```sh
   $X suite --datasets <T>/datasets --results spikes/04-xlsx-writer/results --work <T>/work
   ```

   Then compare every kept 1m output with its source and write its probes:

   ```sh
   $X inspect --input <T>/work/out/<c>-1m.xlsx --source <T>/datasets/header-items-v1.volume.1m.json --columns spikes/04-xlsx-writer/profiles/volume-exact-text.json --probes <T>/work/out/<c>-1m.probes.json
   ```

6. Linux confirmation in the .NET runtime container (no port published, the
   program and the datasets mounted read-only; with a Docker engine running in
   WSL 2 the host folders are given in their `/mnt/c/...` form):

   ```sh
   dotnet publish spikes/04-xlsx-writer/XlsxWriterSpike -c Release -o <T>/publish
   docker run --rm --name xlsx-writer-spike-linux \
     --mount type=bind,source=<T>/publish,target=/app,readonly \
     --mount type=bind,source=<T>/datasets,target=/data,readonly \
     --mount type=bind,source=<T>/work-linux,target=/work \
     mcr.microsoft.com/dotnet/aspnet:10.0.13-noble \
     dotnet /app/XlsxWriterSpike.dll suite --datasets /data --results /work/results --work /work --sizes 1m --repetitions 1
   ```

   Copy `measurements-linux.csv` and `summary-linux.csv` from
   `<T>/work-linux/results/` into `results/`.

7. Row limit at the real limit, with the 1 100 000-record file and the
   measurement profile:

   ```sh
   P=spikes/04-xlsx-writer/profiles/volume-exact-text.json
   I=<T>/datasets/header-items-v1.volume.1100k.json
   $X write --candidate <c> --input $I --columns $P --output <T>/out/fit.xlsx --rows 1048575
   $X write --candidate <c> --input $I --columns $P --output <T>/out/over.xlsx --rows 1048576
   $X write --candidate <c> --input $I --columns $P --output <T>/out/full.xlsx
   $X write --candidate <c> --input $I --columns $P --output <T>/out/full.xlsx --ignore-header-count
   $X write --candidate <c> --input $I --columns $P --output <T>/out/split.xlsx --row-limit-policy split
   $X inspect --input <T>/out/fit.xlsx --source $I --columns $P --rows 1048575
   $X inspect --input <T>/out/split.xlsx --source $I --columns $P --probes <T>/out/split.probes.json
   ```

8. Validity and Excel (Windows with Excel installed; Windows PowerShell 5.1
   or PowerShell 7):

   ```sh
   $X validate --input <T>/work/typing/kept/<case>-<c>.xlsx
   powershell -File spikes/04-xlsx-writer/tools/open-in-excel.ps1 -Path <file>.xlsx -Probes <file>.probes.json -TimeoutSeconds 600
   ```

   `validate` loads the whole document, so it is used on the small files only.
   The Excel check opens the file read-only with alerts enabled and reads back
   the sheet names, the used row and column counts and every probe cell
   (`HasFormula` must be false, text equal, numbers equal); a `blocked` result
   means Excel did not finish in time — most likely a prompt — and the file
   must then be opened by hand.

Exit codes of the program: 0 success, 1 failure, 2 invalid arguments,
3 `OUTPUT_LIMIT_EXCEEDED`.

## Results

### Environment

- Reference environment: [`docs/reference-environment.md`](../../docs/reference-environment.md),
  page revision 1. Measured on 2026-10-21.
- Windows: Windows 11 Pro 10.0.26300, .NET 10.0.13, workstation garbage
  collector (concurrent), invariant globalization.
- Linux: `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble`
  (`sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac`)
  on the container runtime of the reference environment, same runtime and
  garbage collector mode, framework-dependent portable publish.
- Other experiments may have run on the machine at the same time, so
  throughput (rows/s) is indicative only; per-process memory figures are not
  affected by other processes.

### Datasets

Generated with the repository's dataset generator, seed `20261021`; each file
passed `check` (16-digit `quantity` and `reference` above 2^53 in every record,
20-digit `accountId` with leading zeros, formula-like descriptions present).

| File | Records | SHA-256 |
|------|---------|---------|
| `header-items-v1.volume.100k.json` | 100 000 | `c9f1b968f501917a0dbffb488a5767fdfe1497111a6c443fb0517ed8b59475d4` |
| `header-items-v1.volume.500k.json` | 500 000 | `3285fccd1b436dbe192802b5d9d532d120a4fb7c063e5c07b4b638274a3c7352` |
| `header-items-v1.volume.1m.json` | 1 000 000 | `6075bea4c0173633a1c5090f4a9b7047d3bdeb143139484a20f49f9f76986ace` (equals `samples/datasets/SHA256SUMS`) |
| `header-items-v1.volume.1100k.json` | 1 100 000 | `e97108ca83cb4fcf393a7e86a9ab18c56a31c12f424465358068e9c0085df716` |

### Memory and throughput (Windows, medians of 3 runs, profile `volume-exact-text`)

Every run is its own process. Chromium process-tree peak RSS is "n/a — no
browser" for every run. Temporary disk is the peak size of the run's own
temporary folder.

| Candidate | Rows | Managed heap peak (MiB) | Process peak RSS (MiB) | Temporary disk peak (MiB) | Output (MiB) | Rows/s | Allocated (MiB) | Gen2 GCs |
|-----------|------|-----------:|----------:|----:|------:|--------:|-------:|--:|
| `miniexcel` | 100 000 | 19.9 | 62.4 | 0 | 10.6 | 83 388 | 409 | 2 |
| `miniexcel` | 500 000 | 20.8 | 62.8 | 0 | 53.3 | 166 560 | 1 885 | 2 |
| `miniexcel` | 1 000 000 | 20.8 | 62.4 | 0 | 106.7 | 170 256 | 3 732 | 2 |
| `openxml-default` | 100 000 | 65.8 | 135.4 | 0 | 6.7 | 51 833 | 259 | 3 |
| `openxml-default` | 500 000 | 590.5 | 624.7 | 0 | 33.5 | 72 999 | 1 599 | 6 |
| `openxml-default` | 1 000 000 | 1 165.5 | 1 150.2 | 0 | 67.1 | 79 288 | 3 191 | 7 |
| `openxml-writeonly` | any | — | — | — | — | — | — | — |
| `openxml-zip` | 100 000 | 18.2 | 61.3 | 0 | 6.7 | 51 197 | 168 | 0 |
| `openxml-zip` | 500 000 | 18.5 | 61.4 | 0 | 33.5 | 76 338 | 827 | 0 |
| `openxml-zip` | 1 000 000 | 18.5 | 57.8 | 0 | 67.1 | 80 029 | 1 656 | 1 |
| `read-only` | 100 000 | 17.5 | 47.4 | 0 | 0 | 199 419 | 96 | 0 |
| `read-only` | 500 000 | 17.1 | 52.5 | 0 | 0 | 420 194 | 338 | 0 |
| `read-only` | 1 000 000 | 17.1 | 52.2 | 0 | 0 | 574 146 | 634 | 0 |

Every run read the source exactly once (`sourceEnumerations` 1).

### Verdicts

Classification used by this experiment (not a product target): **flat** when
both RSS(1m)/RSS(100k) and heap(1m)/heap(100k) are at most 1.5, otherwise
**grows**; a candidate with a failed run is **failed at** the first size that
failed.

| Candidate | RSS ratio | Heap ratio | Verdict |
|-----------|----------:|-----------:|---------|
| `miniexcel` | 1.000 | 1.048 | **flat** |
| `openxml-default` | 8.494 | 17.715 | **grows** (about 1.1 KiB per row; 1.15 GiB at 1 M rows) |
| `openxml-writeonly` | — | — | **failed at 100k** — the SDK rejects a write-only package before any row is read: `System.IO.IOException: Cannot retrieve parts of writeonly container` (same on Linux) |
| `openxml-zip` | 0.943 | 1.015 | **flat** |
| `read-only` | 1.100 | 0.975 | flat (baseline) |

The managed heap figure is `GC.GetTotalMemory(false)` sampled every 50 ms: it
includes garbage not yet collected and can miss a peak shorter than the
interval. It is therefore read together with the exact operating-system peak
RSS, the total allocated bytes and the gen2 collection count. For the two flat
candidates all four agree: RSS is constant, allocation grows linearly with the
rows (garbage, not retained memory) and gen2 collections stay at 0–2. For
`openxml-default` RSS and heap grow together with the row count, which is the
SDK's documented behaviour on .NET (its ZIP package cannot stream, so each
part is buffered in memory until the package is saved).

### Linux confirmation (1 M rows, one run per candidate)

| Candidate | Managed heap peak (MiB) | Process peak RSS (MiB) | Temporary disk peak (MiB) | Output (MiB) | Rows/s |
|-----------|-----------:|----------:|----:|------:|-------:|
| `miniexcel` | 20.7 | 73.0 | 0 | 106.7 | 71 460 |
| `openxml-default` | 1 169.8 | 1 159.0 | 0 | 67.1 | 29 795 |
| `openxml-writeonly` | failed (same exception) | — | — | — | — |
| `openxml-zip` | 18.4 | 72.2 | 0 | 67.1 | 45 016 |
| `read-only` | 17.8 | 61.1 | 0 | 0 | 191 900 |

The Linux figures confirm the Windows picture; the absolute RSS is about
10 MiB higher for every candidate, including the baseline.

### Exact typing (`typing-check`, 26 cases × 4 writing candidates)

| Case | What it checks | `miniexcel` | `openxml-default` | `openxml-zip` |
|------|----------------|:--:|:--:|:--:|
| T1 | `9876543210123.45` (15 digits) is a numeric cell with that value | pass | pass | pass |
| T2, T3 | `98765432101234.56`, `0.1234567890123456` (16 digits) fail: `OUTPUT_LIMIT_EXCEEDED` precision, record and column named, exit 3, no file | pass | pass | pass |
| T4 | `1000.000000000000` is the number 1000 exactly | pass | pass | pass |
| T5 | `-0.01` and `0` | pass | pass | pass |
| T6, T7 | integer `900719925474099` exact; `9007199254740993` (above 2^53) fails | pass | pass | pass |
| T8, T9, T10 | text columns keep `9007199254740993`, `1234.567890123456` and leading zeros exactly | pass | pass | pass |
| T11 | `=1+1`, `=SUM(A1:A9)`, `+1+1`, `-2+3`, `@SUM(1,1)`, `=HYPERLINK(...)` are text cells equal to the input; zero formula elements | pass | pass | pass |
| T12, T13 | 32 767 characters fit; 32 768 fail (`cell-length`, never truncated) | pass | pass | pass |
| T14 | 2 500 rows over a 1 000-row sheet, policy `error`: fails **before writing** | pass | pass | pass |
| T15 | same with the header count ignored: the streaming guard fails at record 1 001 and the partial file is deleted | pass | pass | pass |
| T16 | same with policy `split`: `items` 1 000, `items-2` 1 000, `items-3` 500, header row in each, records in order | pass | pass | pass |
| T17 | exactly 1 000 rows fit a 1 000-row sheet | pass | pass | pass |
| T18–T20 | `1e-30` (underflow), 29 decimal places, `1E+30` (beyond the decimal range) fail, never rounded | pass | pass | pass |
| T21 | a 33-digit integer that cannot be parsed fails with `OUTPUT_LIMIT_EXCEEDED` | pass | pass | pass |
| T22, T23 | `1.5E3` is 1500; `-0` is written as `0` | pass | pass | pass |
| T24 | a fraction in an integer column is a type mismatch (exit 1) | pass | pass | pass |
| C1, C2 | the passing cases combined (numeric and text profiles), compared cell by cell with the source | pass | pass | pass |

`openxml-writeonly` fails 25 of the 26 cases because no file can be created
(it passes T14, which fails before any writer is involved); `typing-check`
therefore exits 1, with every other candidate at 26/26. Two runs produced
identical output.

On the real 1 M-record dataset, every writing candidate stops at record 1
with `OUTPUT_LIMIT_EXCEEDED` precision — column `quantity`
(`4286.563028959435`) with `volume-strict.json`, column `reference`
(`9007199254740994`) with `volume-strict-reference.json` — exit 3, no file
(`openxml-writeonly` fails earlier with its package error).

### Full-size content checks

Every completed output was read back with a neutral inspector (BCL
`ZipArchive` + `XmlReader`, not the libraries under test) and compared cell by
cell with the source typed by the same rule:

| Output | Data rows | Mismatches | Formula elements | Header and sequence |
|--------|----------:|-----------:|-----------------:|---------------------|
| `miniexcel` 1m | 1 000 000 | 0 | 0 | ok |
| `openxml-default` 1m | 1 000 000 | 0 | 0 | ok |
| `openxml-zip` 1m | 1 000 000 | 0 | 0 | ok |

### Row limit at the real limit (1 100 000-record file)

| Run | `miniexcel` | `openxml-default` | `openxml-zip` |
|-----|-------------|-------------------|---------------|
| `--rows 1048575`, policy `error` | one sheet, 1 048 576 rows, 0 mismatches | same | same |
| `--rows 1048576`, policy `error` | `OUTPUT_LIMIT_EXCEEDED` rows before writing (0.01 s), no file | same | same |
| full file, policy `error` | `OUTPUT_LIMIT_EXCEEDED` rows before writing, no file | same | same |
| full file, `--ignore-header-count` | streaming guard at record 1 048 576, partial file deleted | same | same |
| full file, policy `split` | `items` 1 048 575 + `items-2` 51 425 data rows, 0 mismatches, sequence continuous | same | same |

Memory of the split runs (1 100 000 rows, two sheets):

| Candidate | Managed heap peak (MiB) | Process peak RSS (MiB) | Chromium tree | Temporary disk peak (MiB) | Output (MiB) | Seconds |
|-----------|------:|------:|-----|--:|------:|-----:|
| `miniexcel` | 20.8 | 63.1 | n/a | 0 | 117.4 | 7.8 |
| `openxml-default` | 1 159.6 | 1 183.5 | n/a | 0 | 73.8 | 17.8 |
| `openxml-zip` | 18.4 | 57.9 | n/a | 0 | 73.8 | 17.4 |

The split re-reads the source once per sheet (two enumerations); the memory
stays flat for the streaming candidates.

### Validity and Excel

`OpenXmlValidator` (SDK default file format) reports **0 errors** on the nine
small kept outputs (`C1`, `C2`, `T16` × the three candidates that produce a
file).

Excel (COM class `Excel.Application.16`) opened all 15 listed files read-only with
alerts enabled, without any repair prompt; sheet names, used rows and columns
and every probe matched (`HasFormula` false on every probed cell, including
one formula-like description per prefix):

| File | `miniexcel` | `openxml-default` | `openxml-zip` | Sheets (rows incl. header) | Probes |
|------|:--:|:--:|:--:|------|--:|
| `C1` (numeric profile cases) | pass | pass | pass | `items` 16 | 29 |
| `C2` (text profile cases) | pass | pass | pass | `items` 10 | 21 |
| `T16` (split at 1 000 rows) | pass | pass | pass | `items` 1 001, `items-2` 1 001, `items-3` 501 | 8 |
| 1m output | pass (29.8 s) | pass (28.8 s) | pass (28.3 s) | `items` 1 000 001 | 10 |
| 1.1 M split output | pass (31.5 s) | pass (31.4 s) | pass (34.0 s) | `items` 1 048 576, `items-2` 51 426 | 11 |

No open was blocked, so no file had to be checked by hand.

### Storage and style observations

| | `miniexcel` 1.46.0 (default) | `openxml-default` / `openxml-zip` |
|--|--|--|
| Text cells | `t="str"` with the text in `<v>` (a formula-result string type, without any formula element) | `t="inlineStr"` with `<is><t>` |
| Shared-strings part | present but empty (`count=0`, 162 bytes) — no table that grows with the values | absent |
| Numbers | `t="n"`, `<v>` = the decimal's invariant text | no `t`, `<v>` = the invariant text |
| Cell formats | 6 (bold header style and a data style are applied by default) | 1 (the default) |
| Extra parts and markup | a drawing part, a frozen header pane (`sheetViews`) | none |
| Options | 1.46.0 has **no string-storage option** (it exists only in the 2.0 preview line); typing follows the CLR type of each value (`decimal` → number, `string` → text) | full control of the markup |
| Output size at 1 M rows | 106.7 MiB | 67.1 MiB |

MiniExcel never turned a numeric-looking text into a number and never
altered a decimal's digits, so its dynamic-column option was not needed.

## Conclusion

Evidence for the writer decision (a recommendation, not the decision):

- **The Open XML SDK's package layer cannot be used for large outputs.** With
  `SpreadsheetDocument.Create` the process grows by about 1.1 KiB per row
  (1.15 GiB at 1 M rows, 1.18 GiB at 1.1 M), on Windows and on Linux. Opening
  a write-only package so that it could stream is rejected by the SDK.
- **Both MiniExcel 1.46.0 and `OpenXmlWriter` over a plain ZIP archive write
  1 M and 1.1 M rows in flat memory** — about 60 MiB RSS and 20 MiB managed
  heap, the same as the reader alone plus a few MiB, no temporary files —
  with exact typing, no formula elements and correct row-limit handling.
- MiniExcel is about **twice as fast** (170 000 vs 80 000 rows/s) and needs no
  packaging code, but its default output is **60 % larger**, carries styling
  that was not asked for (bold header, frozen pane, six cell formats, a drawing
  part) and stores text as `t="str"` cells; 1.46.0 offers no option to change
  the string storage.
- `OpenXmlWriter` over a ZIP archive gives **full control of the markup**
  (inline strings, one default cell format, nothing implicit) at the cost of
  writing the package parts — content types and the relationship parts,
  about 110 lines in this spike (the worksheet markup itself is shared with
  the SDK variants).
- Recommendation: prefer **`OpenXmlWriter` over a BCL `ZipArchive`** when the
  exact markup and the smallest output matter (typing, no implicit styling,
  predictable cell types for consumers); MiniExcel is the simpler choice if
  throughput and less own code weigh more and its default styling is
  acceptable. The per-column typing rule must be applied before the writer in
  either case, and the pilot template's numeric declarations of `quantity` and
  `reference` cannot be written from this dataset without failing.

## Packages and licences

From `dotnet list package --include-transitive` (no package reported as
vulnerable):

| Package | Version | Kind | Licence |
|---------|---------|------|---------|
| `MiniExcel` | 1.46.0 | direct | Apache-2.0 |
| `DocumentFormat.OpenXml` | 3.5.1 | direct | MIT |
| `DocumentFormat.OpenXml.Framework` | 3.5.1 | transitive | MIT |
| `System.IO.Packaging` | 10.0.2 | transitive | MIT |

Versions are pinned exactly in this folder's `Directory.Packages.props`,
which takes the place of the repository-wide file for the spike only.

## Limits of this experiment

- No number formats, column widths, bold headers, frozen panes or filters were
  asked of the writers (MiniExcel adds some by default, see above); no `date`
  columns (the volume dataset has none); no metadata sheet.
- No pre-pass for columns that appear late or are not declared; the profile
  must name every record property, and a mismatch is an error.
- The 16 384-column limit was not exercised (the dataset has 7 columns).
- MiniExcel fast mode, the MiniExcel 2.0 preview line and other libraries
  were not measured.
- Throughput is indicative (other experiments may have run concurrently);
  the 1.5 ratio used for "flat" is a classification of this experiment, not a
  product target.
- Generated datasets and XLSX outputs are not kept in the repository; they
  are reproduced with the commands above.
