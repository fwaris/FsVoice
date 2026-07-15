# EMNLP 2026 System Demonstration Plan

Target: EMNLP 2026 System Demonstrations.

Official call: https://2026.emnlp.org/calls/demos/

## Hard Requirements

- Submission deadline: Friday, July 10, 2026, 11:59 pm UTC-12.
- Notification: Friday, August 20, 2026.
- Camera-ready: Sunday, August 30, 2026.
- Conference: October 24-29, 2026, Budapest, Hungary.
- Submission system: OpenReview. The call says the link will be posted at least two weeks before the deadline.
- Review policy: single-blind. Author names and affiliations should be included.
- Paper format: EMNLP 2026 official style, PDF.
- Paper length: up to 6 pages, plus unlimited references and optional ethics/broader-impact text.
- Appendix: optional, maximum 2 pages.
- Video: screencast of at most 2.5 minutes, submitted with the paper. YouTube or similar link is encouraged; MPEG4 supplementary upload is allowed.
- Demo access: a live demo website or downloadable installable package link is mandatory. Missing link can cause desk rejection.
- Evaluation: some form of evaluation is required. The call warns that papers without evaluation may be desk rejected.
- Multiple submission: must not be under review or published elsewhere; multiple demo papers must not overlap by more than 25 percent.

## Proposed Paper

Working title:

> FsVoice: A Platform for Building Voice-First Conversational Applications

Core demo claim:

FsVoice turns realtime speech interaction into an event-driven, typed orchestration for voice-first conversational applications. Speak2Docs demonstrates one application of the platform: a mobile document assistant that lets users select local documents or index bundles, ask questions by voice, and receive spoken answers grounded in retrieved document passages.

## Required Assets

- Paper PDF.
- 2.5 minute screencast.
- Public repository link.
- Installable app/package link or live demo link.
- Screenshots or diagrams for the paper.
- License statement.
- Evaluation results.

## Evaluation Work Items

See `evaluation-plan.md`.

Minimum viable evaluation for the demo track:

1. Conversational-platform task evaluation covering connection, turn completion, Oracle tool use, host-state synchronization, and reconnect/interruption behavior.
2. System latency measurements for spoken question to answer start and answer completion.
3. Retrieval/answering evaluation on the existing InsuranceQA CLI workflow or a small document QA benchmark.
4. A small second-host or second-configuration demonstration if time allows, such as CLI or ASP.NET hosting.

## Paper Build Notes

The draft uses the official ACL family style files. Download the current files from:

https://github.com/acl-org/acl-style-files

Expected local files:

- `acl.sty`
- `acl_natbib.bst`

The demo call is authoritative if any generic ACL formatting guideline differs from the demo-specific rules.
