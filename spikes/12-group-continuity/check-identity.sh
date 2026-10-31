#!/bin/sh
# Prints one line per reused file and per piece of reused evidence: expected
# SHA-256, actual SHA-256, path. Run on the host from the repository root and
# write the output to spikes/12-group-continuity/results/identity.txt. Every
# line must show two equal hashes. First the whole identity list of
# spikes/10-pagedjs-table-strict-accounting, run as it is (the files of
# spikes/02, 03, 07, 08 and 09 and the committed results of
# spikes/08-pagedjs-pre-split); then the files of
# spikes/10-pagedjs-table-strict-accounting this experiment links or reads, and
# the grouped dataset the group fixtures are built from. Nothing listed here is
# ever written by this experiment.
set -eu

file() { # expected path
    printf '%s %s %s\n' "$1" "$(sha256sum < "$2" | cut -c1-64)" "$2"
}

T=spikes/10-pagedjs-table-strict-accounting
file 415522bbfc6c386b4eef41359b737e01ded2ad3cb68853bc411a9f1d610e1b63 $T/check-identity.sh
sh $T/check-identity.sh
file c14f902e4e0d4ff20eab297e88ea41324b29bd18f472a676569d475c18aa50a1 $T/PagedMarkupSpike/PreSplitRunner.cs
file eeddced3773265b2e454f551f687565044fc90ca084821a5170c5e0bf5205dcc $T/PagedMarkupSpike/MatrixDriver.cs
file 18a98dc5bf678e50d24dddbf101407cb3b83744ba531b9829f1e75943f2ac00b $T/PagedMarkupSpike/Plan.cs
file 5b2f9485577e2e3640e49e10babbe57526eac5f3cffa7204a08b39c104307b57 $T/StrictAccountingCheck/Account.cs
file 4e84e3481e510d35a67d840a0dec254d4e087209f23e9d73ae311001ed18997e $T/StrictAccountingCheck/CharacterCheck.cs
file 5b2894595f202f1e0d0d904934cbeddb615e0a83a182623991f52513eb814512 $T/StrictAccountingCheck/HarnessRecords.cs
file 0360cc27444131ed5619cf11ee5ec33190ce80a7b3537b58e55b0d2d36535493 $T/StrictAccountingCheck/Controls.cs
file 1d851afccfa45bbec3a3f09aba13602689a04a3c7b09d20d7187148eaac7ef52 $T/Dockerfile
file 564980323ff6824dc426d98b1315c5eb05242636e1eaa83b8e27cfd2c32660de $T/Dockerfile.account
file 40182ba1275dcae0e5f20008b21b7a35058783dddae8cec9b28814e3a73470a6 $T/results/summary.json
file d1969e96b5413f21c2f04f1ec789b01fe9ea30e3d6ad78691521350ec580c753 samples/datasets/header-items-v1.grouped.10k.json
