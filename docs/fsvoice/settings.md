# Speak2Docs Settings Help

Settings are locked when Speak2Docs is busy, a realtime connection is starting or active, or a source operation is running. Disconnect or wait for processing to finish before changing them.

The Settings top-bar Help icon opens this page.

## Account

Account settings identify the OpenAI account Speak2Docs should use.

### OpenAI key

Stores your OpenAI API key locally so Speak2Docs can connect to OpenAI services for realtime voice interaction, transcription, answers, query expansion, optional keyword enrichment, and optional PDF visual descriptions.

To use Speak2Docs with OpenAI:

1. Go to [platform.openai.com](https://platform.openai.com).
2. Create an OpenAI account or sign in to an existing account.
3. Open the OpenAI platform billing area and add a payment method, credits, or other required funding for API usage.
4. After the account and billing setup are ready, open the [API keys page](https://platform.openai.com/api-keys).
5. Create a new secret key.
6. Copy the key and paste it into the OpenAI key field in Speak2Docs Settings.

### Show or hide key

Toggles whether the OpenAI key field is displayed as plain text while you edit it. This only changes visibility in Settings; it does not change the saved key.

## Models

Model settings are advanced controls. Change them only when you know which OpenAI model id you want each role to use.

### Realtime model

Handles low-latency voice conversation. The built-in default is `gpt-realtime-2`.

### Transcriber model

Converts speech to text when transcription is needed.

### Answer model

Produces grounded answers from selected sources, tool observations, and the current question.

### Keyword model

Generates optional enriched keywords when Enrich Keywords is enabled. The built-in default is `gpt-5-mini`.

### QueryExpansion model

Rewrites or expands user questions to improve document retrieval.

### Reasoning Level

Controls answer-model reasoning effort. Low is the default and is usually fastest.

### Max Answer Tokens

Limits the answer model's output length. The default is 5000.

### Max Tool Calls

Limits how many tool-call rounds the answer model can run for one question. The default is 8.

### Context Chunks

Controls how many ranked document chunks Speak2Docs retrieves for an answer and allows into the model context. The default is 12.

## Activity

Activity settings control how much diagnostic information appears in the activity log.

### Log Level

Switches between Informational and Verbose activity logging. Verbose adds more detail for troubleshooting connection, retrieval, indexing, and answer-generation behavior.

## Audio

Audio settings control the default route used during realtime voice sessions.

### Default Speaker

Toggles whether Speak2Docs defaults voice playback to the device speaker while still preferring a connected headset or Bluetooth route when available.

## Retrieval

Retrieval settings control how selected documents are searched before answers.

### Mode

Switches between FsColbert with fallback and the internal document index.

### Lexical Filter

Helps narrow retrieval to source text that likely matches the question.

### Log Expansions

Adds query-expansion diagnostics to the activity log.

### Log Chunks

Adds retrieved chunk diagnostics to the activity log.

## PDF Parsing

PDF parsing always uses the Hybrid parser. These settings control optional Hybrid indexing features.

### Optical Parsing

Uses local OCR for PDF pages whose native text is missing or too sparse. This can make scanned or Adobe Distiller-produced documents readable but may take longer.

### Auto OCR Fallback

Automatically OCRs individual PDF pages when native text extraction returns enough text but the text looks garbled. This setting is active only when Optical Parsing is enabled.

### Layout Analysis

Uses layout analysis during PDF parsing. This can improve structure but may take longer.

### Enrich Keywords

Allows Speak2Docs to enrich indexed chunks with extra keywords to improve matching.

### Describe Visuals

Allows Hybrid PDF parsing with Layout Analysis to describe detected diagrams, charts, images, and sparse visual tables. This can add processing time, OpenAI API cost, and additional document visual content sent to OpenAI.

## Links

Link settings open public documentation and review notices from inside the app.

### Terms

Opens the Speak2Docs Terms of Use.

### Privacy

Opens the Speak2Docs Privacy Policy.

### Licenses

Opens third-party notices for libraries and assets included with Speak2Docs.

### AI Data

Opens the OpenAI data-processing notice. If the notice was hidden with "Do not show again," this row shows that the notice is currently hidden before connecting.
