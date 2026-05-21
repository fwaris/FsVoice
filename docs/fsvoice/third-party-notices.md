# Speak2Docs Third-Party Notices

Speak2Docs is built with .NET, .NET MAUI, F#, Fabulous, OpenAI client libraries, WebRTC components, document parsing libraries, ONNX Runtime components, and related open-source packages.

The app may also include or download local model assets used for document layout analysis, retrieval, or indexing. Package-specific notices in the source repository provide attribution for embedded model assets.

Notable bundled notice:

- PP-DocLayout-M notice: `src/FsVoice.QA/PackageNotices/PP-DocLayout-M-NOTICE.md`

Speak2Docs source code is licensed under MIT. Third-party packages retain their respective licenses.

## Built-in sample document

Speak2Docs includes a built-in sample index for "An LLM-RAG Approach for Healthy Eating Index-Informed Personalized Food Recommendations" by Yibin Wang, Yanjie Yang, Grace Melo Guerrero, Rodolfo M. Nayga, and Azlan Zahid, arXiv:2605.15213, submitted May 11, 2026. The paper is licensed under Creative Commons Attribution 4.0 International: https://creativecommons.org/licenses/by/4.0/

Source: https://arxiv.org/abs/2605.15213

The PDF is redistributed unchanged. The local FsColbert index is generated from the PDF text for offline retrieval inside the app.
