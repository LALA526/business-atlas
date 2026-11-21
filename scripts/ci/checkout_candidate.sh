#!/usr/bin/env bash
set -euo pipefail
# CI starts from a clean synthetic merge checkout. Verify the reviewed source itself.
candidate=${SYSTEM_PULLREQUEST_SOURCECOMMITID:-}
if [[ -z "$candidate" ]]; then exit 0; fi
[[ "$candidate" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid reviewed candidate SHA' >&2; exit 1; }
git diff --quiet HEAD -- || { echo 'Refuse to replace modified tracked source' >&2; exit 1; }
git cat-file -e "$candidate^{commit}"
git checkout --detach "$candidate"
[[ "$(git rev-parse HEAD)" == "$candidate" ]]
