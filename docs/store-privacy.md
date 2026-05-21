# Store Privacy Notes

Use this as the source checklist for Speak2Docs Apple App Privacy and Google Play Data safety. It is not a privacy policy by itself.

## Data Handled By The App

- OpenAI API key: entered by the user in Speak2Docs and used to connect to OpenAI services.
- Microphone audio: captured only after the user starts a realtime voice session and grants microphone permission.
- User documents: PDFs, Markdown, and JSON files selected by the user for local retrieval and indexing.
- Extracted document text and keywords: generated from selected documents for retrieval and optional keyword enrichment.
- Durable memory records: locally stored memories created by the app workflow.
- Runtime logs: local troubleshooting text that may include document names, source names, or snippets depending on settings.

## Network Sharing

- OpenAI receives realtime audio/transcripts and prompts needed to answer user questions.
- When keyword elaboration is enabled, selected passage text may be sent to OpenAI for keyword generation.
- ONNX document layout and retrieval models run locally after download or bundle installation.

## Local Storage

- API keys should be stored in MAUI `SecureStorage`.
- Non-secret preferences use platform preferences/user defaults.
- Documents, indexes, keyword cache, and durable memory are stored in the app container.
- Android backup is disabled in the manifest to avoid backing up sensitive local app data.

## Apple Privacy Manifest

The bundled `PrivacyInfo.xcprivacy` declares:

- no app tracking,
- no tracking domains,
- required-reason access to user defaults for app preferences,
- required-reason access to file timestamps for app-container document/index management.

Apple App Privacy answers still need to be completed in App Store Connect because audio, documents, prompts, and local user content may be processed by OpenAI depending on the workflow.

## Google Play Data Safety

At minimum, review these categories:

- Audio data: collected/transmitted when realtime voice is used.
- Files and docs: user-provided documents are processed locally and may influence prompts sent to OpenAI.
- App activity or diagnostics: only if logs/diagnostics are exported or transmitted.
- Security practices: data is encrypted in transit; API keys should be stored using secure platform storage.

Speak2Docs does not create app accounts. Say that users do not need an account in the app, but explain that they need an OpenAI API key for realtime question answering.
