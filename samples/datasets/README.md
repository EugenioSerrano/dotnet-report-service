# Pilot datasets

Three synthetic datasets in the `header-items-v1` envelope, produced by a
seeded generator so that anyone can regenerate exactly the same bytes. They
are the data every published measurement of dotnet-report-service runs on.

The dataset files are **generated, never edited and never committed** (they
are ignored by git). What is committed is the generator, in
[`ReportService.Samples.DatasetGenerator/`](ReportService.Samples.DatasetGenerator/),
and [`SHA256SUMS`](SHA256SUMS), the expected hashes of the default files —
the reproducibility contract. All data is fictional: no real person, company
or account appears in any file.

## The envelope

Each file is one UTF-8 JSON object (no byte-order mark, ending with one line
feed) whose properties are, in this order:

- `contractVersion` — `"1.0"`;
- `header` — small, and always before the items: dataset id, report title,
  a fixed generation time (`2026-10-01T00:00:00Z`), the seed, the currency
  (`ARS`), the customer, and the precalculated totals (record count, group
  count for the grouped sets, exact amount total);
- `items` — the records in their final order, each with a `sequence` from 1
  to N without gaps; records of one group are always contiguous.

The header and every record are far below 1 MiB.

## The three datasets

| Dataset | Default file | Records | What it contains |
|---------|--------------|---------|------------------|
| simple | `header-items-v1.simple.25.json` | 25 | An invoice-like list without groups: sequence, description, whole quantity (1–20), unit price and amount with 2 decimals (amount = quantity × unit price, exact). Descriptions come from a fixed list of 40 phrases in English and Spanish; records 7 and 19 carry hostile text (`<script>…`, `Tom & Jerry "quoted" <b>bold</b>`). Fits in a single block. Indented. |
| grouped | `header-items-v1.grouped.10k.json` | 10 000 | A statement with contiguous groups and subtotals: sequence, group id (`G0001`, `G0002`, …), group name, date in September 2026, description and amount (−5 000.00 to 50 000.00, about 3 % negative). Group sizes vary between 20 and 400 records, except the third group (1 record), the sixth (1 200) and the twelfth (2 400), so groups cross 1 000-record blocks; the last group is cut to fit the count. Descriptions have very different lengths (60 % 10–40 characters, 30 % 41–200, 9 % 201–600, 1 % 1 500–2 500 — rows long enough to split across pages); about 2 % are hostile text. Indented. |
| volume | `header-items-v1.volume.1m.json` | 1 000 000 | A high-volume ledger: sequence, group id (`G000001`, …), a 20-digit account id with 2 to 6 leading zeros, description, a quantity with exactly 16 significant digits (4 integer + 12 decimal digits), amount with 2 decimals (about 3 % negative) and an integer reference above 2^53. Groups have between 1 and 5 000 records. Descriptions: 30 % unique (`Ref-<sequence>` plus words), 1 % formula-like text starting with `=`, `+`, `-` or `@`, 1 % hostile text, the rest from a 500-phrase vocabulary. Compact (no indentation). |

Non-ASCII text (accented letters, `ñ`, `€`) is written as raw UTF-8, so
multi-byte characters cross read-buffer boundaries. Hostile and
formula-like values are test data for escaping and spreadsheet safety; they
are written as plain strings and never evaluated.

## Commands

Run from anywhere inside the repository (paths default to this folder and to
[`../templates/`](../templates/)):

```bash
# Generate the three default files into samples/datasets/
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate

# One dataset, another size or seed, another folder
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --dataset volume --count 100000
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate --seed 42 --output /tmp/datasets

# Check everything: same bytes twice, hashes equal SHA256SUMS, structure,
# characteristics, and the sample files of the templates
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- verify

# Streaming structural check of any generated file, with its counters
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- check samples/datasets/header-items-v1.grouped.10k.json

# Rewrite the sample.json of the three sample templates
dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- samples
```

`generate` options: `--dataset simple|grouped|volume|all` (default `all`),
`--count N` (needs a single dataset), `--seed N` (default `20261021`),
`--output DIR`. `verify` options: `--sums FILE`, `--templates DIR`. `check`
option: `--initial-buffer BYTES` (default 65 536, minimum 16). Exit codes: 0
success, 1 failure, 2 invalid arguments.

File names follow the record count: `header-items-v1.<dataset>.<count>.json`
with the count in millions (`1m`), thousands (`10k`) or units (`25`).

`generate` prints one line per file (records, groups, bytes, SHA-256,
seconds) and the peak working set of the process.

## Seed and reproducibility

The default seed is **20261021**. The generator uses its own pseudo-random
number generator (SplitMix64), invariant number formatting, `\n` line
breaks, explicit decimal scales and a fixed generation time, so the same
seed and count produce byte-identical files on every operating system and
.NET runtime patch. Each dataset draws from its own sequence (seed × 31 +
1, 2 or 3), so generating one dataset alone gives the same file as
generating all three.

Default hashes (also in [`SHA256SUMS`](SHA256SUMS), checked by `verify`):

| File | SHA-256 |
|------|---------|
| `header-items-v1.simple.25.json` | `9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3` |
| `header-items-v1.grouped.10k.json` | `d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753` |
| `header-items-v1.volume.1m.json` | `6075bea4c0173633a1c5090f4a9b7047d3bdeb143139484a20f49f9f76986ace` |

These hashes were produced on Windows and reproduced in the Linux .NET SDK
container (`mcr.microsoft.com/dotnet/sdk:10.0.402-noble`). They change only
when the generator's output is changed on purpose — then `SHA256SUMS` and
this page are updated in the same change.

## Volume sizes used by the measurements

Measurements that need fewer records use these exact files, all with the
default seed, so every result can cite the file it ran on:

| Invocation | File | Records | SHA-256 |
|------------|------|---------|---------|
| `generate --dataset volume --count 100000` | `header-items-v1.volume.100k.json` | 100 000 | `c9f1b968f501917a0dbffb488a5767fdfe1497111a6c443fb0517ed8b59475d4` |
| `generate --dataset volume --count 500000` | `header-items-v1.volume.500k.json` | 500 000 | `3285fccd1b436dbe192802b5d9d532d120a4fb7c063e5c07b4b638274a3c7352` |
| `generate --dataset volume --count 1000000` | `header-items-v1.volume.1m.json` | 1 000 000 | `6075bea4c0173633a1c5090f4a9b7047d3bdeb143139484a20f49f9f76986ace` |

`SHA256SUMS` pins only the three default files; the 100 000 and 500 000
record hashes are pinned here.

## Memory

The generator never holds the records in memory: it runs the record sequence
once to compute the header totals and once more to write, flushing every
64 KiB. Its peak working set does not grow with the record count — on the
[reference environment](../../docs/reference-environment.md) about 54 MiB
for 100 000 records and 55 MiB for 1 000 000 on Windows (about 70–75 MiB
inside the Linux SDK container). The 1 000 000-record file is about
186 MiB and takes under two seconds to write there.
