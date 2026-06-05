# Speak2Docs Settings Help

This page explains the controls shown in Speak2Docs Settings. Settings are grouped the same way they appear in the app: Account, Models, Activity, Retrieval, PDF Parsing, Runtime, Links, and PlugIn Settings.

## Account

Account settings identify the OpenAI account and active behavior profile Speak2Docs should use.

### OpenAI key

Stores your OpenAI API key locally so Speak2Docs can connect to OpenAI services for realtime voice interaction, transcription, answers, query expansion, and optional keyword enrichment. The key is saved with the platform secure-storage mechanism when available.

### Show or hide key

Toggles whether the OpenAI key field is displayed as plain text while you edit it. This only changes visibility in the Settings screen; it does not change the saved key.

### PlugIn

Shows the active Speak2Docs plugin. A plugin supplies prompts, model defaults, retrieval behavior, optional packaged contexts, optional tools, and any plugin-specific settings. The built-in default is Generic QA.

Before sending microphone audio, transcripts, prompts, selected document passages, or optional keyword-generation text to OpenAI, Speak2Docs asks you to allow OpenAI processing inside the app. You can review or revoke this permission from Settings.

To create a key:

1. Sign in to the OpenAI platform.
2. Open the API keys page.
3. Create a new secret key.
4. Copy the key and paste it into the OpenAI API key field in Speak2Docs Settings.

Use a separate API key for each app, device, automation, or experiment when practical. This makes it easier to monitor usage, rotate one key without interrupting other workflows, and revoke only the key that is no longer needed.

Good API key practices:

- Keep the key private and do not share it in screenshots, logs, commits, support messages, or public documents.
- Monitor API usage and billing in the OpenAI platform dashboard.
- Revoke keys that are no longer needed, exposed, or used on a device you no longer control.
- Create a fresh key if you are testing a new workflow or giving temporary access for review.
- Avoid reusing a long-lived personal key for unrelated projects.

## Models

Speak2Docs uses separate model roles for realtime voice, transcription, answers, keyword generation, and query expansion. The defaults are selected by the active plugin, and advanced users can override model ids in Settings.

Model settings are advanced controls. Change them only when you know which OpenAI model id you want each role to use.

### Realtime model

Handles low-latency voice conversation and decides when to call the document-answering oracle. The built-in default is `gpt-realtime-2`.

### Transcriber model

Converts speech to text when transcription is needed. The built-in default is `gpt-4o-mini-transcribe`.

### Answer model

Produces grounded answers from selected sources, tool observations, and the current question. The built-in default is `gpt-5.5`.

### Keyword model

Generates optional index keywords when Index Keywords is enabled. The built-in default is `gpt-5-nano`.

### QueryExpansion model

Rewrites or expands user questions to improve document retrieval. The built-in default is `gpt-5-nano`.

### Reasoning Level

Controls answer-model reasoning effort. Low is the default and is usually fastest. Medium or High can be useful for harder synthesis questions, but may increase latency and token usage.

### Max Answer Tokens

Limits the answer model's output length. The default is 2500. Values are normalized between 128 and 32000.

### Tool Rounds

Limits how many function-call rounds the answer model can run for one question. The default is 3. Values are normalized between 1 and 8.

## Activity

Activity settings control how much diagnostic information appears in the in-app activity log.

### Log Level

Switches between Informational and Verbose activity logging. Informational is the default for normal use. Verbose adds more detail for troubleshooting connection, retrieval, indexing, and answer-generation behavior.

## Retrieval

Retrieval settings control how selected documents are searched before the answer model responds. Indexed retrieval uses persisted local indexes when available, with fallback behavior for source matching. Lexical filtering and keyword indexing can improve source targeting for document questions.

### Mode

Switches between FsColbert with fallback and the internal document index. FsColbert with fallback uses persisted local indexes when available and can fall back to the internal index when needed.

### Lexical Filter

Helps narrow retrieval to source text that likely matches the question. It is on by default.

### Log Expansions

Adds query-expansion diagnostics to the activity log. Turn this on when troubleshooting why a question did or did not match a document.

### Log Chunks

Adds retrieved chunk diagnostics to the activity log. Turn this on when checking which source passages are being sent to the answer model.

## PDF Parsing

PDF parsing controls how Speak2Docs extracts text from PDF sources before indexing.

### PDF Parser

Selects Hybrid or Legacy parsing. Hybrid is the default and is intended to produce better structure for indexed PDF content. Legacy parsing is available if a particular PDF behaves poorly with Hybrid parsing.

### Layout Analysis

Uses layout analysis during Hybrid parsing. It is on by default when Hybrid parsing is enabled. It can improve document structure handling, but indexing may take longer. This control is disabled when Legacy parsing is selected.

### Index Keywords

Allows Speak2Docs to generate extra keywords for indexed chunks to improve matching. It is off by default and may use the Keyword model during document processing.

## Runtime

Runtime rows identify the major assemblies used by the current app build. These rows are informational and are useful when reporting support issues.

### Platform contract

Shows the shared platform-contract assembly used between the host app and orchestration layer.

### Orchestration

Shows the Speak2Docs orchestration assembly that coordinates voice, retrieval, source state, and answer flow.

### WebRTC bridge

Shows the bridge assembly used for realtime connection hosting and WebRTC integration.

## Links

Link settings open public documentation and review notices from inside the app.

### Terms

Opens the Speak2Docs Terms of Use.

### Privacy

Opens the Speak2Docs Privacy Policy.

### Licenses

Opens third-party notices for libraries and assets included with Speak2Docs.

### Settings

Opens this Settings Help page.

### AI Data

Opens the OpenAI data-processing notice. If the notice was hidden with "Do not show again," this row shows that the notice is currently hidden before connecting.

## PlugIn Settings

Some plugins can add their own settings. These appear in the PlugIn Settings group only when the active plugin defines them.

### Boolean plugin fields

Plugin fields marked as boolean, bool, toggle, or switch appear as on/off switches.

### Text plugin fields

Other plugin fields appear as text inputs. Their meaning, default value, and validation rules are defined by the active plugin.

## Related Local Preferences

Speak2Docs also stores local preferences that are managed by normal app actions rather than edited directly in the Settings form.

### Document library

Stores the local document-source manifest, including selected sources and processing status.

### Hidden built-in sources

Tracks built-in sample sources that were hidden with Delete. Use the restore control on the main toolbar to show hidden built-in indexes again.

### Accepted terms version

Records the Terms of Use and Privacy Policy version accepted on this device.

### OpenAI data notice preference

Records whether the OpenAI data-processing notice should be shown before connecting, based on the "Do not show again" choice.

## When Settings Are Locked

Some settings are disabled while the app is busy, a realtime connection is active, or a source operation is running. Disconnect or wait for processing to finish before changing model, retrieval, or parsing settings.
