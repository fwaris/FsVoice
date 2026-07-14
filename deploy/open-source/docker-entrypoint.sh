#!/usr/bin/env bash
set -Eeuo pipefail

asset_mode="${FSVOICE_ASSET_MODE:-local}"
asset_runtime_env="${FSVOICE_ASSET_RUNTIME_ENV:-/run/fsvoice/assets.env}"
asset_status_file="${FSVOICE_ASSET_STATUS_FILE:-/run/fsvoice/assets-status.json}"

if [[ "${1:-}" != "--as-app" && "$(id -u)" == "0" ]]; then
    if [[ "${asset_mode}" != "local" ]]; then
        cache_base="${FSVOICE_ASSET_CACHE_ROOT:-/data/asset-cache}"
        cache_namespace="${POD_NAME:-standalone}"

        if [[ "${FSVOICE_ASSET_CACHE_PERMISSION_MODE:-rootPreflight}" == "rootPreflight" ]]; then
            install -d -m 0770 -o app -g app "${cache_base}" "${cache_base}/${cache_namespace}"
        fi

        export FSVOICE_ASSET_CACHE_ROOT="${cache_base}/${cache_namespace}"
    fi

    install -d -m 0770 -o app -g app "$(dirname "${asset_runtime_env}")" "$(dirname "${asset_status_file}")"

    exec setpriv \
        --reuid=app \
        --regid=app \
        --init-groups \
        --bounding-set=-all \
        --ambient-caps=-all \
        --inh-caps=-all \
        "$0" --as-app
fi

if [[ "${1:-}" == "--as-app" ]]; then
    shift
fi

require_file() {
    local path="$1"
    local description="$2"

    if [[ ! -f "$path" ]]; then
        echo "$description was not found: $path" >&2
        exit 2
    fi
}

prepare_assets() {
    local command=(
        dotnet /opt/fsvoice-assets/FsVoice.Assets.Cli.dll prepare
        --mode "${asset_mode}"
        --runtime-env "${asset_runtime_env}"
        --status-path "${asset_status_file}"
    )

    if [[ "${asset_mode}" == "local" ]]; then
        command+=(
            --gemma-model "${LLAMA_CPP_MODEL}"
            --stt-model-dir "${OpenSourceVoice__Stt__ModelDir}"
            --vad-model "${OpenSourceVoice__Vad__ModelPath}"
            --tts-model-dir "${OpenSourceVoice__Tts__ModelDir}"
            --voice-sample "${OpenSourceVoice__Tts__VoiceSamplePath}"
            --index-dir "${OpenSourceVoice__Index__BundleDirectory}"
        )
    elif [[ "${asset_mode}" == "azureBlob" || "${asset_mode}" == "s3" ]]; then
        command+=(
            --cache-root "${FSVOICE_ASSET_CACHE_ROOT}"
            --release-id "${FSVOICE_ASSET_RELEASE_ID:?FSVOICE_ASSET_RELEASE_ID is required for remote assets}"
            --manifest-key "${FSVOICE_ASSET_MANIFEST_KEY:?FSVOICE_ASSET_MANIFEST_KEY is required for remote assets}"
            --manifest-sha256 "${FSVOICE_ASSET_MANIFEST_SHA256:?FSVOICE_ASSET_MANIFEST_SHA256 is required for remote assets}"
            --retain-releases "${FSVOICE_ASSET_RETAIN_RELEASES:-2}"
            --parallel-downloads "${FSVOICE_ASSET_PARALLEL_DOWNLOADS:-4}"
            --max-retries "${FSVOICE_ASSET_MAX_RETRIES:-5}"
        )

        if [[ "${asset_mode}" == "azureBlob" ]]; then
            command+=(
                --azure-account-url "${FSVOICE_AZURE_ACCOUNT_URL:?FSVOICE_AZURE_ACCOUNT_URL is required for Azure Blob assets}"
                --azure-container "${FSVOICE_AZURE_CONTAINER:?FSVOICE_AZURE_CONTAINER is required for Azure Blob assets}"
            )

            if [[ -n "${FSVOICE_AZURE_SAS_TOKEN:-}" ]]; then
                command+=(--azure-sas-token "${FSVOICE_AZURE_SAS_TOKEN}")
            fi

            if [[ -n "${AZURE_CLIENT_ID:-}" ]]; then
                command+=(--azure-managed-identity-client-id "${AZURE_CLIENT_ID}")
            fi
        else
            command+=(
                --s3-bucket "${FSVOICE_S3_BUCKET:?FSVOICE_S3_BUCKET is required for S3 assets}"
                --s3-region "${FSVOICE_S3_REGION:?FSVOICE_S3_REGION is required for S3 assets}"
            )

            if [[ -n "${AWS_ACCESS_KEY_ID:-}" ]]; then
                command+=(--s3-access-key-id "${AWS_ACCESS_KEY_ID}")
            fi

            if [[ -n "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
                command+=(--s3-secret-access-key "${AWS_SECRET_ACCESS_KEY}")
            fi

            if [[ -n "${AWS_SESSION_TOKEN:-}" ]]; then
                command+=(--s3-session-token "${AWS_SESSION_TOKEN}")
            fi

            if [[ -n "${FSVOICE_S3_SERVICE_URL:-}" ]]; then
                command+=(--s3-service-url "${FSVOICE_S3_SERVICE_URL}")
            fi

            if [[ -n "${FSVOICE_S3_FORCE_PATH_STYLE:-}" ]]; then
                command+=(--s3-force-path-style "${FSVOICE_S3_FORCE_PATH_STYLE}")
            fi
        fi
    else
        echo "FSVOICE_ASSET_MODE must be local, azureBlob, or s3; received '${asset_mode}'." >&2
        exit 2
    fi

    "${command[@]}"

    if [[ ! -f "${asset_runtime_env}" ]]; then
        echo "Asset bootstrap did not create its runtime environment file: ${asset_runtime_env}" >&2
        exit 2
    fi

    set -a
    # The bootstrapper writes shell-quoted, manifest-validated absolute paths.
    source "${asset_runtime_env}"
    set +a

    unset FSVOICE_AZURE_SAS_TOKEN AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN
}

prepare_assets

require_file "${LLAMA_CPP_MODEL}" "Gemma GGUF model"
require_file "${OpenSourceVoice__Vad__ModelPath}" "Silero VAD ONNX model"
require_file "${OpenSourceVoice__Index__BundleDirectory}/index-bundle.json" "External FsColbert bundle manifest"

llama_args=(
    --model "${LLAMA_CPP_MODEL}"
    --host 127.0.0.1
    --port 8081
    --ctx-size "${LLAMA_CPP_CONTEXT_SIZE}"
    --n-gpu-layers "${LLAMA_CPP_GPU_LAYERS}"
    --parallel "${LLAMA_CPP_PARALLEL}"
)

if [[ -n "${LLAMA_CPP_THREADS:-}" ]]; then
    llama_args+=(--threads "${LLAMA_CPP_THREADS}")
fi

if [[ -n "${LLAMA_CPP_EXTRA_ARGS:-}" ]]; then
    read -r -a extra_args <<< "${LLAMA_CPP_EXTRA_ARGS}"
    llama_args+=("${extra_args[@]}")
fi

echo "Starting bundled llama.cpp server."
echo "Model: ${LLAMA_CPP_MODEL}"
echo "Context size: ${LLAMA_CPP_CONTEXT_SIZE}"
echo "GPU layers: ${LLAMA_CPP_GPU_LAYERS}"
/opt/llama/bin/llama-server "${llama_args[@]}" &
llama_pid=$!

cleanup() {
    trap - INT TERM

    if [[ -n "${fsvoice_pid:-}" ]] && kill -0 "${fsvoice_pid}" 2>/dev/null; then
        kill -TERM "${fsvoice_pid}" 2>/dev/null || true
    fi

    if kill -0 "${llama_pid}" 2>/dev/null; then
        kill -TERM "${llama_pid}" 2>/dev/null || true
    fi

    wait "${fsvoice_pid:-}" 2>/dev/null || true
    wait "${llama_pid}" 2>/dev/null || true
}

trap 'cleanup; exit 143' INT TERM

deadline=$((SECONDS + LLAMA_CPP_STARTUP_TIMEOUT_SECONDS))

until curl --fail --silent http://127.0.0.1:8081/health >/dev/null; do
    if ! kill -0 "${llama_pid}" 2>/dev/null; then
        wait "${llama_pid}" || true
        echo "Bundled llama.cpp exited before becoming ready." >&2
        exit 3
    fi

    if (( SECONDS >= deadline )); then
        echo "Bundled llama.cpp did not become ready within ${LLAMA_CPP_STARTUP_TIMEOUT_SECONDS} seconds." >&2
        cleanup
        exit 4
    fi

    sleep 2
done

echo "Bundled llama.cpp is ready at http://127.0.0.1:8081."
echo "Starting FsVoice on ${ASPNETCORE_URLS}."
dotnet /opt/fsvoice/FsVoice.OpenSource.Server.dll &
fsvoice_pid=$!

set +e
wait -n "${llama_pid}" "${fsvoice_pid}"
exit_code=$?
set -e

if ! kill -0 "${llama_pid}" 2>/dev/null; then
    echo "Bundled llama.cpp stopped; stopping FsVoice." >&2
elif ! kill -0 "${fsvoice_pid}" 2>/dev/null; then
    echo "FsVoice stopped; stopping bundled llama.cpp." >&2
fi

cleanup
exit "${exit_code}"
