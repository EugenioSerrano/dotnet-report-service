#!/bin/sh
# Prints one line per reused file: expected SHA-256, actual SHA-256, path.
# Run from the repository root; write the output to
# spikes/09-two-pass-page-subtotals/results/identity.txt. Every line must show
# two equal hashes: the shared fixtures, the text-integrity check and its image
# definition of spikes/07-native-tall-rows, the files linked from
# spikes/02-pdf-layout and spikes/03-pdf-assembly (working file and the bytes at
# the commit they were published in), and the copied result-type declarations.
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

N=spikes/07-native-tall-rows
file c2cfaf0a6a46cebf18ff745ed0d4dd7b3e8bf0a22d2d7a832e48c560dbf7a422 $N/PaginationFixtures/ControlMutations.cs
file 10ff2f88ccadf5d976b52e12c807862aa52377ad7dc86bed4756dc09379cd33e $N/PaginationFixtures/Dataset.cs
file 07c0e4e13e31b4d0882b0eb6d230737dfafdce73eb11e03d69935f1716aaf5ef $N/PaginationFixtures/Fixture.cs
file 0fce2a4ee253b8b73f71370580cebacd4a57f20357e75b4ce83de9d55c41acb5 $N/PaginationFixtures/FixtureFile.cs
file 0fa81e0d3d94c5749e4b69b03a704b901718b6764f6796a69195c6dd04145e4a $N/PaginationFixtures/FixtureSeries.cs
file 52c63cdcf53e351af7759f79d3a01213015bb0f1942ea87b4816b3adf5b42155 $N/PaginationFixtures/PaginationFixtures.csproj
file 714bb1536ec527834af6001835e6e837e8362c5d1d87a3c8ef067d3b87e8552c $N/PaginationFixtures/UniqueText.cs
file 3a04059f832bd2214ef9092ef10420b9e0833878c5589edb91c635bd8c1fd2f9 $N/TextIntegrityCheck/Commands.cs
file 5dce94f5b75e5e5348410d086b1d7dd5d81584a3d390d3007a1652b2d61a15e6 $N/TextIntegrityCheck/ExtractionControls.cs
file 3402e60d24278573e778f6eb53c34112ddadc3fadf75b85c007d5f6a98cd923b $N/TextIntegrityCheck/IntegrityRules.cs
file 3848a6687ca031feff07d2a9680e692e7b6b485eee53eec9bc38ab23a55f7943 $N/TextIntegrityCheck/Poppler.cs
file c15e2d260c96eb810149b2269cc07f79628b2ad98bdaa855434c00702f52a635 $N/TextIntegrityCheck/PrintedDocument.cs
file 079a270736abff31ce6717ed4693677331b024227f5731b65dfcbed6de52a8d7 $N/TextIntegrityCheck/Program.cs
file e2e61e9ff9b5fe21d26cbeeede85bb4144bcd2791084870a1fc826ec6a097834 $N/TextIntegrityCheck/RunRecords.cs
file 3c16a9c9d429115b63017bbf667f14d700b4ec834ebf19092a58b540f9a0cd79 $N/TextIntegrityCheck/TextIntegrityCheck.csproj
file b1d3a41309a96a6a8c7791408d3a5d6d3d46f500fde02470273197813fde998b $N/TextIntegrityCheck/TextNormalisation.cs
file 8cd9f360311da3102ff503d4066aa4010b5fc03f4338f93fd938831c768b1ea7 $N/TextIntegrityCheck/VerdictCommand.cs
file 2f75b899c5f6b39cd8b8075a797c07f7083156d3bcfd917d623c79ebd0d81d85 $N/Dockerfile.check
file a47e928c9fcd1bc652e927689cc45a04bf7678709ad3c609d17b325483d062b2 $N/Dockerfile.check.dockerignore
file c1576a4ec8b335bc9a7525dbe762933f7079b1a48882ea06b1fffe7d67190316 $N/results/fixtures.sha256
file 81b0ea417774d33824bdd419b08d9c8a5b37e165e97a251f080a83964f6d6884 $N/results/integrity.json

L=spikes/02-pdf-layout/PdfLayoutSpike
A=spikes/03-pdf-assembly/PdfAssemblySpike
file f1a5d47bf9fcaecdbb56c49f4a0f5e6e709fb10ca5ac05d33f41001f6a9d0ea5 $L/AccountingVerifier.cs
at_commit f1a5d47bf9fcaecdbb56c49f4a0f5e6e709fb10ca5ac05d33f41001f6a9d0ea5 eebc9ba $L/AccountingVerifier.cs
file 970521e6119622d7059b5b0fdbcd8c4cda2751c6cf478c502132cfc4c60f9456 $L/BlockSource.cs
at_commit 970521e6119622d7059b5b0fdbcd8c4cda2751c6cf478c502132cfc4c60f9456 eebc9ba $L/BlockSource.cs
file 0da4b5d0171de290834f36af043aef4a1042caae0399532f48a473956456845b $L/ProcessTreeSampler.cs
at_commit 0da4b5d0171de290834f36af043aef4a1042caae0399532f48a473956456845b eebc9ba $L/ProcessTreeSampler.cs
file cf6b6ad648ab2a08a447aa814fe0ee5acf5dc2320e88e66ca0337467842123eb $A/PgmImage.cs
at_commit cf6b6ad648ab2a08a447aa814fe0ee5acf5dc2320e88e66ca0337467842123eb f88b9f0 $A/PgmImage.cs

# The result-type declarations: lines 10–179 of the layout runner, and the copy's body.
T=b2100c86017ae746f1f811ddb850c39e26d590475bb088c5e7cdee07f278d310
line $T "$(sed -n '10,179p' $L/LayoutRunner.cs | sha256sum | cut -c1-64)" "$L/LayoutRunner.cs:10-179"
line $T "$(git show eebc9ba:$L/LayoutRunner.cs | sed -n '10,179p' | sha256sum | cut -c1-64)" "$L/LayoutRunner.cs:10-179@eebc9ba"
line $T "$(tail -n +3 spikes/09-two-pass-page-subtotals/PageAccountingCheck/Reused/LayoutTypes.cs | sha256sum | cut -c1-64)" "spikes/09-two-pass-page-subtotals/PageAccountingCheck/Reused/LayoutTypes.cs:3-"
