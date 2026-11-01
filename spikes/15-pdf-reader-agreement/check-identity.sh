#!/bin/sh
# Identity of every file this experiment reads but does not own: the linked
# source files, the dataset, the earlier fixture lists, the committed PDFs and
# the earlier results it compares with. Run from the repository root (Git Bash
# or any POSIX shell with sha256sum). Exit 0 when every hash is unchanged.
set -eu
here=spikes/15-pdf-reader-agreement
if [ "${1:-}" = "--write" ]; then
  {
    sed -n 's/.*Include="\.\.\/\.\.\/\([^"]*\)".*/spikes\/\1/p' "$here/ReaderAgreement/ReaderAgreement.csproj" | sed 's#spikes/\.\./#spikes/#'
    sed -n 's/.*Include="\$(AccountingCheck)\([^"]*\)".*/spikes\/09-two-pass-page-subtotals\/PageAccountingCheck\/\1/p' "$here/ReaderAgreement/ReaderAgreement.csproj"
    sed -n 's/.*Include="\$(StrictCheck)\([^"]*\)".*/spikes\/10-pagedjs-table-strict-accounting\/StrictAccountingCheck\/\1/p' "$here/ReaderAgreement/ReaderAgreement.csproj"
    ls spikes/07-native-tall-rows/PaginationFixtures/*.cs
    echo samples/datasets/header-items-v1.grouped.10k.json
    echo samples/datasets/SHA256SUMS
    for f in 07-native-tall-rows 09-two-pass-page-subtotals 12-group-continuity; do echo "spikes/$f/results/fixtures.sha256"; done
    for f in 07-native-tall-rows 08-pagedjs-pre-split 09-two-pass-page-subtotals 10-pagedjs-table-strict-accounting 12-group-continuity; do
      for r in integrity.json accounting.json; do [ -f "spikes/$f/results/$r" ] && echo "spikes/$f/results/$r"; done
    done
    git ls-files 'spikes/0*/results/*.pdf' 'spikes/0*/results/**/*.pdf' 'spikes/0*/fixtures/*.pdf' 'spikes/1[0-2]*/results/**/*.pdf' | sort -u
  } | sort -u | xargs sha256sum > "$here/results/identity.sha256"
  echo "written: $(wc -l < "$here/results/identity.sha256") files"
  exit 0
fi
sha256sum --quiet -c "$here/results/identity.sha256" && echo "identity: every file unchanged ($(wc -l < "$here/results/identity.sha256") files)"
