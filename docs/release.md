# FsVoice Demo Release Guide

This app can be released from a public repo without committing signing material. Keep certificates, provisioning profiles, keystores, service-account files, reviewer keys, and generated store packages outside git.

## Public Artifacts

- `build/release/release.env.example` documents the environment variables used by local release scripts.
- `build/release/publish-ios.sh` builds an iOS App Store package using environment-provided signing values.
- `build/release/publish-android.sh` builds a signed Android App Bundle using environment-provided signing values.
- `build/release/ios-export-options.template.plist` is a template for App Store Connect export/upload automation.
- `build/release/android-signing.properties.example` is a local signing file template.

## Private Inputs

Never commit these files or values:

- Apple `.p12` certificates, private keys, and provisioning profiles.
- App Store Connect API `.p8` keys.
- Android `.jks` or `.keystore` files and passwords.
- Google Play service-account JSON files.
- OpenAI API keys or reviewer test keys.
- Generated `.ipa`, `.aab`, or `.apk` files.

Use a local untracked `build/release/release.env` file or CI secrets with these variables:

```text
APP_TITLE
APP_ID
APP_VERSION
APP_BUILD
APPLE_TEAM_ID
IOS_CODESIGN_KEY
IOS_CODESIGN_PROVISION
ANDROID_KEYSTORE
ANDROID_KEY_ALIAS
ANDROID_KEYSTORE_PASSWORD
ANDROID_KEY_PASSWORD
```

## iOS

1. Register the final bundle ID `com.fsvoice.FsVoice.Demo` in Apple Developer.
2. Create an App Store provisioning profile for that bundle ID.
3. Copy `build/release/release.env.example` to `build/release/release.env` and fill in local values.
4. Run:

```bash
./build/release/publish-ios.sh
```

The Release iOS configuration disables the Mono interpreter, enables AOT, and disables assembly IL stripping because the current toolchain crashes stripping `FSharp.Core.dll`.

Before App Review, provide reviewer instructions and a test OpenAI API key or another reviewer-access path.

## Android

1. Create a Play Console app with the final package name.
2. Create or configure a Play upload keystore.
3. Copy `build/release/release.env.example` to `build/release/release.env` and fill in local values.
4. Run:

```bash
./build/release/publish-android.sh
```

The Android manifest disables app backup and declares microphone hardware as optional. The app still requests microphone permission at runtime when the realtime voice session starts.

## Store Console Checklist

- Privacy policy URL is published and reachable at `https://fwaris.github.io/docs/fsvoice/privacy.html`.
- Third-party software license information is published and reachable at `https://fwaris.github.io/docs/fsvoice/third-party-notices.html`.
- Apple App Privacy answers match `docs/store-privacy.md`.
- Google Play Data safety answers match `docs/store-privacy.md`.
- Reviewer instructions explain that users provide their own OpenAI API key, or include a temporary reviewer key.
- App icon, splash screen, screenshots, description, support URL, and contact email are final.
- Version/build numbers are incremented before every upload.
