# Speak2Docs Third-Party Notices

Speak2Docs is built with .NET, .NET MAUI, F#, Fabulous, OpenAI client libraries, WebRTC components, document parsing libraries, ONNX Runtime components, and related open-source packages.

The app may also include or download local model assets used for document layout analysis, retrieval, or indexing. Package-specific notices in the source repository provide attribution for embedded model assets.

Notable bundled notice:

- PP-DocLayout-M notice: `src/FsVoice.Retrieval/PackageNotices/PP-DocLayout-M-NOTICE.md`
- RapidOCR PP-OCRv4 mobile model notice: `src/FsVoice.Retrieval.RapidOcrModels/THIRD-PARTY-NOTICES.md`

Speak2Docs source code is licensed under MIT. Third-party packages retain their respective licenses.

## Silero VAD

The FsVoice open-source server can download and use the Silero VAD v6.2.1 ONNX
model from `snakers4/silero-vad`. Silero VAD is licensed under the MIT License.
The model and its license remain in the external shared models directory and are
not committed to this repository or embedded in FsVoice packages.

Source: https://github.com/snakers4/silero-vad/tree/v6.2.1

## Built-in sample document

Speak2Docs includes a built-in sample index for "AI on the Pulse: Real-Time Health Anomaly Detection with Wearable and Ambient Intelligence" by Davide Gabrielli, Bardh Prenkaj, Paola Velardi, and Stefano Faralli, arXiv:2508.03436, submitted August 5, 2025. The paper is licensed under Creative Commons Attribution 4.0 International: https://creativecommons.org/licenses/by/4.0/

Source: https://arxiv.org/abs/2508.03436

PDF: https://arxiv.org/pdf/2508.03436

The PDF is redistributed unchanged. The local FsColbert index is generated from the PDF text and compact visual-description passages for offline retrieval inside the app.
