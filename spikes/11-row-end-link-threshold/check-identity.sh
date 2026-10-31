#!/bin/sh
# Prints one line per reused file: expected SHA-256, actual SHA-256, path.
# Run from the repository root; write the output to
# spikes/11-row-end-link-threshold/results/identity.txt. Every line must show two
# equal hashes: the files of spikes/09-two-pass-page-subtotals that are linked,
# copied or adapted here, the measured and reading code that runs inside the
# reused images, the earlier experiment's committed probe result, and the shared
# fixture series' committed sums and text check of spikes/07-native-tall-rows.
set -eu

line() { # expected actual path
    printf '%s %s %s\n' "$1" "$2" "$3"
}

file() { # expected path
    line "$1" "$(sha256sum < "$2" | cut -c1-64)" "$2"
}

at_commit() { # expected commit path
    line "$1" "$(git show "$2:$3" | sha256sum | cut -c1-64)" "$3@$2"
}

N=spikes/09-two-pass-page-subtotals
S=spikes/07-native-tall-rows
file d08f4f06d28b4e2fc30be341281b9608dfd6b270908841872f74e49bd5a96e87 $N/TwoPassSpike/LayoutRun.cs
at_commit d08f4f06d28b4e2fc30be341281b9608dfd6b270908841872f74e49bd5a96e87 94415a9 $N/TwoPassSpike/LayoutRun.cs
file d8f58d43043258f4617fe7fe5d1a4e0d296569cea71b1e7d0c56e48b71f0a4ed $N/TwoPassSpike/ProcessTreeSampler.cs
at_commit d8f58d43043258f4617fe7fe5d1a4e0d296569cea71b1e7d0c56e48b71f0a4ed 94415a9 $N/TwoPassSpike/ProcessTreeSampler.cs
file 2717cc270a3ee9111640b58d9fe0be1767bd9e29d9ba3ffe7e10ef066b2ab018 $N/TwoPassSpike/MatrixDriver.cs
at_commit 2717cc270a3ee9111640b58d9fe0be1767bd9e29d9ba3ffe7e10ef066b2ab018 94415a9 $N/TwoPassSpike/MatrixDriver.cs
file 3e558252f6dc9a431c7fbb8f21daeb473a9978cc30463b2fe56bba3476ef55b0 $N/TwoPassSpike/Program.cs
at_commit 3e558252f6dc9a431c7fbb8f21daeb473a9978cc30463b2fe56bba3476ef55b0 94415a9 $N/TwoPassSpike/Program.cs
file f59625dc4b149d278e89b774a94d5efb9119d3bfec19d9c1b1659e0058828d4b $N/PageAccountingCheck/OrdinaryFixtures.cs
at_commit f59625dc4b149d278e89b774a94d5efb9119d3bfec19d9c1b1659e0058828d4b 94415a9 $N/PageAccountingCheck/OrdinaryFixtures.cs
file 649b198c958099b477969f5df682e73b252bf185bac46fd2c37f03ae798b3cdf $N/TwoPassSpike/HtmlBuilder.cs
at_commit 649b198c958099b477969f5df682e73b252bf185bac46fd2c37f03ae798b3cdf 94415a9 $N/TwoPassSpike/HtmlBuilder.cs
file bf95360bd1c90f95635ee05a566a37ddd8e4d7f6bde6cde67df9a305033eb4a1 $N/TwoPassSpike/assets/native.css
at_commit bf95360bd1c90f95635ee05a566a37ddd8e4d7f6bde6cde67df9a305033eb4a1 94415a9 $N/TwoPassSpike/assets/native.css
file d3bdf9a2f4baebdbbedd8dedeb005031b2a48aee2d89fb31ceb06acb82c4da19 $N/TwoPassSpike/MarkerReader.cs
at_commit d3bdf9a2f4baebdbbedd8dedeb005031b2a48aee2d89fb31ceb06acb82c4da19 94415a9 $N/TwoPassSpike/MarkerReader.cs
file cfb2b1c96d2325296e7a1b9a064dc8afa0228eec0bc793ea5d3a956e0c4f0a81 $N/TwoPassSpike/StampRun.cs
at_commit cfb2b1c96d2325296e7a1b9a064dc8afa0228eec0bc793ea5d3a956e0c4f0a81 94415a9 $N/TwoPassSpike/StampRun.cs
file 59d6bee5b6a552fd24986c1f5888b0414a3f0294a5e7c1e24d2b22f6d1f3fe5b $N/TwoPassSpike/TwoPassSpike.csproj
at_commit 59d6bee5b6a552fd24986c1f5888b0414a3f0294a5e7c1e24d2b22f6d1f3fe5b 94415a9 $N/TwoPassSpike/TwoPassSpike.csproj
file 5bcedb8062dd975bc352d423aaea1b0e1a58f2d807eb11746494131e3a4844dc $N/PageAccountingCheck/ProbeCommand.cs
at_commit 5bcedb8062dd975bc352d423aaea1b0e1a58f2d807eb11746494131e3a4844dc 94415a9 $N/PageAccountingCheck/ProbeCommand.cs
file eb8f227c512c46c7c6757380fbb41bf7b5c3913d5fe18d59301b6a08575239be $N/PageAccountingCheck/Qpdf.cs
at_commit eb8f227c512c46c7c6757380fbb41bf7b5c3913d5fe18d59301b6a08575239be 94415a9 $N/PageAccountingCheck/Qpdf.cs
file 50a25921bf15619e174032f9d4a2586416ebed7a71ce7e451adc0ddc44ae3810 $N/PageAccountingCheck/PdfReading.cs
at_commit 50a25921bf15619e174032f9d4a2586416ebed7a71ce7e451adc0ddc44ae3810 94415a9 $N/PageAccountingCheck/PdfReading.cs
file a88705672a1cbffef214ba7cd3d608c37e47fa7f622d7e5c12a45f0b2549a616 $N/PageAccountingCheck/Records.cs
at_commit a88705672a1cbffef214ba7cd3d608c37e47fa7f622d7e5c12a45f0b2549a616 94415a9 $N/PageAccountingCheck/Records.cs
file b421a86436fa22200ac77e0e039f1ede312d6255a7f13658d6d73d0b43a5d82d $N/Dockerfile
at_commit b421a86436fa22200ac77e0e039f1ede312d6255a7f13658d6d73d0b43a5d82d 94415a9 $N/Dockerfile
file ed35d52fe00a5fa071d83d41e75161402c3934d8f5e59ef60c17992a3fc8e0dc $N/Dockerfile.account
at_commit ed35d52fe00a5fa071d83d41e75161402c3934d8f5e59ef60c17992a3fc8e0dc 94415a9 $N/Dockerfile.account
file 71da38148725be87f792f5304eb232021bd295dd04ba9028338816c49716b500 $N/results/probe-split-rows-4.json
at_commit 71da38148725be87f792f5304eb232021bd295dd04ba9028338816c49716b500 94415a9 $N/results/probe-split-rows-4.json
file c1576a4ec8b335bc9a7525dbe762933f7079b1a48882ea06b1fffe7d67190316 $S/results/fixtures.sha256
file 81b0ea417774d33824bdd419b08d9c8a5b37e165e97a251f080a83964f6d6884 $S/results/integrity.json

# The run-record declarations: lines 12–173 of the harness's layout runner, and the copy's body.
T=317868330202acc4aaf5e9c567cfd62bcb1e41334bcdfb465ccb8b19c1d00530
line $T "$(sed -n '12,173p' $N/TwoPassSpike/LayoutRun.cs | sha256sum | cut -c1-64)" "$N/TwoPassSpike/LayoutRun.cs:12-173"
line $T "$(tail -n +4 spikes/11-row-end-link-threshold/RowEndSweep/Reused/RecordTypes.cs | sha256sum | cut -c1-64)" "spikes/11-row-end-link-threshold/RowEndSweep/Reused/RecordTypes.cs:4-"
