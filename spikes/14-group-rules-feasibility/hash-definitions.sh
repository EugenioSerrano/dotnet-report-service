#!/bin/sh
# Prints the SHA-256 of the README section "Observation rules and classes" (from
# its heading to the next second-level heading, the heading included) and the
# section's line count. Run on the host from the repository root, before the
# measured stage (results/definitions-start.txt) and at the end
# (results/definitions.txt); the two outputs must be equal.
set -eu
R=spikes/14-group-rules-feasibility/README.md
S=$(awk '/^## Observation rules and classes$/{on=1} on && /^## / && !/^## Observation rules and classes$/{exit} on' "$R")
printf '%s %s lines\n' "$(printf '%s\n' "$S" | sha256sum | cut -c1-64)" "$(printf '%s\n' "$S" | wc -l | tr -d ' ')"
