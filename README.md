# FsVoice

FsVoice is an F#/.NET platform for constructing voice-enabled applications. The repository contains reusable voice runtime packages, plugin/tool contracts, event and session infrastructure, host bridge components, test helpers, and a .NET MAUI app. Question answering over selected sources is the current primary use case built on top of the platform.

The current mobile app lets a user add local PDF or Markdown sources, import zipped FsColbert index bundles, build or load local retrieval indexes, and ask questions by realtime voice through OpenAI models. The same platform pieces are intended to support other voice-enabled workflows with different plugins, tools, prompts, transports, and host experiences.

## What Is In This Repo

- `src/FsVoice.Abstractions`: shared plugin, tool, event, memory, and transport contracts.
- `src/FsVoice.Core`: runtime engine, event bus, tool dispatcher, and blackboard implementation.
- `src/FsVoice.Types`: host/session message types shared by app and bridge layers.
- `src/FsVoice.RTFlow`: RTFlow adapters for typed FsVoice orchestration sessions.
- `src/FsVoice.Hosting.AspNetCore`: ASP.NET bridge runtime for browser, WebRTC, and WebSocket hosts.
- `src/FsVoice.QA.Abstractions`: QA source, chunk, retrieval, tool, session, and plugin contracts.
- `src/FsVoice.QA`: retrieval, durable memory, QA orchestration, tools, FsColbert indexing, and hybrid PDF processing.
- `src/FsVoice.PdfRasterization`: platform-specific PDF rasterizers for hybrid PDF parsing.
- `src/FsVoice.Tools`: reusable QA tool providers, currently including a current-time provider.
- `src/FsVoice.Testing`: deterministic text runtime and fake transport helpers for tests.
- `src/FsVoice.Cli`: command-line QA, indexing, and evaluation utilities.
- `src/Speak2Docs.Orchestration`: platform-neutral mobile workflow and realtime/QA agents.
- `src/Speak2Docs`: .NET MAUI app for Android, iOS, and Mac Catalyst.
- `src/FsVoice.Tests`: xUnit test suite.

## Platform Capabilities

- Typed voice application contracts for plugins, tools, events, memory, and transports.
- Runtime engine, event bus, tool dispatcher, and blackboard primitives for voice workflows.
- RTFlow and ASP.NET bridge layers for composing voice sessions across app and browser/WebRTC hosts.
- Test/runtime helpers for deterministic text-mode and fake-transport workflows.
- Plugin-oriented configuration for prompts, model roles, runtime options, settings fields, tools, and context providers.

## QA Features

- Realtime voice session using `gpt-realtime-2` by default.
- Backend answer/oracle model using `gpt-5.5` by default.
- Separate model roles for realtime, transcription, answer generation, planning, keyword generation, and query expansion.
- Unified source picker for `.pdf`, `.md`, and `.zip` files.
- Local source library with ready, failed, retry, selected, and delete states.
- FsColbert semantic retrieval with persisted `.fsci` indexes and internal fallback retrieval.
- Import of zipped FsVoice/FsColbert index bundles.
- Optional keyword enrichment with `gpt-5-nano`.
- Hybrid PDF parsing with local layout analysis and OCR-related support.
- Index preview page for ready sources, showing random chunk samples, keywords, lexical terms, and vector summaries.
- Built-in and folder-loaded QA plugins with prompt, model, runtime, settings, tool, and context-provider hooks.
- Durable memory and tool-planning support in the QA backend.

## Plugins

QA plugins implement `IQaPlugIn` from `FsVoice.QA.Abstractions`. A QA plugin provides:

- a `PlugInDefinition` with prompts, model role defaults, runtime options, settings fields, and behavior profile;
- optional QA tool providers;
- optional context providers.

The app host scans bundled assemblies and, where supported, `AppData/plug-ins/*.dll`. It falls back to the built-in Generic QA plugin when no active plugin is available. The host then applies user overrides for model roles, retrieval mode, lexical filtering, keyword indexing, and plugin settings before starting the realtime and QA agents.

At the lower platform layer, `FsVoice.Abstractions` and `FsVoice.Core` define more general voice-plugin, tool, runtime, event, memory, and transport contracts that are not tied to document QA.

## Build

Restore packages first, then build the solution or a specific target:

```bash
dotnet restore FsVoice.slnx
dotnet build FsVoice.slnx --no-restore
```

MAUI app targets:

```bash
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-android --no-restore --nologo
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-ios --no-restore --nologo
dotnet build src/Speak2Docs/Speak2Docs.fsproj -f net10.0-maccatalyst -r maccatalyst-arm64 --no-restore --nologo
```

Android builds require minSdk 24 because ONNX Runtime's Android package declares that minimum. Some Android builds currently emit native-library page-size and duplicate WebRTC library warnings; these are non-fatal in the current debug build.

## Test

Run the main test suite with:

```bash
dotnet test src/FsVoice.Tests/FsVoice.Tests.fsproj
```

## CLI

`FsVoice.Cli` supports asking questions over sources, building FsColbert index bundles, and running InsuranceQA evaluation workflows.

```bash
dotnet run --project src/FsVoice.Cli/FsVoice.Cli.fsproj -- ask --question "Summarize the abstract" --source path/to/paper.pdf
dotnet run --project src/FsVoice.Cli/FsVoice.Cli.fsproj -- index-folder --input docs --output bundle.zip --bundle-id my-docs --index-keywords
```

The CLI reads `OPENAI_API_KEY` by default, or accepts `--api-key`.

## Configuration

In the MAUI app, open Settings and provide an OpenAI API key. Use a separate key for each app, device, automation, or experiment when practical, monitor usage in the OpenAI platform dashboard, and revoke keys that are no longer needed or may have been exposed.

Important settings include:

- model role overrides;
- retrieval mode: internal document index or FsColbert index with fallback;
- lexical filtering;
- index keyword generation;
- hybrid PDF parsing and layout analysis;
- plugin-specific settings.

The app stores source files, indexes, keyword cache, and settings in app-owned storage. Speak2Docs does not use durable memory storage. Document indexes are reused until source files or index-affecting settings require reprocessing.

## Documentation

- Platform component assembly: `docs/platform-component-assembly.md`
- Exploded component diagram: `diagrams/platform-component-assembly.mmd`
- Settings help: `docs/fsvoice/settings.html`
- Store listing draft: `docs/store-listing.md`
- Store privacy notes: `docs/store-privacy.md`
- Release guide: `docs/release.md`
- Package readmes live next to their projects under `src/`.

## Licensing

FsVoice source code is licensed under MIT. Packages that embed or redistribute third-party model assets include package-specific notices. `FsVoice.QA` embeds the `PP-DocLayout-M` ONNX model; see `src/FsVoice.QA/PackageNotices/PP-DocLayout-M-NOTICE.md` for attribution and checksum details.
