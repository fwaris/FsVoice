# Speak2Docs Settings Help

## OpenAI API Key

Speak2Docs uses your OpenAI API key to connect to OpenAI services for realtime voice interaction, answers, and optional keyword enrichment.

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

Speak2Docs uses separate model roles for realtime voice, transcription, answers, planning, keyword generation, and query expansion. The defaults are selected for the built-in Generic QA plugin, and advanced users can override model ids in Settings.

Model settings are advanced controls. Change them only when you know which OpenAI model id you want each role to use.

- Reasoning Level controls the answer model's reasoning effort. The default is Low.
- Max Answer Tokens limits the answer response length. The default is 2500, and values are kept between 128 and 32000.
- Tool Rounds limits how many function-call rounds the answer model can run for one question. The default is 3, and values are kept between 1 and 8.

## Retrieval

Retrieval settings control how selected documents are searched before the answer model responds. Indexed retrieval uses persisted local indexes when available, with fallback behavior for source matching. Lexical filtering and keyword indexing can improve source targeting for document questions.

- Mode switches between FsColbert with fallback and the internal document index.
- Lexical Filter is on by default and helps narrow retrieval to likely matching document text.
- Log Expansions and Log Chunks add retrieval diagnostics to the activity log for troubleshooting.

## PDF Parsing

PDF parsing controls how Speak2Docs extracts text from PDF sources before indexing.

- PDF Parser is Hybrid by default. Legacy parsing is available if a PDF behaves poorly with hybrid parsing.
- Layout Analysis is on by default when Hybrid parsing is enabled. It can improve document structure handling, but it may make indexing slower.
- Index Keywords is off by default. When enabled, Speak2Docs can generate extra keywords for indexed chunks to improve matching.

## Activity

The Activity log records important setup, indexing, connection, retrieval, and answer events. The default log level is Informational. Turn on the verbose log level when troubleshooting a connection, retrieval, or answer-generation problem.

## When Settings Are Locked

Some settings are disabled while the app is busy, a realtime connection is active, or a source operation is running. Disconnect or wait for processing to finish before changing model, retrieval, or parsing settings.
