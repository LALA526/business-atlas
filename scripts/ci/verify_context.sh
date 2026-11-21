#!/usr/bin/env bash
# Shared CI identity and toolchain resolution; keep independent of checkout HEAD in PR builds.
atlas_verification_candidate() {
  local candidate=${SYSTEM_PULLREQUEST_SOURCECOMMITID:-}
  if [[ -z "$candidate" ]]; then
    candidate=$(git rev-parse HEAD)
  fi
  if [[ ! "$candidate" =~ ^[0-9a-f]{40}$ ]]; then
    printf 'AT-T02 candidate must be a full lowercase Git SHA: %s\n' "$candidate" >&2
    return 1
  fi
  local actual
  actual=$(git rev-parse HEAD) || return 1
  if [[ "$actual" != "$candidate" ]]; then
    printf 'AT-T02 checkout HEAD does not match reviewed candidate: actual=%s expected=%s\n' "$actual" "$candidate" >&2
    return 1
  fi
  printf '%s\n' "$candidate"
}

atlas_verification_python() {
  local python=${ATLAS_PYTHON:-python3}
  if ! command -v "$python" >/dev/null 2>&1; then
    printf 'AT-T02 Python interpreter is unavailable: %s\n' "$python" >&2
    return 1
  fi
  printf '%s\n' "$python"
}
