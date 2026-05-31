# FsVoice

FsVoice is an F#/.NET 10 workspace for building voice-first question answering systems. The repo currently centers on Speak2Docs, a .NET MAUI app that lets a user select document sources, build or import local retrieval indexes, connect to OpenAI realtime voice, and ask questions about the selected material.

The reusable pieces are split into small packages: typed voice orchestration contracts, RTFlow and ASP.NET hosting adapters, QA/plugin contracts, retrieval and PDF processing, OpenAI Responses helpers, CLI utilities, and tests.

## Current App: Speak2Docs

Speak2Docs is the primary product built from this repo.

- Realtime voice uses OpenAI Realtime over WebRTC. The app requests microphone permission when a realtime session starts.
- The user provides an OpenAI API key in Settings. The app does not create an account or ship with a shared key.
- Local source imports currently support PDF, Markdown, and zipped Speak2Docs/FsColbert index bundles. The retrieval layer and CLI also support JSON knowledge sources.
- The app ships a built-in sample FsColbert index for "AI on the Pulse: Real-Time Health Anomaly Detection with Wearable and Ambient Intelligence." Built-in sources can be hidden and restored.
- Document state is tracked as queued, processing, ready, or failed. Users can select ready sources, retry failed processing, delete sources, cancel processing, and preview ready indexes.
- Retrieval can use an internal document index or persisted FsColbert indexes with fallback.
- Retrieval behavior includes lexical filtering, local query cleanup, optional LLM query expansion, optional keyword metadata generation, and source-balancing for broad summaries or comparisons.
- PDF parsing can run in legacy mode or hybrid document-structure mode. Hybrid parsing can use layout analysis, platform PDF rasterizers, local ONNX layout models, and optional OCR model discovery.
- The realtime front-end routes substantive spoken questions through a backend oracle tool. The Ctx runtime then searches selected sources, tool observations, session blackboard context, and configured plugin context providers before producing the answer.
- Speak2Docs disables durable memory storage in the app flow, but the reusable Ctx runtime still includes durable memory services for other hosts.

The default model roles in source are:

- Realtime: `gpt-realtime-2`
- Transcriber: `gpt-4o-mini-transcribe`
- Answer: `gpt-5.5`
- Keyword generation: `gpt-5-nano`
- Query expansion: `gpt-5-nano`

All model role ids can be overridden by plugins and by app settings.

## Repository Layout

- `src/FsVoice.Platform`: public typed voice contracts: `VoiceConnection`, `IVoiceSession`, `IVoiceOrchestration`, host context, and host message codecs.
- `src/FsVoice.RTFlow`: adapter from an RTFlow workflow to the `IVoiceSession<'ToHost, 'FromHost>` contract.
- `src/FsVoice.Hosting.AspNetCore`: ASP.NET Core bridge session store and endpoints for browser/WebRTC-style hosts.
- `src/FsVoice.Core`: shared text helpers and generic async queue utilities.
- `src/FsResponses`: typed OpenAI Responses API and Responses WebSocket request/event models used by the Ctx runtime.
- `src/FsVoice.Ctx.Contracts`: context-backed answer contracts for sources, chunks, retrieval modes, sessions, plugins, tools, context providers, memory, and model roles.
- `src/FsVoice.Retrieval`: FsColbert retrieval, keyword enrichment, source loading, index preview, and hybrid PDF processing.
- `src/FsVoice.Ctx.Runtime`: oracle/context answer runtime, tool loading, blackboard, durable memory, plugin profiles, and answer transport.
- `src/FsVoice.Retrieval.PdfRasterization`: desktop, Android, iOS, and Mac Catalyst PDF rasterizers for hybrid PDF parsing.
- `src/FsVoice.Ctx.Tools`: reusable context tool providers. It currently includes a current-time provider.
- `src/FsVoice.Tools` and `src/FsVoice.PdfRasterization`: deprecated compatibility facades for older source names.
- `src/FsVoice.Cli`: command-line asking, index-bundle creation, and InsuranceQA evaluation utilities.
- `src/Speak2Docs.Orchestration`: platform-neutral Speak2Docs workflow, agents, runtime settings, plugin loading, and source model.
- `src/Speak2Docs`: .NET MAUI app for Android, iOS, and Mac Catalyst.
- `src/FsVoice.Tests`: tests for QA, retrieval, tools, hosting, orchestration, and app-adjacent behavior.
- `src/FsResponsesTest`: tests for the OpenAI Responses request and stream-event helpers.
- `data/qa-plug-ins`: sample QA plugin profiles, currently including `insuranceqa.json`.
- `docs`: public support, settings, privacy, terms, third-party notices, release notes, and store metadata.
- `build/release`: local release scripts and signing environment templates for Android, iOS, and Mac Catalyst.

## QA Plugins And Tools

Ctx plugins implement `IQaPlugIn` from `FsVoice.Ctx.Contracts`. A plugin supplies a `PlugInDefinition` with:

- behavior profile, voice replacements, query expansion rules, keyword hints, and answer instructions;
- prompt templates for realtime, transcription, answers, keywords, and spoken oracle results;
- model role defaults for realtime, transcription, answers, keywords, and query expansion;
- runtime defaults such as retrieval mode, query expansion, keyword elaboration, lexical filtering, context limits, timeouts, and writeback behavior;
- optional settings fields shown by hosts;
- optional QA tool providers and context providers.

Speak2Docs loads plugins from bundled assemblies and, where supported, from `AppData/plug-ins/*.dll`. Folder-loaded plugins are disabled on iOS. If no selected plugin is available, the host falls back to the built-in Generic QA plugin.

The Ctx runtime always includes built-in tools for selected-source search, source inventory, durable-memory search, and session blackboard search. Additional tools can come from a plugin, from a configured tool-provider directory, or from packages such as `FsVoice.Ctx.Tools`.

## Source Indexes And Storage

Speak2Docs stores its document library, copied sources, imported bundles, persisted FsColbert indexes, hidden built-in source list, settings, and keyword caches in app-owned storage.

FsColbert indexes are persisted as `.fsci` files with metadata fingerprints so source changes, parser changes, plugin profile changes, and keyword settings can trigger rebuilding when needed. Index bundles use an `index-bundle.json` manifest plus `documents/` and `indexes/` files. The app can import zipped bundles, and the CLI can create either a directory bundle or a `.zip`.

Deleting a user source removes the app library entry and clears persisted FsColbert indexes. Deleting a built-in source hides it until the user restores built-in indexes.

## Build

Install the .NET 10 SDK first. Building the MAUI app also requires the relevant MAUI workloads and platform toolchains.

Restore packages:

```bash
dotnet restore FsVoice.slnx
```

Build the core/testable projects without requiring a full MAUI app build:

```bash
dotnet build src/FsVoice.Tests/FsVoice.Tests.fsproj --no-restore
dotnet build src/FsResponsesTest/FsResponsesTest.fsproj --no-restore
dotnet build src/FsVoice.Cli/FsVoice.Cli.fsproj --no-restore
```

Build the full solution when MAUI workloads are installed:

```bash
dotnet build FsVoice.slnx --no-restore
```

Build Speak2Docs targets:

```bash
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-android --no-restore --nologo
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-ios --no-restore --nologo
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-maccatalyst -r maccatalyst-arm64 --no-restore --nologo
```

Android requires minSdk 24 because ONNX Runtime's Android package declares that minimum.

## Test

Run the non-live test suites:

```bash
dotnet test src/FsVoice.Tests/FsVoice.Tests.fsproj
dotnet test src/FsResponsesTest/FsResponsesTest.fsproj
```

Some `FsResponsesTest` tests are live OpenAI smoke tests and are skipped by default.

## CLI

The CLI reads `OPENAI_API_KEY` by default or accepts `--api-key`.

Ask over one or more sources:

```bash
dotnet run --project src/FsVoice.Cli/FsVoice.Cli.fsproj -- ask \
  --question "Summarize the abstract" \
  --source path/to/paper.pdf
```

Create an index bundle from a folder of PDF, Markdown, or JSON sources:

```bash
dotnet run --project src/FsVoice.Cli/FsVoice.Cli.fsproj -- index-folder \
  --input docs \
  --output bundle.zip \
  --bundle-id my-docs \
  --index-keywords
```

Useful CLI options include:

- `--retrieval internal|fscolbert`
- `--answer-model`
- `--small-model`
- `--storage-root`
- `--plug-in-profile`
- `--json` for structured `ask` output
- `--layout-model heron|pp-doclayout-m` for `index-folder`
- `--layout-plugin` and `--layout-plugin-type` for trusted custom layout providers

The CLI also includes `insuranceqa-eval`, `insuranceqa-search-eval`, and `insuranceqa-elaborate-index` workflows for retrieval and answer-quality experiments.

## Configuration

Important Speak2Docs settings include:

- OpenAI API key;
- active QA plugin;
- model role overrides;
- max answer output tokens;
- retrieval mode;
- lexical filtering;
- keyword index elaboration;
- hybrid PDF parsing;
- layout analysis;
- activity log verbosity;
- plugin-specific settings.

The app persists settings with MAUI preferences and app-owned files. Avoid committing API keys, generated release packages, signing files, imported private documents, or local app data.

## Release

Release scripts live under `build/release`:

- `publish-android.sh` builds a signed Android App Bundle.
- `publish-ios.sh` builds an iOS App Store package.
- `publish-maccatalyst.sh` builds a Mac Catalyst app.
- `release.env.example` documents required local environment variables.
- `android-signing.properties.example` and `ios-export-options.template.plist` are templates only.

See `docs/release.md` for release checklist details. Do not commit keystores, provisioning profiles, certificates, App Store Connect keys, Google Play service-account JSON, OpenAI reviewer keys, or generated `.ipa`, `.aab`, and `.apk` files.

## Public Docs

- Support index: `docs/index.md`
- Settings help: `docs/fsvoice/settings.md`
- Terms: `docs/fsvoice/terms.md`
- Privacy policy: `docs/fsvoice/privacy.md`
- Third-party notices: `docs/fsvoice/third-party-notices.md`
- Store listing draft: `docs/store-listing.md`
- Store privacy notes: `docs/store-privacy.md`

## Licensing

FsVoice source code is licensed under MIT.

`FsVoice.Retrieval` is packaged as `MIT AND Apache-2.0` because it embeds the `PP-DocLayout-M` ONNX model. See `src/FsVoice.Retrieval/PackageNotices/PP-DocLayout-M-NOTICE.md` for model attribution and checksum details.

Speak2Docs also includes a built-in sample document and local FsColbert index for an arXiv paper licensed under Creative Commons Attribution 4.0. See `src/Speak2Docs/Resources/Raw/FsColbertIndexes/NOTICE.md`.
