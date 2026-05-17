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
: "${ANDROID_KEYSTORE:?Set ANDROID_KEYSTORE in release.env or the environment.}"
: "${ANDROID_KEY_ALIAS:?Set ANDROID_KEY_ALIAS in release.env or the environment.}"
: "${ANDROID_KEYSTORE_PASSWORD:?Set ANDROID_KEYSTORE_PASSWORD in release.env or the environment.}"
: "${ANDROID_KEY_PASSWORD:?Set ANDROID_KEY_PASSWORD in release.env or the environment.}"

dotnet publish "$PROJECT" \
  -f net10.0-android \
  -c Release \
  -p:AndroidPackageFormat=aab \
  -p:ApplicationTitle="$APP_TITLE" \
  -p:ApplicationId="$APP_ID" \
  -p:ApplicationDisplayVersion="$APP_VERSION" \
  -p:ApplicationVersion="$APP_BUILD" \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore="$ANDROID_KEYSTORE" \
  -p:AndroidSigningKeyAlias="$ANDROID_KEY_ALIAS" \
  -p:AndroidSigningStorePass="$ANDROID_KEYSTORE_PASSWORD" \
  -p:AndroidSigningKeyPass="$ANDROID_KEY_PASSWORD"
