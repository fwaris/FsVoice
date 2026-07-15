# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597
ARG CUDA_DEVEL_IMAGE=nvidia/cuda:13.3.0-devel-ubuntu24.04@sha256:ef2203909e80b8b976cfc672f7e2ae2b00bc0e25c404ee86d89e10a3802f1c52
ARG CUDA_RUNTIME_IMAGE=nvidia/cuda:13.3.0-cudnn-runtime-ubuntu24.04@sha256:95c91edfddb448d236689f572725b8421f3e51a6808f11e37ba6834dc57b12c8

FROM --platform=linux/amd64 ${CUDA_DEVEL_IMAGE} AS llama-build
ARG LLAMA_CPP_VERSION=b9987
ARG LLAMA_CUDA_ARCHITECTURES=80-real;80-virtual

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        build-essential \
        ca-certificates \
        cmake \
        git \
        ninja-build \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --branch "${LLAMA_CPP_VERSION}" --depth 1 https://github.com/ggml-org/llama.cpp.git /src/llama.cpp \
    && cmake -S /src/llama.cpp -B /src/llama.cpp/build -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_CUDA_ARCHITECTURES="${LLAMA_CUDA_ARCHITECTURES}" \
        -DBUILD_SHARED_LIBS=OFF \
        -DGGML_CUDA=ON \
        -DGGML_NATIVE=OFF \
        -DLLAMA_CURL=OFF \
    && cmake --build /src/llama.cpp/build --config Release --target llama-server --parallel \
    && strip /src/llama.cpp/build/bin/llama-server

FROM --platform=linux/amd64 ${DOTNET_SDK_IMAGE} AS app-build
WORKDIR /src
COPY . .

RUN dotnet publish src/FsVoice.OpenSource.Server/FsVoice.OpenSource.Server.fsproj \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained false \
        --output /out \
        -p:OnnxRuntimeFlavor=Cuda \
        -p:DebugSymbols=false \
        -p:DebugType=None \
    && rm -rf /out/runtimes/win-x64 \
    && find /out -maxdepth 1 -type f -iname 'onnxruntime*.dll' -delete

RUN dotnet publish src/FsVoice.Assets.Cli/FsVoice.Assets.Cli.fsproj \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained false \
        --output /assets-out \
        -p:DebugSymbols=false \
        -p:DebugType=None

RUN forbidden="$(find /out -type f \( \
        -iname '*.gguf' \
        -o -iname '*.fsci' \
        -o -iname 'index-bundle.json' \
        -o -iname 'default_voice.wav' \
        -o -iname 'encoder-model*.onnx' \
        -o -iname 'decoder_joint-model*.onnx' \
        -o -iname 'silero_vad.onnx' \
        -o -iname 'flow_lm_main*.onnx' \
        -o -iname 'mimi_decoder*.onnx' \
    \) -print)" \
    && if [ -n "${forbidden}" ]; then \
        printf 'External runtime asset was included in the FsVoice publish:\n%s\n' "${forbidden}" >&2; \
        exit 1; \
    fi

RUN forbidden="$(find /assets-out -type f \( \
        -iname '*.gguf' \
        -o -iname '*.fsci' \
        -o -iname 'index-bundle.json' \
        -o -iname 'default_voice.wav' \
        -o -iname '*.onnx' \
    \) -print)" \
    && if [ -n "${forbidden}" ]; then \
        printf 'External runtime asset was included in the asset bootstrap publish:\n%s\n' "${forbidden}" >&2; \
        exit 1; \
    fi

FROM --platform=linux/amd64 ${DOTNET_ASPNET_IMAGE} AS dotnet-runtime

FROM --platform=linux/amd64 ${CUDA_RUNTIME_IMAGE} AS final
ARG APP_UID=1654
ARG APP_GID=1654

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        libgomp1 \
        libicu74 \
        libssl3 \
        tini \
        util-linux \
        zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid "${APP_GID}" app \
    && useradd --uid "${APP_UID}" --gid "${APP_GID}" --create-home --shell /usr/sbin/nologin app \
    && mkdir -p /opt/fsvoice /opt/llama/bin /data/models /data/voices /data/indexes /data/work \
    && chown -R app:app /opt/fsvoice /opt/llama /data /home/app

COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=llama-build /src/llama.cpp/build/bin/llama-server /opt/llama/bin/llama-server
COPY --from=app-build /out/ /opt/fsvoice/
COPY --from=app-build /assets-out/ /opt/fsvoice-assets/
COPY --chmod=0755 deploy/open-source/docker-entrypoint.sh /opt/fsvoice/docker-entrypoint.sh

ENV PATH="/usr/share/dotnet:/opt/llama/bin:${PATH}" \
    DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://0.0.0.0:5067 \
    OpenSourceVoice__Gemma__LlamaCppEndpoint=http://127.0.0.1:8081 \
    OpenSourceVoice__WorkDir=/data/work \
    OpenSourceVoice__Index__BundleDirectory=/data/indexes \
    OpenSourceVoice__Vad__ModelPath=/data/models/silero-vad-onnx/silero_vad.onnx \
    OpenSourceVoice__Vad__AllowBargeIn=true \
    FSVOICE_ASSET_MODE=local \
    FSVOICE_ASSET_CACHE_ROOT=/data/asset-cache \
    FSVOICE_ASSET_CACHE_PERMISSION_MODE=rootPreflight \
    FSVOICE_ASSET_RUNTIME_ENV=/run/fsvoice/assets.env \
    FSVOICE_ASSET_STATUS_FILE=/run/fsvoice/assets-status.json \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    LLAMA_CPP_MODEL=/data/models/gemma-4-E2B_q4_0-it.gguf \
    LLAMA_CPP_CONTEXT_SIZE=16384 \
    LLAMA_CPP_GPU_LAYERS=999 \
    LLAMA_CPP_PARALLEL=1 \
    LLAMA_CPP_STARTUP_TIMEOUT_SECONDS=900

WORKDIR /opt/fsvoice
USER root

EXPOSE 5067/tcp
EXPOSE 50670-50679/udp

HEALTHCHECK --interval=30s --timeout=10s --start-period=10m --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:5067/healthz/ready >/dev/null || exit 1

ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/opt/fsvoice/docker-entrypoint.sh"]
