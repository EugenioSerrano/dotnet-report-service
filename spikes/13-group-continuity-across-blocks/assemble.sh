#!/bin/sh
# Assembles and verifies every report instance prepared by "set-manifest", inside the
# measuring image of spikes/06-pdf-assembly-chromium-parts (run unchanged, with this
# script as its entry point). Uses only that image's own commands:
#   - the numbered report: assemble --variant pdfsharp-merge-stamp (merge and global stamp in one pass);
#   - the unnumbered report: qpdf-args, then qpdf @args (merge only);
#   - verify on both (structure, page boxes, order by content words, numbering, pixels on every page);
#   - for the numbering controls, on the instance named in the second argument: the unnumbered report
#     verified as numbered (nx1.json), and the numbered report verified against the manifest with
#     parts 2 and 3 swapped (nx2.json).
# Each step runs under "timeout 600". Writes assembly.txt per instance (exit codes and wall times).
# Usage: sh assemble.sh REPORTS_DIR CONTROL_INSTANCE
set -u

reports=$1
control=$2
app=/app/PdfAssemblySpike.dll

now() { date +%s%N; }

ms() { echo $(( ($2 - $1) / 1000000 )); }

for dir in "$reports"/*/; do
    instance=$(basename "$dir")
    [ -f "$dir/set.json" ] || continue
    work=/tmp/assembly-$instance
    rm -rf "$work"
    mkdir -p "$work"

    t0=$(now)
    timeout 600 dotnet $app assemble --variant pdfsharp-merge-stamp --manifest "$dir/set.json" --out "$dir/numbered.pdf" \
        --temp "$work/numbered" --metrics "$dir/metrics-numbered.json" > "$dir/assemble-numbered.log" 2>&1
    numbered=$?
    t1=$(now)
    timeout 600 dotnet $app qpdf-args --manifest "$dir/set.json" --output "$dir/unnumbered.pdf" --args "$work/qpdf-args.txt" > "$dir/assemble-unnumbered.log" 2>&1 \
        && timeout 600 qpdf "@$work/qpdf-args.txt" >> "$dir/assemble-unnumbered.log" 2>&1
    unnumbered=$?
    t2=$(now)
    timeout 600 dotnet $app verify --manifest "$dir/set.json" --pdf "$dir/numbered.pdf" --numbered true --pages all \
        --temp "$work/verify-numbered" --box-rule source-or-rounded --report "$dir/verify-numbered.json" > "$dir/verify-numbered.log" 2>&1
    verifyNumbered=$?
    timeout 600 dotnet $app verify --manifest "$dir/set.json" --pdf "$dir/unnumbered.pdf" --numbered false --pages all \
        --temp "$work/verify-unnumbered" --box-rule source-only --report "$dir/verify-unnumbered.json" > "$dir/verify-unnumbered.log" 2>&1
    verifyUnnumbered=$?
    t3=$(now)

    if [ "$instance" = "$control" ]; then
        timeout 600 dotnet $app verify --manifest "$dir/set.json" --pdf "$dir/unnumbered.pdf" --numbered true --pages all \
            --temp "$work/nx1" --box-rule source-or-rounded --report "$dir/nx1.json" > "$dir/nx1.log" 2>&1
        timeout 600 dotnet $app verify --manifest "$dir/set-swapped.json" --pdf "$dir/numbered.pdf" --numbered true --pages all \
            --temp "$work/nx2" --box-rule source-or-rounded --report "$dir/nx2.json" > "$dir/nx2.log" 2>&1
    fi

    {
        echo "numberedExit $numbered"
        echo "numberedMs $(ms "$t0" "$t1")"
        echo "unnumberedExit $unnumbered"
        echo "unnumberedMs $(ms "$t1" "$t2")"
        echo "verifyNumberedExit $verifyNumbered"
        echo "verifyUnnumberedExit $verifyUnnumbered"
        echo "verifyMs $(ms "$t2" "$t3")"
    } > "$dir/assembly.txt"
    echo "$instance: numbered $numbered, unnumbered $unnumbered, verify $verifyNumbered/$verifyUnnumbered"
    rm -rf "$work"
done
