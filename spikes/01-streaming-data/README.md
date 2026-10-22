# 01 — Streaming envelope reader and per-record rendering (measurement)

A throwaway measurement program. It reads the `header-items-v1` envelope
incrementally, plain and gzip, and renders one record at a time through
three template engines: Scriban, Handlebars (Handlebars.Net) and Liquid
(Fluid). It measures memory, the behaviour at every data limit, the byte
identity of the three engines' HTML and the cost of each engine. The code is
disposable; the numbers below are what this folder is for.

Contents:

- `StreamingDataSpike/` — the console program (reader, validator, render
  harness, meter, probes, measurement runner, self-check);
- `templates/<template>/html/` — the Handlebars (`.html.hbs`) and Liquid
  (`.html.liquid`) variants of the three sample templates (the Scriban parts
  are read from [`../../samples/templates/`](../../samples/templates/));
- `results/` — the raw measurement lines (`*.jsonl`), the summary tables
  written by `bench` (`summary.md`), the escaping table (`escaping.md`) and
  the limits table (`limits.md`);
- `Directory.Packages.props` — the exact versions of the three engines, for
  this folder only.

## Hypothesis

1. **The envelope can be read incrementally**, plain and gzip, with memory
   that does not grow with the number of records; the header, record, token,
   depth and input-size limits fail with stable, named errors instead of
   exhausting memory.
2. **One record at a time can be rendered through Scriban, Handlebars and
   Liquid** with one common render context and one escaping policy, so that
   the same pilot template gives byte-identical HTML in the three engines,
   hostile values included; each engine's cost and needs (parse once, render
   per record, limits, native escaping) are measured.

**Flatness rule T** — fixed before measuring, applied to the parse runs of the
volume dataset, both formats, both figures (peak managed heap and peak process
resident memory), medians of 3 runs:

```text
median(500k) <= 1.20 × median(100k) + 8 MiB   and   median(1M) <= 1.20 × median(100k) + 8 MiB
```

The same rule is evaluated for the render runs (1 M against 100 k), for
information.

The starting limits are hypotheses: header 1 MiB, record 1 MiB, token 1 MiB,
depth 64, blocks of up to 1 000 records or 8 MiB.

## How to run

From a clean clone, at the repository root, with the .NET SDK of
`global.json` and the container runtime of the
[reference environment](../../docs/reference-environment.md). `RUN` is any
empty folder outside the repository (about 450 MiB is used); examples are for
a POSIX shell.

```bash
RUN=/tmp/streaming-data-spike/run1

# 1. Build the dataset generator outside its own folder, and check it
dotnet build samples/datasets/ReportService.Samples.DatasetGenerator -c Release --artifacts-path "$RUN/generator-build"
GEN="$RUN/generator-build/bin/ReportService.Samples.DatasetGenerator/release/ReportService.Samples.DatasetGenerator.dll"
dotnet "$GEN" verify

# 2. Generate the five inputs (default seed 20261021) and check each one
dotnet "$GEN" generate --dataset simple --output "$RUN/datasets"
dotnet "$GEN" generate --dataset grouped --output "$RUN/datasets"
dotnet "$GEN" generate --dataset volume --count 100000 --output "$RUN/datasets"
dotnet "$GEN" generate --dataset volume --count 500000 --output "$RUN/datasets"
dotnet "$GEN" generate --dataset volume --output "$RUN/datasets"
for f in "$RUN"/datasets/header-items-v1.*.json; do dotnet "$GEN" check "$f"; done

# 3. Build this program and gzip the inputs
dotnet build spikes/01-streaming-data/StreamingDataSpike -c Release
SPIKE=spikes/01-streaming-data/StreamingDataSpike/bin/Release/net10.0/StreamingDataSpike.dll
dotnet $SPIKE prepare --datasets "$RUN/datasets"

# 4. The repeatable check (writes the hostile inputs into $RUN/hostile first)
dotnet $SPIKE selfcheck --datasets "$RUN/datasets" --output spikes/01-streaming-data/results

# 5. Measurements: parse 100k/500k/1M plain and gzip, render 100k/1M per engine
dotnet $SPIKE bench --datasets "$RUN/datasets" --output spikes/01-streaming-data/results --runs 3
dotnet $SPIKE compare "$RUN/datasets/header-items-v1.volume.1m.json" --template pilot-volume >> spikes/01-streaming-data/results/compare.jsonl
dotnet $SPIKE escape-probe > spikes/01-streaming-data/results/escaping.md
dotnet $SPIKE limits-probe > spikes/01-streaming-data/results/limits.md

# 6. Linux confirmation: 1M gzip inside a container with a memory cap and no swap
dotnet publish spikes/01-streaming-data/StreamingDataSpike -c Release -o "$RUN/publish"
for CAP in 256m 128m; do
  docker run --rm --name "streaming-data-spike-run1-$CAP" --network none --memory "$CAP" --memory-swap "$CAP" \
    -v "$RUN/publish:/app:ro" -v "$RUN/datasets:/data:ro" \
    mcr.microsoft.com/dotnet/aspnet:10.0.13-noble dotnet /app/StreamingDataSpike.dll parse /data/header-items-v1.volume.1m.json.gz
done
```

Single commands, for exploring:

```bash
dotnet $SPIKE hostile --output "$RUN/hostile" --from "$RUN/datasets/header-items-v1.volume.100k.json"
dotnet $SPIKE parse FILE [--header-limit B] [--record-limit B] [--token-limit B] [--depth-limit N] [--input-limit B] [--initial-buffer B] [--block-records N] [--block-bytes B]
dotnet $SPIKE render FILE --template pilot-volume --engine scriban|handlebars|liquid [--context per-record|per-block]
dotnet $SPIKE compare FILE --template pilot-grouped [--culture es-AR] [--parallel 4]
```

On Windows with Rancher Desktop (WSL 2), the container mounts take the
virtual machine's form of the host folder, `/mnt/<drive>/<path>`; a
`C:\…` or `C:/…` mount is rejected by the engine.

Exit codes: 0 success, 1 failure, 2 invalid arguments, 3 data error with one
line on standard error, `error code=<CODE> limit=<name>|reason=<name> ordinal=<n> offset=<bytes>`.
Every command prints one JSON object per result on standard output, with the
input's file name only.

## Results

### Environment

- Machine and software: the [reference environment](../../docs/reference-environment.md),
  **page revision 2 (2026-10-21)**. Measured on **2026-10-21**.
- Host runs: Windows 11 Pro (build 26300), .NET SDK 10.0.402, runtime
  .NET 10.0.13, workstation garbage collector with background (concurrent)
  collection, one child process per measured run, one discarded warm-up run per
  input.
- Linux run: `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble`
  (`sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac`),
  same published program.
- Noise: other experiments and interactive sessions were running on the same
  machine (about 20 % of the processor busy before the measurements, about
  three of the sixteen threads), and two containers of other work were
  running.
  The spread between the three runs of each measurement is small (see the raw
  lines), so the medians are representative of this load.

### Common figures

Every result line carries the common figures: `managedHeapPeakBytes` (peak of
`GC.GetTotalMemory(false)`, sampled every 1 000 records and at the end),
`processPeakRssBytes` (peak working set on Windows; on Linux the kernel's
`VmHWM`, which is used whenever it differs from the runtime's value),
`chromiumTreePeakRssBytes` (always n/a: no browser runs here) and
`tempDiskBytes` (always 0: the measured commands write nothing, HTML goes to
hashing sinks). Temporary disk used by the inputs themselves: 364 MiB of
datasets and their gzip copies, 35 MiB of hostile inputs, 2 MiB for the
published program.

### Datasets

| File | Records | Bytes plain | Bytes gzip | SHA-256 plain | SHA-256 gzip |
|---|---|---|---|---|---|
| `header-items-v1.simple.25.json` | 25 | 4 198 | 1 022 | `9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3` | `31f71d1357be446954e21280053b61344cb18ec57138ff97b7196b6553c76e05` |
| `header-items-v1.grouped.10k.json` | 10 000 | 2 877 773 | 398 078 | `d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753` | `161d79959e291952c55b73d2a0498e594d4bf6dd74fd292caf9262a0ed43695f` |
| `header-items-v1.volume.100k.json` | 100 000 | 19 382 897 | 4 206 519 | `c9f1b968f501917a0dbffb488a5767fdfe1497111a6c443fb0517ed8b59475d4` | `96c94567e7696fa67daff2f447286125e05b698307d2cc38b53a2c95957fe0cb` |
| `header-items-v1.volume.500k.json` | 500 000 | 97 346 328 | 21 034 946 | `3285fccd1b436dbe192802b5d9d532d120a4fb7c063e5c07b4b638274a3c7352` | `08868f20be5258d78315331cbdadf1ad338a4739e68b2621475707434e9a935e` |
| `header-items-v1.volume.1m.json` | 1 000 000 | 194 805 664 | 42 073 699 | `6075bea4c0173633a1c5090f4a9b7047d3bdeb143139484a20f49f9f76986ace` | `a55a5ddce5f4be54bcb13c14860a3be973b6dba44c9f1b8913484542ba019f4c` |

The three default files equal [`SHA256SUMS`](../../samples/datasets/SHA256SUMS);
the 100 000 and 500 000 record files equal the hashes published in
[`samples/datasets/README.md`](../../samples/datasets/README.md). The gzip
copies are written by `prepare` (optimal compression); their hashes depend on
the compression library and are given for traceability only. Every plain file
passes the generator's `check`, and every file, plain and gzip, parses `ok`
with record count, exact amount total and group count equal to the header.

### Parse — memory stays flat

| Size | Format | Managed heap peak MiB (median / max) | Process peak RSS MiB (median / max) | Chromium | Temp disk | Allocated MiB | Seconds | Records/s | Max buffer | Max record |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 k | plain | 18.6 / 18.6 | 46.7 / 46.7 | n/a | 0 | 106.7 | 0.56 | 177 872 | 4 096 | 225 |
| 100 k | gzip | 18.6 / 18.7 | 47.8 / 48.4 | n/a | 0 | 106.6 | 0.64 | 157 410 | 4 096 | 225 |
| 500 k | plain | 19.5 / 19.6 | 57.5 / 57.5 | n/a | 0 | 530.3 | 1.47 | 339 386 | 4 096 | 229 |
| 500 k | gzip | 19.6 / 19.6 | 58.0 / 58.1 | n/a | 0 | 530.1 | 1.54 | 324 968 | 4 096 | 229 |
| 1 M | plain | 19.5 / 19.6 | 57.5 / 57.5 | n/a | 0 | 1 059.9 | 2.55 | 391 676 | 4 096 | 229 |
| 1 M | gzip | 19.7 / 19.7 | 58.3 / 58.9 | n/a | 0 | 1 059.4 | 2.55 | 391 519 | 4 096 | 229 |

Rule T: **pass** for both figures in both formats — managed heap 18.6 → 19.7 MiB
against a bound of 30.3 MiB; process RSS 46.7 → 57.5 MiB (plain) and
47.8 → 58.3 MiB (gzip) against bounds of 64.0 and 65.4 MiB. The read buffer
never grew past its 4 KiB start on the pilot data (largest record 229 bytes;
2 742 bytes in the grouped set, still inside 4 KiB). Raw lines:
[`results/parse-runs.jsonl`](results/parse-runs.jsonl); full tables:
[`results/summary.md`](results/summary.md).

Resident memory rises by about 11 MiB between 100 k and 500 k and then stays
put up to 1 M; the managed heap does not move. The step comes from the runtime
(more garbage collections and more code tiered up), not from retained records:
allocations are proportional to the records (about 1.1 KiB per record, almost
all short-lived), gen 2 collections stay at 0, and the heap peak is flat.

**Linux, 1 M gzip under a memory cap** ([`results/linux-run.jsonl`](results/linux-run.jsonl)):

| Cap (memory = memory + swap) | Exit | Records | Managed heap peak MiB | Process peak RSS MiB | Memory the runtime could use MiB | Seconds |
|---|---|---|---|---|---|---|
| 256 MiB | 0 (`ok`) | 1 000 000 | 18.4 | 67.1 (`VmHWM`; the runtime reported 63.7) | 192 | 3.53 |
| 128 MiB | 0 (`ok`) | 1 000 000 | 11.3 | 59.8 (`VmHWM`; the runtime reported 56.3) | 96 | 3.24 |

The uncompressed 1 M file is 194 805 664 bytes (185.8 MiB): the 256 MiB cap is
**above** it, so that run alone does not show the document is never held. The
128 MiB run is below the file size and still ends `ok` with less than 60 MiB
resident, which does show it. The swap limit equals the memory limit in both
runs, so no swap was available.

### Hostile inputs — every limit fails with a stable, named error

Each input runs in its own process with the default limits unless stated
([`results/hostile-runs.jsonl`](results/hostile-runs.jsonl)).

| # | Input (size on disk) | Options | Expected | Observed | Records before the error | Peak RSS MiB | Largest buffer |
|---|---|---|---|---|---|---|---|
| H1 | header with a 1.5 MiB string (1 573 224 B) | — | `QUOTA_EXCEEDED limit=header ordinal=0` | same, offset 34 | 0 | 29.0 | 1 MiB |
| H2 | record 2 with a 1.5 MiB `description` (1 573 231 B) | — | `RECORD_TOO_LARGE limit=record ordinal=2` | same, offset 243 | 1 | 29.1 | 1 MiB |
| H3 | record 2 with a 100 KiB string (102 767 B) | `--token-limit 65536` | `QUOTA_EXCEEDED limit=token ordinal=2` | same, offset 309 | 1 | 27.3 | 128 KiB |
| H4 | record 2 holding 70 nested arrays (498 B) | — | `QUOTA_EXCEEDED limit=depth ordinal=2` | same, offset 363 | 1 | 26.8 | 4 KiB |
| H5 | gzip, record 1 is one 512 MiB string (522 055 B on disk) | — | `RECORD_TOO_LARGE limit=record ordinal=1`, RSS < 256 MiB | same, offset 192 | 0 | 29.2 | 1 MiB |
| H6 | gzip, valid envelope of 105 889 102 B uncompressed (8 829 908 B on disk) | `--input-limit 33554432` | `QUOTA_EXCEEDED limit=input-bytes`, offset ≤ 32 MiB + one buffer | same, offset 33 557 098 (limit + 2 666 B, buffer 4 096 B), ordinal 317 585 | 317 584 | 49.2 | 4 KiB |
| H7 | volume 100 k cut 64 bytes before its end | — | `DATA_INVALID reason=truncated`, no success, records reported | same, ordinal 100 000, offset 19 382 833 (the end of the cut file) | 99 999 | 46.8 | 4 KiB |
| H8 | valid envelope followed by `{"x":1}` | — | `DATA_INVALID reason=trailing-content` | same, offset 350 | 3 | 26.9 | 4 KiB |
| H9 | `items` before `header` | — | `DATA_INVALID reason=header-not-first` | same, offset 25 | 0 | 26.8 | 4 KiB |
| H10 | record 2 with `amount` twice | — | `DATA_INVALID reason=duplicate-property` | same, ordinal 2, offset 295 | 1 | 27.0 | 4 KiB |
| H11 | byte 0xFF inside a string | — | `DATA_INVALID reason=malformed` | same, ordinal 2, offset 302 | 1 | 27.3 | 4 KiB |
| H12 | 4 MiB of spaces between records 1 and 2 | — | exit 0 with a bounded buffer, or `QUOTA_EXCEEDED limit=token` | **`QUOTA_EXCEEDED limit=token`**, ordinal 2, offset 243 | 1 | 28.9 | 1 MiB |
| H13 | `contractVersion` `"2.0"` | — | `DATA_INVALID reason=contract-version` | same, offset 19 | 0 | 26.7 | 4 KiB |
| H14 | `totals.recordCount` one more than the records | — | `DATA_INVALID reason=totals-mismatch` | same, offset 350 | 3 | 26.9 | 4 KiB |

All fourteen give exactly the expected exit code (3), code and limit or reason.
None ends in an out-of-memory or a stack overflow; the worst peak is 49 MiB.
Every error line has only the form
`error code=… limit=|reason=… ordinal=… offset=…`: no path and no data value.
A `--initial-buffer 16` parse of the grouped set gives the same records,
groups, blocks and total as the default (the buffer grows to 4 KiB).

**Whitespace and the buffer (H12).** The JSON reader skips a whitespace run
it cannot finish inside the buffer when the run follows `[`, `:` or a value,
but not when it follows a `,`: then the reader rewinds to before the comma
and needs the comma, the whole run and the next token in one buffer. A
standalone probe shows it: with a 4 KiB buffer and 1 MiB of spaces, the
consumed position moves to 4 096 after `[`, after `:` and before `,`, but
stays at 2 (just before the comma) after `,`. So whitespace after a comma
counts against the buffer cap, and an envelope with more than about 1 MiB of
whitespace after a comma fails with `limit=token`.

### Byte identity — the three engines give the same HTML

[`results/compare.jsonl`](results/compare.jsonl): every part of every record
is rendered by each engine and compared before it is hashed.

| Dataset × template | Records | Mismatches | Scriban SHA-256 | Handlebars SHA-256 | Liquid SHA-256 | HTML bytes |
|---|---|---|---|---|---|---|
| `simple.25` × `pilot-simple` | 25 | 0 | `05fd8372…5211d` | `05fd8372…5211d` | `05fd8372…5211d` | 4 704 |
| `grouped.10k` × `pilot-grouped` | 10 000 | 0 | `da9149aa…94a0` | `da9149aa…94a0` | `da9149aa…94a0` | 3 086 726 |
| `volume.100k` × `pilot-volume` | 100 000 | 0 | `a50dd5bc…950a` | `a50dd5bc…950a` | `a50dd5bc…950a` | 26 658 149 |
| `volume.1m` × `pilot-volume` | 1 000 000 | 0 | `82263b2d…6670` | `82263b2d…6670` | `82263b2d…6670` | 267 556 077 |
| `grouped.10k` × `pilot-grouped`, current culture `es-AR` | 10 000 | 0 | `da9149aa…94a0` | `da9149aa…94a0` | `da9149aa…94a0` | 3 086 726 |
| `grouped.10k` × `pilot-grouped`, 4 renders at once | 10 000 | 0 | `da9149aa…94a0` | `da9149aa…94a0` | `da9149aa…94a0` | 3 086 726 |

Full hashes: simple `05fd83724a3d0ae1071dc491f2b1e81f151f18d52eb6ca9cfbc168d97d15211d`,
grouped `da9149aa952e4f9bcda5f847c6c2b502c19c800f1c892f602283afbb495f94a0`,
volume 100 k `a50dd5bcc529525d66fb66cf457bc4f8106aef287644b811627e7d9432a6950a`,
volume 1 M `82263b2db60fd7a29d67d3b8545a20468cbab31cdd7a1df58873a2d830506670`.
The single-engine `render` runs of the measurements give the same hashes.

Escaping checks (in `selfcheck`, on every row): 2 hostile rows in the simple
set, 216 in the grouped set and 979 in volume 100 k all contain the policy's
encoding of the value and never the raw value when it has `<`, `>`, `&`, `"`
or `'`. The template-looking values (`{{ header.customer.name }}`,
`{% raw %}{{ 7*7 }}{% endraw %}`, `${7*7}`) appear literally, never
evaluated. The 6, 6 984 and 49 023 rows with non-ASCII text carry it as the
policy defines (Latin-1 letters as numeric references, `€` raw).

The render context is the same in the three engines: `header`, `item`,
`group` (`groupId`, `groupName`), `block` (`index`, `recordCount`),
`aggregates` (`groupAmount`: `0.00` at the group start, includes the current
record at the row, the group total at the group end) and `render` (culture
`es-AR`, time zone `America/Argentina/Buenos_Aires`, the header's generation
time). **Every number is given to the engines as its invariant text**
(`1000.00`, `9007199254740994`, `3225.661884312400`), because Fluid drops the
trailing zeros of a `decimal` (`1000.00` printed as `1000.0`, `-0.50` as
`-0.5`); with text, the three engines print every number exactly as received,
whatever the current culture. Missing values (`groupName` in the volume set)
print as empty in the three engines.

### Per-engine notes

Render runs: volume dataset, `pilot-volume`, one engine per process, HTML to
a hashing sink ([`results/render-runs.jsonl`](results/render-runs.jsonl)).
"Render µs/record" counts only the time inside the engine calls; it is the
median over 3 runs (1 run for the per-block context).

| | Scriban 7.5.1 | Handlebars.Net 2.4.3 | Liquid (Fluid 2.40.0) |
|---|---|---|---|
| Template parse/compile, all parts, once | 34 ms | 93 ms | 95 ms |
| Render µs/record, 1 M, new context per render | 22.78 | 2.28 | 2.80 |
| Render µs/record, 1 M, one context per block | 5.70 | 2.08 | 2.62 |
| Render µs/record, 100 k, new context per render | 46.08 | 7.36 | 7.78 |
| Allocated per record, 1 M, new context per render (reading included) | 37.2 KiB | 2.9 KiB | 3.8 KiB |
| Allocated per record, 1 M, one context per block (reading included) | 3.1 KiB | 2.2 KiB | 2.7 KiB |
| Managed heap peak MiB, 100 k → 1 M | 20.5 → 20.5 | 20.0 → 20.2 | 19.2 → 19.5 |
| Process peak RSS MiB, 100 k → 1 M | 66.0 → 64.1 | 66.0 → 66.4 | 63.2 → 63.6 |
| Rule T on the render runs | pass | pass | pass |
| Escapes `{{ }}` by default | **no** — escaping only where the template calls `html.escape` | yes | yes (with an encoder; `| raw` bypasses it) |
| Native escaping vs the policy | `html.escape` **is** the policy | encodes `'` as `&#x27;`, `` ` `` and `=` too, leaves Latin-1 and `𝄞` raw | hexadecimal references, encodes `€` and every non-ASCII letter |
| Escaping policy implemented by | the templates (`html.escape`) | a text encoder in the configuration | a text encoder passed to each render |
| Numbers | prints `decimal` exactly | prints `decimal` exactly with an invariant format provider (default is the current culture) | drops trailing zeros of `decimal` |
| Thread safety | parsed template shared; context per render | compiled template shared | parsed template shared; context per render |
| Stop on a runaway template | `LoopLimit` 1 000 (range checked before the loop starts) | **none** | `MaxSteps` (0 = unlimited by default), but see below; output-size limit not available in 2.40.0 |
| Cancellation | yes (`TemplateContext.CancellationToken`) | no | **not available in 2.40.0** |

The 100 k figures are higher than the 1 M ones because a shorter run spends a
larger share of its time before the runtime has optimised the hot code; the
1 M figures are the steady state. Scriban's new context per render is the
expensive part (about 34 KiB allocated per record): reusing one context per
block makes it four times faster. For Handlebars and Liquid the context costs
little.

Limits ([`results/limits.md`](results/limits.md), each runaway template under
a 10-second guard enforced by the harness):

- **Scriban** stops `{{ for i in 1..100000000 }}` at once with
  `ScriptRuntimeException: Range expression exceeds LoopLimit 1000`
  (0 characters written). `RecursiveLimit` 100 and `LimitToString` 1 048 576
  stay at their defaults.
- **Liquid (Fluid 2.40.0)** with `MaxSteps` 100 000: `{% for i in (1..100000000) %}`
  stops after 50 000 iterations, but with an `InvalidOperationException` whose
  message speaks of recursion ("The maximum level of recursion has been
  reached…"), not a `FluidException`; and it first **materialises the whole
  range**: 6.8 s and 4 588 MiB allocated before the step limit applies (a
  standalone probe: 82 MiB peak for a 1 M range, 497 MiB for 10 M, 4.6 GiB for
  100 M). The step limit therefore does not protect against a large range in
  a template. An **output-size limit is not available in 2.40.0**
  (`TemplateOptions` has no `MaxOutputSize`; a 2 MiB value is written in
  full), and **cancellation is not available in 2.40.0** (`TemplateContext`
  has no `CancellationToken`). `MaxRecursion` defaults to 100 and is not
  exercised (no includes). The library's current documentation describes an
  output-size limit and cancellation; they belong to a later version line,
  not to 2.40.0.
- **Handlebars.Net** has no step, loop or output limit and its compiled
  template takes no cancellation token: `{{#each}}` over 10 000 000 elements
  runs to the end (0.6 s, 10 000 000 characters). Only the harness can stop
  it (for example through the output writer, as the guard here does).

Native escaping, value by value: [`results/escaping.md`](results/escaping.md).

## Conclusion

Observations and recommendations for the decisions that follow; nothing here
is decided.

**Data limits.**

- Incremental reading works and is flat: peak managed heap about 19–20 MiB
  and peak resident memory under 60 MiB from 100 k to 1 M records, plain and
  gzip, and the 1 M gzip file reads in a container capped at 128 MiB, below
  the uncompressed file size. Throughput is about 390 k records per second
  for 1 M records of the volume set on this machine.
- Every limit can be enforced before memory is at risk: header, record,
  token, depth, input size and the structural errors all fail with a stable
  code and a named limit or reason, with a peak of at most 49 MiB, including a
  512 MiB string that is only 0.5 MiB compressed.
- The **record limit is in practice the buffer limit**: the read buffer must
  hold a whole record, so its cap is set by the larger of the header and
  record limits. A **token limit** below the record limit is observable on its
  own (H3) and bounds the buffer earlier. The pilot data never needed more
  than 4 KiB; the 1 MiB starting figures leave a wide margin (largest pilot
  record 2.7 KiB).
- **Depth** is checked per token before the JSON reader's own limit; 64 is far
  above the pilot data (depth 3: root, `items`, record).
- **Whitespace** after a comma counts against the buffer cap (see H12). A
  producer that pretty-prints is fine; an envelope padded with more than about
  1 MiB of whitespace after a comma fails with `limit=token`. Worth stating in
  the envelope's contract.
- A document cut short is always reported as `reason=truncated` with the
  number of records read before it, because the reader never treats a buffer
  as final until the stream ends.

**Template-engine adapter contract.**

- **Render context:** one model works for the three engines — six names,
  plain dictionaries, rebuilt per record — and gives byte-identical HTML for
  1 M records. Passing **every number as its invariant text** is the simplest
  way to make the three engines agree (Fluid drops `decimal` trailing zeros;
  Handlebars formats with the current culture unless told otherwise).
- **Escaping policy:** one policy, implemented per engine — Scriban by
  template discipline (`html.escape` on every text value, nothing automatic);
  Handlebars and Liquid by a text encoder, so every `{{ }}` is escaped
  (`{{{ }}}` and `| raw` bypass it). The engines' native escapers differ from
  each other (`'` as `&#39;` or `&#x27;`, Latin-1 raw or encoded), so a policy
  that is not native to an engine must be supplied to it explicitly, as here.
  Scriban's lack of automatic escaping is the largest difference between the
  engines and should be checked on templates (for example a lint that every
  text value goes through `html.escape`).
- **Per-record vs per-block context:** for Scriban, a new context per render
  costs about four times more time and twelve times more allocation than one
  context per block; for Handlebars and Liquid the difference is small. If a
  context is reused within a block, it must be reset per block and never
  shared between threads.
- **Limit presets:** only Scriban stops a runaway template by itself and
  supports cancellation. Liquid (Fluid 2.40.0) has a step limit (`MaxSteps`)
  that fires only after a range has been materialised and reports it with an
  `InvalidOperationException` and an unrelated recursion message; an
  output-size limit and cancellation are not available in 2.40.0.
  Handlebars has nothing. So, with Fluid 2.40.0, **an output-size bound and
  cancellation must come from the caller** — an external guard around the
  render — and `MaxSteps` does **not** prevent the materialisation of a large
  range; Handlebars needs the same guards. For those two engines the service
  needs a time limit enforced from outside, an output-size limit enforced by
  the output writer, and template review that forbids large literal ranges.
- **Cost:** with a context per block, the three engines render a volume row in
  about 2.1–5.7 µs; with a new context per render, Handlebars (2.3 µs) and
  Liquid (2.8 µs) stay cheap while Scriban costs 23 µs. Memory does not grow
  with the record count for any engine.

## Packages and licences

Declared once, with exact versions, in
[`Directory.Packages.props`](Directory.Packages.props) (this folder only; the
repository-wide file is imported first). All are stable releases from
nuget.org, used nowhere outside this folder. Licences read from each
package's `.nuspec` at the exact version; `dotnet list package --vulnerable --include-transitive`
reports no vulnerable package.

| Package | Version | Licence | How it is used |
|---|---|---|---|
| Scriban | 7.5.1 | BSD-2-Clause | template engine |
| Handlebars.Net | 2.4.3 | MIT | template engine |
| Fluid.Core | 2.40.0 | MIT | template engine (Liquid) |
| Parlot | 1.5.2 | BSD-3-Clause | dependency of Fluid.Core |
| TimeZoneConverter | 7.2.0 | MIT | dependency of Fluid.Core |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.0 | MIT | dependency of Fluid.Core |
| Microsoft.Extensions.Primitives | 10.0.0 | MIT | dependency of Microsoft.Extensions.FileProviders.Abstractions |

All licences are compatible with the project's MIT licence.

## Deviations from the sample templates

None. The thirteen Scriban parts of `pilot-simple`, `pilot-grouped` and
`pilot-volume` parse and render unchanged and use only `header.*`, `item.*`,
`group.groupId`, `group.groupName`, `aggregates.groupAmount` and
`html.escape`. The Handlebars and Liquid variants in `templates/` are the same
text with ` | html.escape` removed, because those two engines escape every
`{{ }}` through the policy encoder. Two observations on the parts, for a later
revision of the samples: the volume group heading prints the group id
followed by an empty name (`G000001 `), since the volume set has no
`groupName`; and the grouped and volume tables open their `<tbody>` in the
group parts, so a dataset without groups rendered with those templates would
produce rows outside any `<tbody>`.
