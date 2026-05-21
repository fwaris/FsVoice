#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT_DIR/src/Speak2Docs/Speak2Docs.fsproj"

if [ -f "$ROOT_DIR/build/release/release.env" ]; then
  set -a
  . "$ROOT_DIR/build/release/release.env"
  set +a
fi

: "${APP_TITLE:?Set APP_TITLE in release.env or the environment.}"
: "${APP_ID:?Set APP_ID in release.env or the environment.}"
: "${APP_VERSION:?Set APP_VERSION in release.env or the environment.}"
: "${APP_BUILD:?Set APP_BUILD in release.env or the environment.}"
: "${APPLE_TEAM_ID:?Set APPLE_TEAM_ID in release.env or the environment.}"
: "${IOS_CODESIGN_KEY:?Set IOS_CODESIGN_KEY in release.env or the environment.}"
: "${IOS_CODESIGN_PROVISION:?Set IOS_CODESIGN_PROVISION in release.env or the environment.}"

IOS_RUNTIME_IDENTIFIER="${IOS_RUNTIME_IDENTIFIER:-ios-arm64}"

dotnet clean "$PROJECT" \
  -f net10.0-ios \
  -c Release \
  -r "$IOS_RUNTIME_IDENTIFIER" \
  -v:minimal

dotnet publish "$PROJECT" \
  -f net10.0-ios \
  -c Release \
  -r "$IOS_RUNTIME_IDENTIFIER" \
  -p:ArchiveOnBuild=true \
  -p:BuildIpa=true \
  -p:ApplicationTitle="$APP_TITLE" \
  -p:ApplicationId="$APP_ID" \
  -p:ApplicationDisplayVersion="$APP_VERSION" \
  -p:ApplicationVersion="$APP_BUILD" \
  -p:CodesignTeamId="$APPLE_TEAM_ID" \
  -p:CodesignKey="$IOS_CODESIGN_KEY" \
  -p:CodesignProvision="$IOS_CODESIGN_PROVISION"
