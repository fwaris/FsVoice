# Speak2Docs Settings Help

## OpenAI API Key

Speak2Docs uses your OpenAI API key to connect to OpenAI services for realtime voice interaction, answers, and optional keyword enrichment.

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

## Retrieval

Retrieval settings control how selected documents are searched before the answer model responds. Indexed retrieval uses persisted local indexes when available, with fallback behavior for source matching. Lexical filtering and keyword indexing can improve source targeting for document questions.
