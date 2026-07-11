namespace FsVoice.OpenSource

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers

type private PocketV2Tensor =
    | FloatTensor of DenseTensor<float32>
    | Int64Tensor of DenseTensor<int64>
    | BoolTensor of DenseTensor<bool>

type private PocketV2StateEntry =
    { InputName: string
      Index: int
      Shape: int array
      DataType: string
      Fill: string }

type private PocketV2Bundle =
    { SampleRate: int
      FrameRate: float
      LatentDim: int
      ConditioningDim: int
      MaxTokensPerChunk: int
      PadShortInputs: bool
      RemoveSemicolons: bool
      RecommendedFramesAfterEos: int option
      TokenizerFile: string
      BosBeforeVoiceFile: string option
      FlowState: PocketV2StateEntry array
      MimiState: PocketV2StateEntry array }

type private PocketV2Sessions =
    { MimiEncoder: InferenceSession
      TextConditioner: InferenceSession
      FlowMain: InferenceSession
      Flow: InferenceSession
      MimiDecoder: InferenceSession
      Tokenizer: SentencePieceTokenizer
      BosBeforeVoice: DenseTensor<float32> option }

    interface IDisposable with
        member this.Dispose() =
            this.MimiEncoder.Dispose()
            this.TextConditioner.Dispose()
            this.FlowMain.Dispose()
            this.Flow.Dispose()
            this.MimiDecoder.Dispose()

type private PocketV2CachedVoice =
    { Key: string; State: PocketV2RunState }

and private PocketV2RunState =
    | ManagedState of PocketV2Tensor array
    | RunState of owner: IDisposableReadOnlyCollection<DisposableNamedOnnxValue> * outputOffset: int

module private PocketV2Bundle =
    let private invalidData message = raise (InvalidDataException message)

    let private requiredProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> value
        | _ -> invalidData $"Pocket TTS v2 bundle is missing '{name}'."

    let private optionalBool (name: string) fallback (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private optionalInt (name: string) fallback (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> fallback

    let private optionalIntOption (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
        | _ -> None

    let private optionalString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let private parseState (element: JsonElement) =
        { InputName = requiredProperty "input_name" element |> _.GetString()
          Index = requiredProperty "index" element |> _.GetInt32()
          Shape =
            requiredProperty "shape" element
            |> _.EnumerateArray()
            |> Seq.map _.GetInt32()
            |> Seq.toArray
          DataType = requiredProperty "dtype" element |> _.GetString()
          Fill = requiredProperty "fill" element |> _.GetString() }

    let load (path: string) =
        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement

        let parseManifest (name: string) =
            requiredProperty name root
            |> _.EnumerateArray()
            |> Seq.map parseState
            |> Seq.sortBy _.Index
            |> Seq.toArray

        { SampleRate = requiredProperty "sample_rate" root |> _.GetInt32()
          FrameRate = requiredProperty "frame_rate" root |> _.GetDouble()
          LatentDim = requiredProperty "latent_dim" root |> _.GetInt32()
          ConditioningDim = requiredProperty "conditioning_dim" root |> _.GetInt32()
          MaxTokensPerChunk = optionalInt "max_token_per_chunk" 50 root
          PadShortInputs = optionalBool "pad_with_spaces_for_short_inputs" false root
          RemoveSemicolons = optionalBool "remove_semicolons" false root
          RecommendedFramesAfterEos = optionalIntOption "model_recommended_frames_after_eos" root
          TokenizerFile = requiredProperty "tokenizer_file" root |> _.GetString()
          BosBeforeVoiceFile = optionalString "bos_before_voice_file" root
          FlowState = parseManifest "flow_lm_state_manifest"
          MimiState = parseManifest "mimi_state_manifest" }

module private PocketV2Npy =
    let private invalidData message = raise (InvalidDataException message)

    let loadFloat32 (path: string) =
        use stream = File.OpenRead path
        use reader = new BinaryReader(stream, Encoding.ASCII, false)
        let magic = reader.ReadBytes 6

        if magic <> [| 0x93uy; byte 'N'; byte 'U'; byte 'M'; byte 'P'; byte 'Y' |] then
            invalidData $"Unsupported NumPy file: {path}"

        let major = reader.ReadByte()
        reader.ReadByte() |> ignore

        let headerLength =
            if major = 1uy then
                int (reader.ReadUInt16())
            else
                reader.ReadInt32()

        let header = Encoding.ASCII.GetString(reader.ReadBytes headerLength)

        if not (header.Contains("<f4", StringComparison.Ordinal)) then
            invalidData $"Pocket TTS BOS tensor must use little-endian float32: {path}"

        let shapeMatch = Regex.Match(header, @"'shape':\s*\(([^)]*)\)")

        if not shapeMatch.Success then
            invalidData $"Pocket TTS BOS tensor has no readable shape: {path}"

        let shape =
            shapeMatch.Groups[1]
                .Value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map Int32.Parse

        let count = shape |> Array.fold (fun total value -> total * value) 1
        let values = Array.init count (fun _ -> reader.ReadSingle())
        DenseTensor<float32>(values, shape)

module private PocketV2State =
    let private invalidData message = raise (InvalidDataException message)

    let private elementCount shape =
        shape |> Array.fold (fun total value -> total * value) 1

    let create (entry: PocketV2StateEntry) =
        let count = elementCount entry.Shape

        match entry.DataType with
        | "float32" ->
            let value = if entry.Fill = "nan" then Single.NaN else 0.0f
            FloatTensor(DenseTensor<float32>(Array.create count value, entry.Shape))
        | "int64" -> Int64Tensor(DenseTensor<int64>(Array.zeroCreate<int64> count, entry.Shape))
        | "bool" ->
            let value = entry.Fill = "ones"
            BoolTensor(DenseTensor<bool>(Array.create<bool> count value, entry.Shape))
        | value -> invalidData $"Unsupported Pocket TTS state type '{value}'."

    let initialize manifest = manifest |> Array.map create

    let named name tensor =
        match tensor with
        | FloatTensor value -> NamedOnnxValue.CreateFromTensor(name, value)
        | Int64Tensor value -> NamedOnnxValue.CreateFromTensor(name, value)
        | BoolTensor value -> NamedOnnxValue.CreateFromTensor(name, value)

    let private namedResult name (entry: PocketV2StateEntry) (value: DisposableNamedOnnxValue) =
        match entry.DataType with
        | "float32" ->
            let tensor = value.AsTensor<float32>()

            if tensor.Length = 0 then
                NamedOnnxValue.CreateFromTensor(
                    name,
                    DenseTensor<float32>(Array.empty<float32>, tensor.Dimensions.ToArray())
                )
            else
                NamedOnnxValue.CreateFromTensor(name, tensor)
        | "int64" ->
            let tensor = value.AsTensor<int64>()

            if tensor.Length = 0 then
                NamedOnnxValue.CreateFromTensor(
                    name,
                    DenseTensor<int64>(Array.empty<int64>, tensor.Dimensions.ToArray())
                )
            else
                NamedOnnxValue.CreateFromTensor(name, tensor)
        | "bool" ->
            let tensor = value.AsTensor<bool>()

            if tensor.Length = 0 then
                NamedOnnxValue.CreateFromTensor(name, DenseTensor<bool>(Array.empty<bool>, tensor.Dimensions.ToArray()))
            else
                NamedOnnxValue.CreateFromTensor(name, tensor)
        | value -> invalidData $"Unsupported Pocket TTS state type '{value}'."

    let feeds manifest state =
        match state with
        | ManagedState values -> Array.map2 (fun entry value -> named entry.InputName value) manifest values
        | RunState(owner, outputOffset) ->
            manifest
            |> Array.map (fun entry ->
                let value = owner |> Seq.item (outputOffset + entry.Index)
                namedResult entry.InputName entry value)

    let dispose state =
        match state with
        | ManagedState _ -> ()
        | RunState(owner, _) -> owner.Dispose()

type PocketTtsOnnxV2Runtime(options: TtsRuntimeOptions, pathBase: string) =
    let modelDir = RuntimePaths.resolveAgainst pathBase options.ModelDir

    let bundleDir =
        let direct = Path.Combine(modelDir, "bundle.json")
        let nested = Path.Combine(modelDir, "english_2026-04", "bundle.json")

        if File.Exists direct then modelDir
        elif File.Exists nested then Path.GetDirectoryName nested
        else modelDir

    let precision =
        match options.Precision.Trim().ToLowerInvariant() with
        | "int8" -> "int8"
        | "fp32" -> "fp32"
        | value -> invalidArg (nameof options.Precision) $"Pocket TTS v2 precision must be int8 or fp32, not '{value}'."

    let executionProvider =
        if String.IsNullOrWhiteSpace options.ExecutionProvider then
            "cpu"
        else
            options.ExecutionProvider.Trim().ToLowerInvariant()

    let numThreads = max 1 options.NumThreads
    let numSteps = max 1 options.NumSteps
    let temperature = max 0.0 options.Temperature
    let decoderChunkFrames = max 1 options.DecoderChunkFrames
    let firstChunkFrames = max 1 (min decoderChunkFrames options.FirstChunkFrames)
    let maxReferenceAudioSeconds = max 1.0 options.MaxReferenceAudioSeconds
    let voiceCacheCapacity = max 0 options.VoiceEmbeddingCacheCapacity
    let trimReferenceSilence = options.TrimReferenceSilence
    let referenceSilenceThresholdDb = options.ReferenceSilenceThresholdDb
    let referenceSilencePaddingSeconds = max 0.0 options.ReferenceSilencePaddingSeconds
    let synthesisGate = new SemaphoreSlim(1, 1)
    let syncRoot = obj ()
    let mutable bundle: PocketV2Bundle option = None
    let mutable sessions: PocketV2Sessions option = None
    let voiceCache = ResizeArray<PocketV2CachedVoice>()

    let bundlePath = Path.Combine(bundleDir, "bundle.json")

    let selectedModel stem =
        if precision = "int8" then
            Path.Combine(bundleDir, $"{stem}_int8.onnx")
        else
            Path.Combine(bundleDir, $"{stem}.onnx")

    let requiredFiles () =
        let metadata =
            if File.Exists bundlePath then
                Some(PocketV2Bundle.load bundlePath)
            else
                None

        [| yield bundlePath
           yield Path.Combine(bundleDir, "mimi_encoder.onnx")
           yield Path.Combine(bundleDir, "text_conditioner.onnx")
           yield selectedModel "flow_lm_main"
           yield selectedModel "flow_lm_flow"
           yield selectedModel "mimi_decoder"

           match metadata with
           | Some value ->
               yield Path.Combine(bundleDir, value.TokenizerFile)

               match value.BosBeforeVoiceFile with
               | Some file -> yield Path.Combine(bundleDir, file)
               | None -> ()
           | None ->
               yield Path.Combine(bundleDir, "tokenizer.model")
               yield Path.Combine(bundleDir, "bos_before_voice.npy") |]

    let missingFiles () =
        requiredFiles () |> Array.distinct |> Array.filter (File.Exists >> not)

    let configuredVoiceSample () =
        if String.IsNullOrWhiteSpace options.VoiceSamplePath then
            ""
        else
            RuntimePaths.resolveAgainst pathBase options.VoiceSamplePath

    let requestVoiceSample (request: TtsSynthesisRequest) =
        request.VoiceSamplePath
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (RuntimePaths.resolveAgainst pathBase)
        |> Option.defaultValue (configuredVoiceSample ())

    let sessionOptions () =
        let value = new SessionOptions()
        value.IntraOpNumThreads <- numThreads
        value.InterOpNumThreads <- 1
        value.GraphOptimizationLevel <- GraphOptimizationLevel.ORT_ENABLE_ALL

        if executionProvider <> "cpu" then
            invalidArg
                (nameof options.ExecutionProvider)
                $"Pocket TTS v2 is currently CPU-optimized. Use ExecutionProvider=cpu, not '{executionProvider}'."

        value

    let loadBundle () =
        match bundle with
        | Some value -> value
        | None ->
            let value = PocketV2Bundle.load bundlePath
            bundle <- Some value
            value

    let loadSessions () =
        match sessions with
        | Some value -> value
        | None ->
            lock syncRoot (fun () ->
                match sessions with
                | Some value -> value
                | None ->
                    let missing = missingFiles ()

                    if missing.Length > 0 then
                        let missingText = String.Join(", ", missing)
                        invalidOp $"Pocket TTS v2 is missing: {missingText}"

                    let metadata = loadBundle ()

                    let createSession (path: string) =
                        use sessionOptions = sessionOptions ()
                        new InferenceSession(path, sessionOptions)

                    use tokenizerStream = File.OpenRead(Path.Combine(bundleDir, metadata.TokenizerFile))

                    let value =
                        { MimiEncoder = createSession (Path.Combine(bundleDir, "mimi_encoder.onnx"))
                          TextConditioner = createSession (Path.Combine(bundleDir, "text_conditioner.onnx"))
                          FlowMain = createSession (selectedModel "flow_lm_main")
                          Flow = createSession (selectedModel "flow_lm_flow")
                          MimiDecoder = createSession (selectedModel "mimi_decoder")
                          Tokenizer = SentencePieceTokenizer.Create(tokenizerStream, false, false)
                          BosBeforeVoice =
                            metadata.BosBeforeVoiceFile
                            |> Option.map (fun file -> PocketV2Npy.loadFloat32 (Path.Combine(bundleDir, file))) }

                    sessions <- Some value
                    value)

    let tensorFloat (value: DisposableNamedOnnxValue) =
        let tensor = value.AsTensor<float32>()
        DenseTensor<float32>(tensor.ToArray(), tensor.Dimensions.ToArray())

    let resultAt index (results: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) = results |> Seq.item index

    let prependBos (bos: DenseTensor<float32>) (embeddings: DenseTensor<float32>) =
        let bosDims = bos.Dimensions.ToArray()
        let embeddingDims = embeddings.Dimensions.ToArray()

        if
            bosDims.Length <> 3
            || embeddingDims.Length <> 3
            || bosDims[0] <> embeddingDims[0]
            || bosDims[2] <> embeddingDims[2]
        then
            raise (InvalidDataException "Pocket TTS BOS and voice embedding tensors are incompatible.")

        let width = embeddingDims[2]
        let values = Array.zeroCreate<float32> ((bosDims[1] + embeddingDims[1]) * width)
        let bosValues = bos.ToArray()
        let embeddingValues = embeddings.ToArray()
        Array.Copy(bosValues, 0, values, 0, bosValues.Length)
        Array.Copy(embeddingValues, 0, values, bosValues.Length, embeddingValues.Length)
        DenseTensor<float32>(values, [| 1; bosDims[1] + embeddingDims[1]; width |])

    let conditionVoice (active: PocketV2Sessions) (metadata: PocketV2Bundle) path =
        let sampleRate, original = Wave.readMono path

        let prepared =
            if trimReferenceSilence then
                AudioPcm.trimEdgeSilence sampleRate referenceSilenceThresholdDb referenceSilencePaddingSeconds original
            else
                original

        if prepared.Length = 0 then
            invalidOp $"Pocket TTS v2 reference voice contains no speakable audio: {path}"

        let maxSamples = int (Math.Ceiling(maxReferenceAudioSeconds * float sampleRate))

        let limited =
            if prepared.Length <= maxSamples then
                prepared
            else
                prepared[0 .. maxSamples - 1]

        let samples = AudioPcm.resampleBandLimited sampleRate metadata.SampleRate limited

        use encoderResults =
            active.MimiEncoder.Run(
                [| NamedOnnxValue.CreateFromTensor("audio", DenseTensor<float32>(samples, [| 1; 1; samples.Length |])) |]
            )

        let embeddings = resultAt 0 encoderResults |> tensorFloat

        let voiceEmbeddings =
            match active.BosBeforeVoice with
            | Some bos -> prependBos bos embeddings
            | None -> embeddings

        let initialState = PocketV2State.initialize metadata.FlowState |> ManagedState
        let feeds = ResizeArray<NamedOnnxValue>()

        feeds.Add(
            NamedOnnxValue.CreateFromTensor(
                "sequence",
                DenseTensor<float32>(Array.empty<float32>, [| 1; 0; metadata.LatentDim |])
            )
        )

        feeds.Add(NamedOnnxValue.CreateFromTensor("text_embeddings", voiceEmbeddings))
        PocketV2State.feeds metadata.FlowState initialState |> feeds.AddRange

        let results = active.FlowMain.Run feeds
        RunState(results, 2)

    let voiceState active metadata path =
        let info = FileInfo path

        let key =
            $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{maxReferenceAudioSeconds}|{trimReferenceSilence}|{referenceSilenceThresholdDb}|{referenceSilencePaddingSeconds}"

        let cachedIndex = voiceCache |> Seq.tryFindIndex (fun value -> value.Key = key)

        match cachedIndex with
        | Some index ->
            let value = voiceCache[index]
            voiceCache.RemoveAt index
            voiceCache.Add value
            value.State, false
        | None ->
            let state = conditionVoice active metadata info.FullName

            if voiceCacheCapacity = 0 then
                state, true
            else
                voiceCache.Add { Key = key; State = state }

                while voiceCache.Count > voiceCacheCapacity do
                    let oldest = voiceCache[0]
                    voiceCache.RemoveAt 0
                    PocketV2State.dispose oldest.State

                state, false

    let prepareText (metadata: PocketV2Bundle) (text: string) =
        let mutable value = Regex.Replace(text.Trim(), @"\s+", " ")

        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof text) "Pocket TTS text cannot be empty."

        if metadata.RemoveSemicolons then
            value <- value.Replace(';', ',')

        if not (Char.IsUpper value[0]) then
            value <- Char.ToUpperInvariant(value[0]).ToString() + value.Substring(1)

        if Char.IsLetterOrDigit value[value.Length - 1] then
            value <- value + "."

        if metadata.PadShortInputs && Regex.Matches(value, @"\S+").Count < 5 then
            value <- String(' ', 8) + value

        let framesAfterEosGuess = if Regex.Matches(value, @"\S+").Count <= 4 then 3 else 1
        value, framesAfterEosGuess

    let tokenize (active: PocketV2Sessions) (metadata: PocketV2Bundle) text =
        let prepared, framesAfterEos = prepareText metadata text
        let ids = active.Tokenizer.EncodeToIds(prepared) |> Seq.map int64 |> Seq.toArray
        ids, framesAfterEos

    let textChunks (active: PocketV2Sessions) (metadata: PocketV2Bundle) text =
        let prepared, _ = prepareText metadata text

        let sentences =
            Regex.Split(prepared, @"(?<=[.!?])\s+")
            |> Array.filter (String.IsNullOrWhiteSpace >> not)

        let chunks = ResizeArray<string>()
        let mutable current = ""

        for sentence in sentences do
            let candidate =
                if String.IsNullOrWhiteSpace current then
                    sentence
                else
                    current + " " + sentence

            let ids, _ = tokenize active metadata candidate

            if
                ids.Length > metadata.MaxTokensPerChunk
                && not (String.IsNullOrWhiteSpace current)
            then
                chunks.Add current
                current <- sentence
            else
                current <- candidate

        if not (String.IsNullOrWhiteSpace current) then
            chunks.Add current

        chunks.ToArray()

    let gaussian (rng: Random) count standardDeviation =
        let output = Array.zeroCreate<float32> count
        let mutable index = 0

        while index < count do
            let u1 = max Double.Epsilon (rng.NextDouble())
            let u2 = rng.NextDouble()
            let magnitude = Math.Sqrt(-2.0 * Math.Log u1) * standardDeviation
            output[index] <- float32 (magnitude * Math.Cos(2.0 * Math.PI * u2))

            if index + 1 < count then
                output[index + 1] <- float32 (magnitude * Math.Sin(2.0 * Math.PI * u2))

            index <- index + 2

        output

    let runFlowChunk
        (active: PocketV2Sessions)
        (metadata: PocketV2Bundle)
        (baseState: PocketV2RunState)
        (rng: Random)
        (text: string)
        (onLatent: float32 array -> unit)
        (cancellationToken: CancellationToken)
        =
        let tokenIds, framesAfterEosGuess = tokenize active metadata text
        let tokenTensor = DenseTensor<int64>(tokenIds, [| 1; tokenIds.Length |])

        use conditionerResults =
            active.TextConditioner.Run([| NamedOnnxValue.CreateFromTensor("token_ids", tokenTensor) |])

        let textEmbeddings = resultAt 0 conditionerResults |> tensorFloat

        let emptySequence =
            DenseTensor<float32>(Array.empty<float32>, [| 1; 0; metadata.LatentDim |])

        let emptyText =
            DenseTensor<float32>(Array.empty<float32>, [| 1; 0; metadata.ConditioningDim |])

        let promptFeeds = ResizeArray<NamedOnnxValue>()
        promptFeeds.Add(NamedOnnxValue.CreateFromTensor("sequence", emptySequence))
        promptFeeds.Add(NamedOnnxValue.CreateFromTensor("text_embeddings", textEmbeddings))
        PocketV2State.feeds metadata.FlowState baseState |> promptFeeds.AddRange

        let promptResults = active.FlowMain.Run promptFeeds
        let mutable state = RunState(promptResults, 2)

        let mutable current = Array.create metadata.LatentDim Single.NaN
        let mutable eosStep: int option = None

        let framesAfterEos =
            metadata.RecommendedFramesAfterEos
            |> Option.defaultValue (framesAfterEosGuess + 2)

        let frameLimit =
            Math.Ceiling((float tokenIds.Length / 3.0 + 2.0) * metadata.FrameRate) |> int

        let dt = 1.0 / float numSteps
        let standardDeviation = Math.Sqrt temperature
        let mutable step = 0
        let mutable finished = false

        try
            while step < frameLimit && not finished do
                cancellationToken.ThrowIfCancellationRequested()
                let feeds = ResizeArray<NamedOnnxValue>()

                feeds.Add(
                    NamedOnnxValue.CreateFromTensor(
                        "sequence",
                        DenseTensor<float32>(current, [| 1; 1; metadata.LatentDim |])
                    )
                )

                feeds.Add(NamedOnnxValue.CreateFromTensor("text_embeddings", emptyText))
                PocketV2State.feeds metadata.FlowState state |> feeds.AddRange

                let previousState = state
                let results = active.FlowMain.Run feeds
                let conditioning = resultAt 0 results |> tensorFloat
                let eos = resultAt 1 results |> _.AsTensor<float32>() |> Seq.head
                state <- RunState(results, 2)
                PocketV2State.dispose previousState

                if eos > -4.0f && eosStep.IsNone then
                    eosStep <- Some step

                match eosStep with
                | Some value when step >= value + framesAfterEos -> finished <- true
                | _ ->
                    let mutable latent =
                        if temperature > 0.0 then
                            gaussian rng metadata.LatentDim standardDeviation
                        else
                            Array.zeroCreate metadata.LatentDim

                    for flowStep in 0 .. numSteps - 1 do
                        let s = float flowStep / float numSteps
                        let t = s + dt

                        use flowResults =
                            active.Flow.Run(
                                [| NamedOnnxValue.CreateFromTensor("c", conditioning)
                                   NamedOnnxValue.CreateFromTensor(
                                       "s",
                                       DenseTensor<float32>([| float32 s |], [| 1; 1 |])
                                   )
                                   NamedOnnxValue.CreateFromTensor(
                                       "t",
                                       DenseTensor<float32>([| float32 t |], [| 1; 1 |])
                                   )
                                   NamedOnnxValue.CreateFromTensor(
                                       "x",
                                       DenseTensor<float32>(latent, [| 1; metadata.LatentDim |])
                                   ) |]
                            )

                        let flow = resultAt 0 flowResults |> _.AsTensor<float32>() |> Seq.toArray

                        for index in 0 .. latent.Length - 1 do
                            latent[index] <- latent[index] + flow[index] * float32 dt

                    onLatent latent
                    current <- latent

                step <- step + 1
        finally
            PocketV2State.dispose state

    let synthesizeText
        (active: PocketV2Sessions)
        (metadata: PocketV2Bundle)
        (voice: string)
        (text: string)
        (emitChunk: float32 array -> Task)
        (cancellationToken: CancellationToken)
        =
        let rng = Random options.Seed
        let allAudio = ResizeArray<float32>()
        let baseState, disposeBaseState = voiceState active metadata voice

        try
            for chunkText in textChunks active metadata text do
                let mutable decoderState =
                    PocketV2State.initialize metadata.MimiState |> ManagedState

                let pending = ResizeArray<float32 array>()
                let mutable emittedFirst = false

                try
                    let decodePending () =
                        if pending.Count > 0 then
                            let values = pending |> Seq.collect id |> Seq.toArray
                            let frameCount = pending.Count
                            let feeds = ResizeArray<NamedOnnxValue>()

                            feeds.Add(
                                NamedOnnxValue.CreateFromTensor(
                                    "latent",
                                    DenseTensor<float32>(values, [| 1; frameCount; metadata.LatentDim |])
                                )
                            )

                            PocketV2State.feeds metadata.MimiState decoderState |> feeds.AddRange

                            let previousState = decoderState
                            let results = active.MimiDecoder.Run feeds

                            let audio =
                                resultAt 0 results
                                |> _.AsTensor<float32>()
                                |> Seq.map AudioPcm.clamp
                                |> Seq.toArray

                            decoderState <- RunState(results, 1)
                            PocketV2State.dispose previousState
                            pending.Clear()
                            allAudio.AddRange audio
                            emitChunk audio |> _.GetAwaiter().GetResult()
                            emittedFirst <- true

                    let onLatent latent =
                        pending.Add latent

                        let threshold =
                            if emittedFirst then
                                decoderChunkFrames
                            else
                                firstChunkFrames

                        if pending.Count >= threshold then
                            decodePending ()

                    runFlowChunk active metadata baseState rng chunkText onLatent cancellationToken
                    decodePending ()
                finally
                    PocketV2State.dispose decoderState
        finally
            if disposeBaseState then
                PocketV2State.dispose baseState

        allAudio.ToArray()

    interface ITtsRuntime with
        member _.Status() =
            let missing =
                [| yield! missingFiles ()

                   let voice = configuredVoiceSample ()

                   if not (String.IsNullOrWhiteSpace voice) && not (File.Exists voice) then
                       yield voice |]

            { Ready = missing.Length = 0
              SupportsVoiceCloning = true
              SupportsStreaming = true
              Runtime = "pocket-tts-onnx-v2"
              ModelDir = bundleDir
              ExecutionProvider = executionProvider
              OutputSampleRate =
                if File.Exists bundlePath then
                    (loadBundle ()).SampleRate
                else
                    24000
              VoiceSamplePath = configuredVoiceSample ()
              MissingFiles = missing
              Message =
                if missing.Length = 0 then
                    $"Pocket TTS ONNX v2 is ready. precision={precision}; threads={numThreads}; steps={numSteps}; temperature={temperature:F2}; seed={options.Seed}; voiceSample={configuredVoiceSample ()}."
                else
                    let missingText = String.Join(", ", missing)
                    $"Pocket TTS ONNX v2 is not ready. Missing: {missingText}" }

        member this.SynthesizeAsync(request, emitChunk, cancellationToken) =
            task {
                let status = (this :> ITtsRuntime).Status()

                if not status.Ready then
                    invalidOp status.Message

                let voice = requestVoiceSample request

                if String.IsNullOrWhiteSpace voice || not (File.Exists voice) then
                    invalidOp $"Pocket TTS v2 reference voice was not found: {voice}"

                Directory.CreateDirectory request.OutputDirectory |> ignore
                let outputPath = Path.Combine(request.OutputDirectory, request.OutputFileName)
                do! synthesisGate.WaitAsync cancellationToken
                let stopwatch = Stopwatch.StartNew()

                try
                    let active = loadSessions ()
                    let metadata = loadBundle ()

                    let! samples =
                        Task.Run(
                            (fun () -> synthesizeText active metadata voice request.Text emitChunk cancellationToken),
                            cancellationToken
                        )

                    Wave.writeMono16 outputPath metadata.SampleRate samples
                    stopwatch.Stop()

                    return
                        { Phase = request.Phase
                          Text = request.Text
                          OutputPath = Some outputPath
                          SampleRate = metadata.SampleRate
                          Samples = samples.Length
                          DurationMs = float samples.Length * 1000.0 / float metadata.SampleRate
                          InferenceTimeMs = stopwatch.Elapsed.TotalMilliseconds
                          Message =
                            $"Pocket TTS ONNX v2 synthesized {samples.Length} samples with precision={precision}, steps={numSteps}, seed={options.Seed}." }
                finally
                    synthesisGate.Release() |> ignore
            }

    interface IDisposable with
        member _.Dispose() =
            lock syncRoot (fun () ->
                sessions |> Option.iter (fun value -> (value :> IDisposable).Dispose())
                voiceCache |> Seq.iter (fun value -> PocketV2State.dispose value.State)
                voiceCache.Clear()
                sessions <- None
                bundle <- None
                ())

            synthesisGate.Dispose()
