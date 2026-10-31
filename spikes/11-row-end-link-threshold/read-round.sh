#!/bin/sh
# Reads one run folder with the two unchanged checker images, one named container
# at a time: the text check (pages and the row's printed span), then the split-row
# probe (qpdf's link annotations and the end-rectangle fields). Run from the
# repository root as: sh spikes/11-row-end-link-threshold/read-round.sh <host root as the engine sees it> <round folder name>
# The round folder is spikes/11-row-end-link-threshold/results/raw/<name>, holding runs/.
set -u
W=$1
NAME=$2
S=spikes/11-row-end-link-threshold
R=$S/results/raw
D=$R/$NAME
TS=$(date +%y%m%d%H%M%S)
T=text-integrity-check-$TS-$NAME
echo "$T" >> $R/containers.txt
docker run --name "$T" --init --memory 2g --memory-swap 2g \
  -v "$W/$S/results/raw/fixtures:/fixtures:ro" -v "$W/$D/runs:/runs:ro" \
  text-integrity-check:local check --fixtures /fixtures --runs /runs --out /out/o07 > "$D/o07.log" 2>&1
echo "$?" > "$D/o07.exit"
docker cp "$T:/out/o07" "$D/o07" > /dev/null
docker rm "$T" > /dev/null
P=page-accounting-check-$TS-$NAME
echo "$P" >> $R/containers.txt
docker run --name "$P" --init --memory 2g --memory-swap 2g \
  -v "$W/$S/results/raw/fixtures:/fixtures:ro" -v "$W/$D/runs:/runs:ro" -v "$W/$D/o07:/o07:ro" \
  -v "$W/spikes/07-native-tall-rows/results:/ref07:ro" \
  page-accounting-check:local probe split-rows --fixtures /fixtures --runs /runs --integrity /o07/integrity.json \
  --reference-integrity /ref07/integrity.json --out /out/o09 --work /work > "$D/o09.log" 2>&1
EXIT=$?
docker cp "$P:/out/o09" "$D/o09" > /dev/null
echo "$EXIT" > "$D/o09/exit.txt"
docker rm "$P" > /dev/null
echo "text check exit $(cat "$D/o07.exit"), probe exit $EXIT"
