#!/bin/sh
# Prints one line per reused file and per piece of reused evidence: expected
# SHA-256, actual SHA-256, path. Run on the host from the repository root and
# write the output to spikes/14-group-rules-feasibility/results/identity.txt. Every
# line must show two equal hashes. First the whole identity list of
# spikes/12-group-continuity, run as it is (itself the list of
# spikes/10-pagedjs-table-strict-accounting — the files of spikes/02, 03, 07, 08
# and 09 and the committed results of spikes/08-pagedjs-pre-split — then that
# folder's linked and copied files and the grouped dataset); then the files of
# spikes/12-group-continuity this experiment links, copies from or reads, and that
# folder's committed results used as pins, anchors and known answers. Nothing
# listed here is ever written by this experiment.
set -eu

file() { # expected path
    printf '%s %s %s\n' "$1" "$(sha256sum < "$2" | cut -c1-64)" "$2"
}

G=spikes/12-group-continuity
file f214e20f5f09f3b4353293b02c4fc89a0ae3aa8a678d38f3446149d29d968cf3 $G/check-identity.sh
sh $G/check-identity.sh
file 3adf6bd0ed236aef17323a2e6a6d263866f287851256f372b5edfea8845d117b $G/GroupContinuityCheck/GroupCheck.cs
file 000a56b1ac2b9ccdae9a6985ffff71f81c8715e4d8c342b8ed7f9bab5f192544 $G/GroupContinuityCheck/GroupFixtures.cs
file 9db03f46c3eaf93624d764594fd23049816619339bf83b5b3ab1840b1b8faa2d $G/GroupContinuityCheck/GroupControls.cs
file 3ddc9012002110f251d2b796684384c9f7da129b454002645be9736642698750 $G/GroupContinuityCheck/Commands.cs
file fd81d359577bae1883de4abbda534129f03f2f584fb4c3b2008c384c3c6cea0a $G/GroupContinuityCheck/Verdict.cs
file e4c5cae63a600ebddabb0efde3e2a260937c45e924bb98c815ea0a4ca33cfd9b $G/GroupContinuityCheck/Program.cs
file cec484f5945d691aaf5a85e2963223772c29c8b7ccd7b0bddab89f4d6ffd34ad $G/GroupContinuityCheck/GroupContinuityCheck.csproj
file c4f4f945aaef3dcf81ed1235224de7fbb8612b37f4d29618c2d4e44e9861952f $G/GroupLayoutSpike/Plan.cs
file 0fb2a594abfc17d29c59f750f685ec7287ff6966713034710b59bb0ceffe2500 $G/GroupLayoutSpike/Program.cs
file a6362c1cce17e17c5ff077e487bc3a38bf693fd7824e329b7d87a199116eeaf4 $G/GroupLayoutSpike/GroupLayoutSpike.csproj
file 75e7ef114e8e062ed0140b7f44c9afd0895b726f097090b9cfb8687d9ca03220 $G/Directory.Packages.props
file a305900212b74dfb29b26257cf4d985430e042811ecd9185e85d14d3187408f1 $G/Dockerfile
file aaf9c0b6ca3eab93734a0829d6f8c31a3d53a6b98faa312da11a62dbb72c8e0b $G/Dockerfile.account
file d57760b4eeef380442a515fe93bd3857b310bdfafe4e58c0d26c14bfb9aeab25 $G/Dockerfile.dockerignore
file 4b906b9bc69b6f60d10aaaf4b9c01a669d9e67cd44b1dc2e51173b3cb6d4642f $G/Dockerfile.account.dockerignore
file 10267bf9cbb61eae20883470c08cb01b20dbbac08e6bf2921cd4f76ca2ba93ae $G/results/fixtures.sha256
file 599df92c1be034388c9b69ff572c575bb49e7682aad040b3ee5594752d479a43 $G/results/group-fixtures.json
file 6c434408570193bca761535884d584f6990365543e4d74eb635bcda4590aad3e $G/results/plan.json
file ca3b1a9c3e788dd1caafb069c61f880b1e7eac88e36d8e5978d5dde47015239d $G/results/accounting.json
file 9fb1c65081e2f28e6004ceb3491110753491563ebfd99220fda0e53d590e8b73 $G/results/integrity.json
file 6446e9e1324dfdec6b06c0a973a2f1a618e9b3de3016f64b03f6c7b29e2c1827 $G/results/groups.json
file c85a30a6ec92165fba872e27f1c4e1747d8d682b50fdc5138d57efb7347ea1da $G/results/controls.json
file bae19f3be00cf9e8bab63f21fa3a98639c05281b90c1bed0a8767ed261de6a3a $G/results/summary.json
file a8f45e98be155d103c20d6f810378a496d846d47011a04b18a93a7afd70f4ffe $G/results/runs.csv
