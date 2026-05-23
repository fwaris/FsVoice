# FsVoice Platform Component Assembly

FsVoice is a platform, not a single application. The platform supplies contracts, runtime plumbing, host adapters, QA building blocks, and test helpers. An application assembles those with a host shell, orchestration, model clients, plug-ins, tools, context providers, sources, storage, and runtime policy.

The exploded component diagram is in [../diagrams/platform-component-assembly.mmd](../diagrams/platform-component-assembly.mmd).

## Component Rings

| Ring | Supplied by FsVoice | Supplied by the application or developer |
| --- | --- | --- |
| Host shell | Typed session contracts, ASP.NET bridge, test/text runtime helpers | MAUI UI, browser UI, CLI, app settings, audio permissions, storage roots |
| Runtime | `FsVoice.Abstractions`, `FsVoice.Core`, event bus, blackboard, tool dispatcher, transport contracts | Concrete `IVoicePlugin`, host context, transport, runtime options |
| Typed orchestration | `FsVoice.Types`, `FsVoice.RTFlow` adapter | `IVoiceOrchestration<'ToHost,'FromHost>`, app-specific message types, agents, workflow state |
| QA | `FsVoice.QA.Abstractions`, `FsVoice.QA`, built-in source/memory tools, FsColbert provider, durable memory, PDF processing | `IQaPlugIn`, optional `IQaToolProvider`, optional `IQaContextProvider`, prompts, model roles, source policy |
| Integration | `FsVoice.Hosting.AspNetCore` bridge endpoints and session store | ASP.NET route registration, browser client, WebRTC signaling/event projection |

## Speak2Docs Assembly

Speak2Docs is the direct mobile/desktop assembly example.

1. The MAUI app owns UI, settings, audio permission, app storage, source picking, and connection lifetime.
2. `PlugInHost.loadActive` discovers bundled QA plug-ins and supported folder-loaded plug-ins, then falls back to the generic QA plug-in.
3. `PlugInComposer.withHostOverrides` combines plug-in defaults with host settings such as model overrides, retrieval mode, lexical filtering, and keyword indexing.
4. `DemoVoiceOrchestration` implements `IVoiceOrchestration<ToHost, FromHost>` for the app workflow.
5. `Connect.start` creates a `VoiceConnection` from local channels, connects those channels to the realtime WebRTC client, creates the orchestration session, and starts host/client/server pumps.
6. `StateMachine.create` starts `VoiceAgent`, `QaAgent`, and `HostAgent`.
7. `VoiceAgent` configures the realtime session and exposes the `QUERY_ORACLE` tool.
8. `QaAgent` creates model clients, builds a `QaSession`, creates the FsColbert context provider for selected sources, appends plug-in context providers, and passes plug-in tool providers into the QA runtime.
9. A substantive spoken turn flows through realtime tool calling into `QaSession.AnswerAsync`, then returns context, oracle answer text, logs, and speech output to the host.

The main assembly points are:

- `src/Speak2Docs/Interaction/Update.fs`: app model to orchestration options.
- `src/Speak2Docs/Interaction/Connect.fs`: MAUI/WebRTC connection and session pumps.
- `src/Speak2Docs.Orchestration/DemoVoiceOrchestration.fs`: typed voice orchestration.
- `src/Speak2Docs.Orchestration/Agents/WorkFlow.fs`: workflow agent assembly.
- `src/Speak2Docs.Orchestration/Agents/QaAgent.fs`: QA runtime assembly.

## ASP.NET Core Hosting Assembly

`FsVoice.Hosting.AspNetCore` is the browser/server assembly example. It is intentionally thinner than Speak2Docs: it bridges browser events and realtime events into the generic `VoiceRuntimeEngine`.

1. The web app creates a `BridgeSessionStore`.
2. The web app registers `BridgeEndpoints.map` with a route prefix and a `BridgeSessionFactory`.
3. The factory returns `BridgeSessionOptions` containing a session id, an `IVoicePlugin`, a `VoicePluginHostContext`, and optional `VoiceRuntimeOptions`.
4. A browser calls `POST {prefix}/sessions` to create and start a `BridgeSession`.
5. `BridgeSession` creates a `VoiceRuntimeEngine` with `BridgeTransport`.
6. Browser, WebRTC, and realtime server events enter through `POST {prefix}/sessions/{sessionId}/events`.
7. Runtime and outbound realtime client events are exposed through `GET {prefix}/sessions/{sessionId}/events`.
8. `DELETE {prefix}/sessions/{sessionId}` disposes the session.

Minimal registration shape:

```fsharp
let store = FsVoice.Hosting.AspNetCore.BridgeSessionStore()

let createOptions sessionId =
    { FsVoice.Hosting.AspNetCore.BridgeSessionOptions.sessionId = sessionId
      plugin = myVoicePlugin
      hostContext =
        { storageRoot = storageRoot
          settings = Map.empty
          report = fun message -> logger.LogInformation("{Message}", message) }
      runtimeOptions = None }

FsVoice.Hosting.AspNetCore.BridgeEndpoints.map "/fsvoice" store createOptions app
|> ignore
```

For a QA-oriented web app, a QA plug-in can be adapted into the generic runtime contract:

```fsharp
let myVoicePlugin =
    FsVoice.QA.VoicePluginAdapters.fromQaPlugIn myQaPlugIn
```

Use that adapter when the browser host needs the generic `IVoicePlugin` surface. Use the full Speak2Docs-style orchestration when the app needs richer typed host messages, workflow agents, source lifecycle, realtime voice UX, and QA answer coordination.

## What Developers Usually Replace

- Host shell: MAUI, browser, CLI, background service, or test harness.
- Plug-in: prompts, model roles, runtime options, settings fields, and behavior profile.
- Tools: business actions exposed as `IQaToolProvider` or generic `IVoiceTool`.
- Context: app data, packaged knowledge, database retrieval, API retrieval, or selected local sources.
- Runtime policy: retrieval mode, lexical filter, keyword indexing, timeouts, memory/writeback behavior.
- Model clients: realtime, transcription, answer, planner, keyword, and query-expansion models.

## Assembly Checklist

1. Choose the host: direct typed orchestration, ASP.NET bridge, CLI, or deterministic test runtime.
2. Choose the contract level: generic `IVoicePlugin` for platform voice sessions, or `IQaPlugIn` for QA workflows.
3. Provide host context: storage root, settings, reporting/logging, and any app-specific services.
4. Compose plug-in defaults with host overrides.
5. Attach sources and context providers.
6. Attach tool providers.
7. Create model clients for the roles the workflow enables.
8. Start the session, pump host/realtime events, and dispose the session on disconnect.
