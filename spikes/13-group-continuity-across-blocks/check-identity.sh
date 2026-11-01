#!/bin/sh
# Prints one line per reused file and per piece of reused evidence: expected
# SHA-256, actual SHA-256, path. Run on the host from the repository root and
# write the output to spikes/13-group-continuity-across-blocks/results/identity.txt.
# Every line must show two equal hashes. First the whole identity list of
# spikes/12-group-continuity, run as it is (which runs the list of
# spikes/10-pagedjs-table-strict-accounting: the files of spikes/02, 03, 07, 08
# and 09 and their committed results, and the grouped dataset); then the files of
# spikes/12-group-continuity this experiment links or reads and its committed
# results (the parity reference); the assembly harness of
# spikes/06-pdf-assembly-chromium-parts whose image this experiment runs; and the
# in-page accounting script of spikes/02-pdf-layout this experiment's derived
# script comes from. Nothing listed here is ever written by this experiment.
set -eu

file() { # expected path
    printf '%s %s %s\n' "$1" "$(sha256sum < "$2" | cut -c1-64)" "$2"
}

G=spikes/12-group-continuity
file f214e20f5f09f3b4353293b02c4fc89a0ae3aa8a678d38f3446149d29d968cf3 $G/check-identity.sh
sh $G/check-identity.sh
file 000a56b1ac2b9ccdae9a6985ffff71f81c8715e4d8c342b8ed7f9bab5f192544 $G/GroupContinuityCheck/GroupFixtures.cs
file 3adf6bd0ed236aef17323a2e6a6d263866f287851256f372b5edfea8845d117b $G/GroupContinuityCheck/GroupCheck.cs
file cec484f5945d691aaf5a85e2963223772c29c8b7ccd7b0bddab89f4d6ffd34ad $G/GroupContinuityCheck/GroupContinuityCheck.csproj
file 9db03f46c3eaf93624d764594fd23049816619339bf83b5b3ab1840b1b8faa2d $G/GroupContinuityCheck/GroupControls.cs
file 3ddc9012002110f251d2b796684384c9f7da129b454002645be9736642698750 $G/GroupContinuityCheck/Commands.cs
file fd81d359577bae1883de4abbda534129f03f2f584fb4c3b2008c384c3c6cea0a $G/GroupContinuityCheck/Verdict.cs
file a6362c1cce17e17c5ff077e487bc3a38bf693fd7824e329b7d87a199116eeaf4 $G/GroupLayoutSpike/GroupLayoutSpike.csproj
file 0fb2a594abfc17d29c59f750f685ec7287ff6966713034710b59bb0ceffe2500 $G/GroupLayoutSpike/Program.cs
file a305900212b74dfb29b26257cf4d985430e042811ecd9185e85d14d3187408f1 $G/Dockerfile
file aaf9c0b6ca3eab93734a0829d6f8c31a3d53a6b98faa312da11a62dbb72c8e0b $G/Dockerfile.account
file 10267bf9cbb61eae20883470c08cb01b20dbbac08e6bf2921cd4f76ca2ba93ae $G/results/fixtures.sha256
file bae19f3be00cf9e8bab63f21fa3a98639c05281b90c1bed0a8767ed261de6a3a $G/results/summary.json
file 6446e9e1324dfdec6b06c0a973a2f1a618e9b3de3016f64b03f6c7b29e2c1827 $G/results/groups.json
file ca3b1a9c3e788dd1caafb069c61f880b1e7eac88e36d8e5978d5dde47015239d $G/results/accounting.json
file 9fb1c65081e2f28e6004ceb3491110753491563ebfd99220fda0e53d590e8b73 $G/results/integrity.json
file a8f45e98be155d103c20d6f810378a496d846d47011a04b18a93a7afd70f4ffe $G/results/runs.csv
A=spikes/06-pdf-assembly-chromium-parts
file a8f73d1b585fdefe6ac6f9f1534b6ee3212bb78e16c9f5b396653fb324bdd397 $A/Dockerfile
file a1e2fcdd8da0e58c99700adda1863749b49a31e1171f1f0dcf35849198583cdb $A/Dockerfile.dockerignore
file f4680102566ae9b8da5c5ed1db054f05e8472da2efb5a4914d0431404012f1cc $A/fixtures/pdfsharp-part.pdf
file 53a97739cee0007a7040e56e2a6eae01b9097d4f24b4e5a5c0cf697b0b1d30af $A/PdfAssemblySpike/PdfAssemblySpike.csproj
file 6c19eac50a7bccc35cdfe88ce6214766956b264a97ac72e237c1ad53907c369b $A/PdfAssemblySpike/Directory.Packages.props
file 80ebdd97c90d96479c3d15ddef66a1c400aeca32c16da8fd2b4d7e857d3d4b21 $A/PdfAssemblySpike/Assembler.cs
file 8b4e186c8dd952af1d3ea1cbdfde93bcce853563eedb665f4b1f00671879acfc $A/PdfAssemblySpike/ChromiumInputs.cs
file 9dc31f2f559b7f518181ed65cf968e83423cfbeeed4468050fc47209c21dee02 $A/PdfAssemblySpike/DejaVuFontResolver.cs
file edb7f0d3b2eb5d449ebabc0d88ecd5b1b219e28f168447a6d9cff54a60e3bb85 $A/PdfAssemblySpike/ExternalTool.cs
file 92cb31d1bbfeeef3c6eca59c0f54839bf9917bdf26844a5f644cb242b9c4b703 $A/PdfAssemblySpike/MatrixRunner.cs
file 48b177f13e7ffd5f317e86c325950486c2636f8cfadbb03f6c84380b08caf157 $A/PdfAssemblySpike/MemorySampler.cs
file bd73d82468e1e129504e1baa8ddd893d940cd6421ab412329a715138fedf3c03 $A/PdfAssemblySpike/PageBoxes.cs
file 67fe75aed01d203c4d4415a416dc55c21153dec0044cca35c304d9f189f5d617 $A/PdfAssemblySpike/PageLayout.cs
file 2f9f6ef7c78b5b63911480da57c476b7cfaa5f16dfbb30e3888766035853de2e $A/PdfAssemblySpike/PageStamper.cs
file 30fb317754c931481c25a5bcbeacd6fac4eabedb4269e76639980e3c830f03b6 $A/PdfAssemblySpike/PageWords.cs
file f97560e3fd9c4787ed6c9d4d0024b06b963f8078827e42034e022f181f96b777 $A/PdfAssemblySpike/PgmImage.cs
file fd0611b011b0a4985e3aeae9843dbb4d47bb8ab2cb6094b2d072e59790176723 $A/PdfAssemblySpike/Probe.cs
file 920996acbb4a84b8ee8ab99c0206370d78d162a9ddd420c64a34fc0741be0a8a $A/PdfAssemblySpike/Program.cs
file 5aa62010f7b3ac77cebf00185f97c5e6fe2cb812517e5c24ea1cb4c0465446f2 $A/PdfAssemblySpike/QpdfCheck.cs
file 32b26d134c611bd423b8c4fbc33306af6c87afa4077cb7327c9e2cfdfaffb3f9 $A/PdfAssemblySpike/ResultsWriter.cs
file 2c56e9539e9f59e1e182a71edbc2d22b9c84e9594c987bc88ac438840d2b62f8 $A/PdfAssemblySpike/SetManifest.cs
file 86d276fc698790e5e8701e019d1cdb59c0fdf6a2a399f0e461c7183686bde3dc $A/PdfAssemblySpike/SplitMix64.cs
file b86c953d746cff1fb64bd83a1bce2375e45eec193930206d814607795686870f $A/PdfAssemblySpike/TimeOutput.cs
file 660b03b676a3ef1b079c2c7e650e330ce3607e2b5072901d6202b14f157ec874 $A/PdfAssemblySpike/Verifier.cs
file fec14c3ffe1d04119de529c13289a4007513ca10960d3610d56b645e51395b04 $A/results/summary.json
file 83f6c458cb30265ea8cf2b2ab3d61641467701b6984c03f2359cc747031445c1 spikes/02-pdf-layout/PdfLayoutSpike/assets/paged-layout.js
file db1ce1a7f0dcab7571f84aec2f437e94d8b976c46d3421269cb2c20e3d57c52c spikes/09-two-pass-page-subtotals/results/raw/fixtures/SHA256SUMS
