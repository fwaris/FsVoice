namespace FsVoice.OpenSource

open System

module TtsRuntimeFactory =
    let create (options: TtsRuntimeOptions) pathBase =
        let runtime =
            if String.IsNullOrWhiteSpace options.Runtime then "chatterbox-onnx"
            else options.Runtime.Trim().ToLowerInvariant()

        match runtime with
        | "chatterbox-onnx" | "chatterbox" ->
            new ChatterboxOnnxTtsRuntime(options, pathBase) :> ITtsRuntime
        | other ->
            invalidArg (nameof options.Runtime) $"Unsupported open-source TTS runtime '{other}'. Use chatterbox-onnx."

