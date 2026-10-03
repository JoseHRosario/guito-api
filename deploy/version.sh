#!/usr/bin/env bash
# Issue #75: compute the build version string stamped into the API assembly.
# Output format: 0.2.1-beta.N+20261003T1422Z.1a2b3c4
#   • base semver — the last v* git tag, patch-bumped, plus a -beta pre-release
#     suffix: a tag v0.2.0 yields 0.2.1-beta.*; no tag yields 0.1.0-beta.*.
#     When HEAD IS the tag (release build), the plain tag version is used.
#   • .N — merged commits since that tag (per-repo counter, works locally and in CI).
#   • +timestamp.sha — UTC second-resolution timestamp (disambiguates same-day
#     builds) + short commit SHA. Build metadata, semver-compliant.
# Requires a checkout with tags and history (CI: checkout fetch-depth: 0).
# Never hand-edited in the repo: the version is a build artifact (issue #75).
set -euo pipefail
cd "$(dirname "$0")/.."

TAG=$(git describe --tags --match 'v*' --abbrev=0 2>/dev/null || true)
if [ -n "$TAG" ]; then
  BASE="${TAG#v}"
  MAJOR=${BASE%%.*}; REST=${BASE#*.}; MINOR=${REST%%.*}; PATCH=${REST#*.}
  if [ "$(git rev-parse HEAD)" = "$(git rev-list -n 1 "$TAG")" ]; then
    # Release build of a tagged commit: the plain tag version, no suffix.
    echo "$BASE"
    exit 0
  fi
  BASE_VERSION="$MAJOR.$MINOR.$((PATCH + 1))-beta"
  COMMITS=$(git rev-list --count "$TAG"..HEAD)
else
  BASE_VERSION="0.1.0-beta"
  COMMITS=$(git rev-list --count HEAD)
fi
TIMESTAMP=$(date -u +%Y%m%dT%H%M%SZ)
SHA=$(git rev-parse --short HEAD)
echo "${BASE_VERSION}.${COMMITS}+${TIMESTAMP}.${SHA}"
