# Adaptive reasoning latency experiment

Branch: `experiment/adaptive-reasoning-latency`

The experiment uses the local Gemma Q4_K_M CUDA model with deterministic decoding. Model initialization is reported separately from steady-state generation.

## Measured results

| Policy | Clean answers | Observed model latency | Finding |
|---|---:|---:|---|
| Fast, thinking off, 96 tokens | 3/4 | 2.9–5.1 s warm | Correct on direct arithmetic and logic, but failed the genuinely multi-step books problem. |
| Thinking, 192 tokens | 1/4 | 25.5–30.8 s on truncated cases | Too small: three responses ended inside the private thought channel. |
| Thinking, 256 tokens | 2/4 | 26.9–31.3 s warm | Arithmetic completed at 254 tokens; logic and multi-step responses still truncated. |
| Deep, thinking, 512 tokens | 4/4 | 16.4–40.7 s warm | Reliable control policy, but expensive. |
| Concise thinking, 384 tokens | 2/2 hard cases | 28.3 s generation for logic; 36.3 s warm for multi-step | Both produced valid public answers. Multi-step improved from 40.7 s to 36.3 s and 319 to 285 output tokens. |

The first fast case and the concise-logic case included model load time, so their total wall times are not used as steady-state comparisons. The simple answer “Two plus two equals four” was counted as correct even though the initial string rubric expected the digit `4`.

## Policy selected for this branch

- Fast: only greetings, direct time/status/inventory requests, and simple two-operand arithmetic. Thinking is disabled and the output cap is 96 tokens.
- Balanced: ordinary requests use the tested brief-reasoning instruction and a 384-token safety cap.
- Deep: explicit analysis and multi-step relationships keep the original 512-token reasoning path.
- Direct tools: time, runtime status, source inventory, and explicit document questions bypass the model’s tool-selection round. Tool execution still returns to Gemma for a natural final answer.

The direct-tool optimization is structurally verified: the integration test observes one answer-generation request rather than a separate model tool-selection request followed by answer generation. Its exact end-to-end saving depends on prompt length and generated tokens, so production traces should be used rather than assigning a synthetic millisecond estimate.

## Turn instrumentation

Each turn’s `details.json` now records:

- selected depth, reason, thinking flag, token cap, and tool-round cap;
- input/output tokens, thought characters, parse result, stop reason, and runtime timings for every Gemma round;
- tool duration and whether the tool was deterministically pre-routed.

See [adaptive_reasoning_latency.mmd](adaptive_reasoning_latency.mmd) for the experimental flow.
