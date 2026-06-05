# Evaluation Plan

EMNLP 2026 System Demonstrations explicitly asks how the system was evaluated and warns that submissions without evaluation may be desk rejected. The goal here is to produce credible evidence for FsVoice as a conversational-application platform, with Speak2Docs as the concrete document-QA reference application.

## Evaluation Questions

1. Can FsVoice complete representative conversational application tasks with stable realtime behavior?
2. What latency does the multi-agent Voice + Oracle design introduce?
3. Can the platform coordinate speech, host state, tools, and context providers?
4. Can Speak2Docs retrieve relevant evidence from selected document sources?
5. Can Speak2Docs answer document questions with useful, grounded responses?
6. What does the platform enable beyond a single hard-coded app?

## Proposed Measurements

### Conversational Platform Robustness

Run a scripted demo checklist at least 10 times:

- Start realtime session.
- Ask a direct conversational question.
- Ask a tool-requiring or Oracle-delegated question.
- Observe host-state updates such as transcript/activity logs.
- Disconnect and reconnect.
- Exercise interruption or cancellation behavior if feasible.

Report failures, observed limitations, and mitigations.

### Latency

Measure:

- Time from final transcript to Oracle request.
- Tool or retrieval time where applicable.
- Oracle answer time.
- Time to first spoken answer token/audio.
- Total turn completion time.

Report median, p90, and sample size.

### Retrieval Quality

Use the existing `insuranceqa-search-eval` or a similar CLI evaluation path.

Report:

- Dataset or source collection.
- Number of queries.
- Retrieval mode.
- Top-k setting.
- Metrics: Recall@k, MRR, nDCG, or the metrics already emitted by the CLI.

### End-to-End Answer Quality

Use `insuranceqa-eval` or a curated set of document QA questions over a public source bundle.

Report:

- Number of questions.
- Source documents.
- Judge method: human rubric, LLM-assisted rubric, or exact/semantic answer match.
- Metrics: answer correctness, citation/support quality, refusal/error rate.

## Candidate Baselines

- Text-only CLI QA over the same source.
- Same retrieval without query expansion.
- Same retrieval without keyword enrichment.
- Realtime voice direct answer without Oracle tool call, if supported by a controlled setup.
- Same platform task through a second host or configuration, if feasible.

## Artifacts To Archive

- Evaluation scripts or commands.
- Source bundle manifest.
- Query set.
- Raw result JSON/CSV.
- Summarized tables for the paper.
- Demo video script.
