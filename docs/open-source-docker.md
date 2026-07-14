# FsVoice open-source CUDA container

This deployment uses one Linux x64 CUDA 13 image. The image contains FsVoice,
llama.cpp, the .NET runtime, and native inference libraries. Model weights,
voice samples, the FsColbert index bundle, and generated run artifacts remain
outside the image.

## Host requirements

- An NVIDIA GPU supported by CUDA 13 and the NVIDIA Container Toolkit.
- Docker Engine with Compose v2, or Docker Desktop using its NVIDIA GPU support.
- Absolute host directories containing the model, voice, index, and work data.

The llama.cpp build includes a native A100 `sm80` cubin plus `compute_80` PTX.
CUDA 13 drivers can JIT the PTX on newer GPU architectures, allowing the same
image to run across systems without multiplying the image and build size with a
separate cubin for every GPU generation. Override `LLAMA_CUDA_ARCHITECTURES`
only when a deployment needs additional architecture-specific cubins.

## External data layout

~~~text
models/
  gemma-4-E2B_q4_0-it.gguf
  parakeet-tdt-0.6b-v3-onnx/
  silero-vad-onnx/
    silero_vad.onnx
  pocket-tts-onnx-english-2026-04/
voices/
  default_voice.wav
indexes/version-1/
  index-bundle.json
  indexes/
    *.fsci
work/
~~~

`index-bundle.json` may describe multiple sources. Its `index_file` values are
resolved relative to the mounted bundle directory. FsVoice validates and loads
the complete bundle during startup; a missing, corrupt, empty, or incompatible
bundle stops the container.

## Build and run

From the repository root in PowerShell:

~~~powershell
Copy-Item deploy/open-source/.env.example deploy/open-source/.env
# Edit deploy/open-source/.env and set all four absolute directory paths.

docker compose `
  --env-file deploy/open-source/.env `
  -f deploy/open-source/compose.yml `
  build

docker compose `
  --env-file deploy/open-source/.env `
  -f deploy/open-source/compose.yml `
  up -d
~~~

Open `http://localhost:5067/?transport=websocket`. The container exposes TCP
port 5067 and UDP ports 50670-50679. WebSocket is the baseline transport when
Docker networking prevents direct WebRTC ICE connectivity.

The microphone icon connects continuous audio to server-side Silero VAD. The
default `FSVOICE_ALLOW_BARGE_IN=true` lets new speech cancel an active response;
set it to `false` and recreate the container for half-duplex operation.

Check the service:

~~~powershell
Invoke-RestMethod http://localhost:5067/healthz
Invoke-RestMethod http://localhost:5067/healthz/ready
Invoke-RestMethod http://localhost:5067/api/status
docker compose --env-file deploy/open-source/.env -f deploy/open-source/compose.yml logs -f
~~~

Run a full WebSocket speech turn with an existing PCM16 or float32 WAV file:

~~~powershell
.\deploy\open-source\smoke-websocket.ps1 `
  -Url http://localhost:5067 `
  -AudioPath E:\path\to\spoken-test.wav
~~~

The smoke script verifies transcription, Gemma generation, streamed Pocket TTS
audio, and the response-to-first-answer-audio metric.

The container does not expose llama.cpp on a host port. FsVoice talks to the
bundled server at `http://127.0.0.1:8081`.

## Cloud-backed asset cache

For automated Azure Blob or S3 deployments, use
`deploy/open-source/compose.remote.yml` with
`deploy/open-source/.env.remote.example`. The container downloads a pinned
immutable asset release before starting llama.cpp, validates every SHA-256, and
caches verified content in `FSVOICE_ASSET_CACHE_DIR`. See
[`open-source-assets.md`](open-source-assets.md) for publishing and Helm
deployment instructions.

## Replace the index bundle

Prepare the new bundle in a different versioned host directory. Change
`FSVOICE_INDEX_DIR` in `.env`, then recreate the service:

~~~powershell
docker compose `
  --env-file deploy/open-source/.env `
  -f deploy/open-source/compose.yml `
  up -d --force-recreate
~~~

The bundle is intentionally loaded once per process. FsVoice never copies it
into the writable work directory and does not hot-reload a partially replaced
bundle.

On Linux, ensure UID 1654 can write to `FSVOICE_WORK_DIR`. All other mounts are
read-only. To tune llama.cpp without rebuilding, set `LLAMA_CPP_CONTEXT_SIZE`,
`LLAMA_CPP_GPU_LAYERS`, or whitespace-separated `LLAMA_CPP_EXTRA_ARGS` in
`.env`.
