# Store Privacy Notes

Use this as the source checklist for Speak2Docs Apple App Privacy and Google Play Data safety. It is not a privacy policy by itself.

## Data Handled By The App

- OpenAI API key: entered by the user in Speak2Docs and used to connect to OpenAI services.
- Microphone audio: captured only after the user starts a realtime voice session and grants microphone permission.
- User documents: PDFs, Markdown, and JSON files selected by the user for local retrieval and indexing.
- Extracted document text and keywords: generated from selected documents for retrieval and optional keyword enrichment.
- Runtime logs: local troubleshooting text that may include document names, source names, or snippets depending on settings.
- OpenAI data notice suppression: a versioned local preference recording whether the user chose not to show the OpenAI notice again before connecting.

## Network Sharing

- Speak2Docs shows an in-app OpenAI data notice popup before sending personal data or document context to OpenAI unless the user has chosen not to show the notice again.
- OpenAI receives realtime audio, transcripts, prompts, selected document passages, and related context needed to answer user questions after the user acknowledges the notice.
- When keyword elaboration is enabled and OpenAI processing is allowed, selected passage text may be sent to OpenAI for keyword generation.
- Speak2Docs does not use a separate developer backend for OpenAI requests; the user's OpenAI API key authenticates requests directly to OpenAI.
- ONNX document layout and retrieval models run locally after download or bundle installation.

## Local Storage

- API keys are stored in MAUI `SecureStorage`; older preference-stored keys are migrated to secure storage.
- Non-secret preferences use platform preferences/user defaults.
- Documents, indexes, and keyword cache are stored in the app container. Speak2Docs does not use durable memory storage.
- Android backup is disabled in the manifest to avoid backing up sensitive local app data.

## Apple Privacy Manifest

The bundled `PrivacyInfo.xcprivacy` declares:

- no app tracking,
- no tracking domains,
- audio data for app functionality,
- other user content for app functionality,
- required-reason access to user defaults for app preferences,
- required-reason access to file timestamps for app-container document/index management.

Apple App Privacy answers still need to be completed in App Store Connect because audio, documents, prompts, and local user content may be processed by OpenAI depending on the workflow.

Recommended App Store Connect notes: disclose that OpenAI is the third-party AI provider, list the data sent to OpenAI, explain that the app shows an acknowledgement notice before OpenAI processing, and note that users can choose not to show the notice again or re-enable it in Settings.

## Google Play Data Safety

At minimum, review these categories:

- Audio data: collected/transmitted when realtime voice is used.
- Files and docs: user-provided documents are processed locally and may influence prompts sent to OpenAI.
- App activity or diagnostics: only if logs/diagnostics are exported or transmitted.
- Security practices: data is encrypted in transit; API keys are stored using secure platform storage.

Speak2Docs does not create app accounts. Say that users do not need an account in the app, but explain that they need an OpenAI API key for realtime question answering.
