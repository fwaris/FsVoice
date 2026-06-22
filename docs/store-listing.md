# Store Listing Draft

## App Name

Speak2Docs

## Short Description

Realtime voice question answering over your selected documents.

## Full Description

Speak2Docs lets you add PDFs, Markdown, and structured JSON sources, build a local retrieval index, and ask questions by voice using OpenAI realtime models. The main screen keeps selected ready sources in a compact Sources flow view, while the Library page provides search, preview, selection, clear-selection, delete, and retry controls for longer source lists. The app keeps document indexes on device and uses your OpenAI API key for realtime answers after you acknowledge the OpenAI data notice in the app.

## Reviewer Notes

The app requires microphone permission for realtime voice interaction. Core functionality also requires an OpenAI API key. Reviewers can use a temporary test key, if provided, or enter their own key in Settings.

OpenAI data use: Speak2Docs uses OpenAI as the third-party AI service for realtime transcription, voice answers, retrieval support, optional keyword enrichment, and optional PDF visual descriptions. Before a realtime connection sends data to OpenAI, the app shows an in-app notice explaining that microphone audio, transcripts, prompts, selected document passages, and optional cropped PDF visual regions may be sent to OpenAI. The popup includes Privacy Policy and Terms of Use links, a Do not show again checkbox, and Acknowledge/Dismiss actions. The connection starts only after acknowledgement; dismissing the popup cancels the connection. Documents and indexes remain local unless selected context or enabled PDF visual crops are sent to OpenAI for these features. Speak2Docs does not use a separate developer backend for OpenAI requests; the user's OpenAI API key authenticates requests directly to OpenAI.

Suggested reviewer flow:

1. Open Settings and enter an OpenAI API key.
2. Use the bundled sample source or tap + to add PDFs, Markdown, JSON, or a Speak2Docs index bundle.
3. Wait for document processing to finish. The add progress panel shows status and can be canceled.
4. Tap Sources to open Library, search or preview sources if desired, and select one or more ready sources.
5. Return to the main screen and confirm the selected sources appear in the compact Sources area.
6. Tap Connect, review the OpenAI data notice popup, and choose Acknowledge.
7. Tap Connect again if needed, then ask a question about the selected sources.

## Keywords

voice, documents, PDF, question answering, retrieval, AI, OpenAI, notes, research

## Support And Privacy URLs

- Support URL: https://fwaris.github.io/docs/
- Terms of Use URL: https://fwaris.github.io/docs/fsvoice/terms.html
- Privacy Policy URL: https://fwaris.github.io/docs/fsvoice/privacy.html
- Third-Party Software License URL: https://fwaris.github.io/docs/fsvoice/third-party-notices.html
