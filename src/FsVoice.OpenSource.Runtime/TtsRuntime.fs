namespace FsVoice.OpenSource

open System

module TtsRuntimeFactory =
    let create (options: TtsRuntimeOptions) pathBase =
        let runtime =
            if String.IsNullOrWhiteSpace options.Runtime then
                "chatterbox-onnx"
            else
                options.Runtime.Trim().ToLowerInvariant()

        match runtime with
        | "chatterbox-onnx"
        | "chatterbox" -> new ChatterboxOnnxTtsRuntime(options, pathBase) :> ITtsRuntime
        | "pocket-tts-onnx"
        | "pocket-tts"
        | "pocket" -> new PocketTtsOnnxRuntime(options, pathBase) :> ITtsRuntime
        | "pocket-tts-onnx-v2"
        | "pocket-tts-v2" -> new PocketTtsOnnxV2Runtime(options, pathBase) :> ITtsRuntime
        | other ->
            invalidArg
                (nameof options.Runtime)
                $"Unsupported open-source TTS runtime '{other}'. Use chatterbox-onnx, pocket-tts-onnx, or pocket-tts-onnx-v2."
