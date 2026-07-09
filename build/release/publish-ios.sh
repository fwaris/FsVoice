#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT_DIR/src/Speak2Docs/Speak2Docs.fsproj"

ENV_APP_TITLE="${APP_TITLE-}"
ENV_APP_ID="${APP_ID-}"
ENV_APP_VERSION="${APP_VERSION-}"
ENV_APP_BUILD="${APP_BUILD-}"
ENV_APPLE_TEAM_ID="${APPLE_TEAM_ID-}"
ENV_IOS_CODESIGN_KEY="${IOS_CODESIGN_KEY-}"
ENV_IOS_CODESIGN_PROVISION="${IOS_CODESIGN_PROVISION-}"
ENV_IOS_RUNTIME_IDENTIFIER="${IOS_RUNTIME_IDENTIFIER-}"

if [ -f "$ROOT_DIR/build/release/release.env" ]; then
  set -a
  . "$ROOT_DIR/build/release/release.env"
  set +a
fi

[ -n "$ENV_APP_TITLE" ] && APP_TITLE="$ENV_APP_TITLE"
[ -n "$ENV_APP_ID" ] && APP_ID="$ENV_APP_ID"
[ -n "$ENV_APP_VERSION" ] && APP_VERSION="$ENV_APP_VERSION"
[ -n "$ENV_APP_BUILD" ] && APP_BUILD="$ENV_APP_BUILD"
[ -n "$ENV_APPLE_TEAM_ID" ] && APPLE_TEAM_ID="$ENV_APPLE_TEAM_ID"
[ -n "$ENV_IOS_CODESIGN_KEY" ] && IOS_CODESIGN_KEY="$ENV_IOS_CODESIGN_KEY"
[ -n "$ENV_IOS_CODESIGN_PROVISION" ] && IOS_CODESIGN_PROVISION="$ENV_IOS_CODESIGN_PROVISION"
[ -n "$ENV_IOS_RUNTIME_IDENTIFIER" ] && IOS_RUNTIME_IDENTIFIER="$ENV_IOS_RUNTIME_IDENTIFIER"

: "${APP_TITLE:?Set APP_TITLE in release.env or the environment.}"
: "${APP_ID:?Set APP_ID in release.env or the environment.}"
: "${APP_VERSION:?Set APP_VERSION in release.env or the environment.}"
: "${APP_BUILD:?Set APP_BUILD in release.env or the environment.}"
: "${APPLE_TEAM_ID:?Set APPLE_TEAM_ID in release.env or the environment.}"
: "${IOS_CODESIGN_KEY:?Set IOS_CODESIGN_KEY in release.env or the environment.}"
: "${IOS_CODESIGN_PROVISION:?Set IOS_CODESIGN_PROVISION in release.env or the environment.}"

IOS_RUNTIME_IDENTIFIER="${IOS_RUNTIME_IDENTIFIER:-ios-arm64}"

dotnet restore "$PROJECT" \
  -r "$IOS_RUNTIME_IDENTIFIER" \
  -v:minimal

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
