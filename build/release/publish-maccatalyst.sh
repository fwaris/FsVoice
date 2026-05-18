#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT_DIR/src/FsVoiceDemo/FsVoiceDemo.fsproj"

if [ -f "$ROOT_DIR/build/release/release.env" ]; then
  set -a
  . "$ROOT_DIR/build/release/release.env"
  set +a
fi

: "${APP_TITLE:?Set APP_TITLE in release.env or the environment.}"
: "${APP_ID:?Set APP_ID in release.env or the environment.}"
: "${APP_VERSION:?Set APP_VERSION in release.env or the environment.}"
: "${APP_BUILD:?Set APP_BUILD in release.env or the environment.}"

MACCATALYST_RUNTIME_IDENTIFIER="${MACCATALYST_RUNTIME_IDENTIFIER:-maccatalyst-arm64}"

publish_args=(
  dotnet publish "$PROJECT"
  -f net10.0-maccatalyst
  -c Release
  -r "$MACCATALYST_RUNTIME_IDENTIFIER"
  -p:ApplicationTitle="$APP_TITLE"
  -p:ApplicationId="$APP_ID"
  -p:ApplicationDisplayVersion="$APP_VERSION"
  -p:ApplicationVersion="$APP_BUILD"
)

if [ -n "${APPLE_TEAM_ID:-}" ]; then
  publish_args+=(-p:CodesignTeamId="$APPLE_TEAM_ID")
fi

if [ -n "${MACCATALYST_CODESIGN_KEY:-}" ]; then
  publish_args+=(-p:CodesignKey="$MACCATALYST_CODESIGN_KEY")
fi

if [ -n "${MACCATALYST_CODESIGN_PROVISION:-}" ]; then
  publish_args+=(-p:CodesignProvision="$MACCATALYST_CODESIGN_PROVISION")
fi

"${publish_args[@]}"
