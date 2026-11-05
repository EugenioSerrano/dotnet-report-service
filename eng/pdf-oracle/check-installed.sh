#!/usr/bin/env bash
# The licence control of the oracle image (ADR-013 C3), run by the build after the
# install. It reads the packages the install added or changed, stops when one of
# them is not in the owner's accepted list or has no copyright file, and records
# the version, the copyright file's SHA-256 and the licence names it declares into
# the evidence folder the image keeps. Nothing is shipped from this image.
set -euo pipefail

before=$1
accepted=$2
evidence=$3

after=$(mktemp)
dpkg-query -W -f='${Package} ${Version}\n' >"$after"

# A package whose version changed is inside the set, as the acceptance basis says.
changed=$({ diff "$before" "$after" || true; } | sed -n 's/^> //p' | cut -d' ' -f1 | sort -u)
if [ -z "$changed" ]; then
    echo "The install added no package; C3 has nothing to record." >&2
    exit 1
fi

mkdir -p "$evidence"
: >"$evidence/packages.txt"
status=0
for package in $changed; do
    if ! grep -qx "$package" "$accepted"; then
        echo "C3: the install added '$package', which the owner has not accepted." >&2
        status=1
        continue
    fi

    copyright="/usr/share/doc/$package/copyright"
    if [ ! -f "$copyright" ]; then
        echo "C3: '$package' has no copyright file." >&2
        status=1
        continue
    fi

    version=$(dpkg-query -W -f='${Version}' "$package")
    hash=$(sha256sum "$copyright" | cut -d' ' -f1)
    licences=$(sed -n 's/^License: *//p' "$copyright" | sort -u | paste -sd',' -)
    printf '%s %s %s %s\n' "$package" "$version" "$hash" "$licences" >>"$evidence/packages.txt"
done

rm -f "$after"
if [ "$status" -ne 0 ]; then
    exit 1
fi

echo "C3: $(wc -l <"$evidence/packages.txt") package(s) added or changed, every one accepted."
