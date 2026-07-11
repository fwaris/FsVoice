namespace FsVoice.OpenSource

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice.Ctx

type private VoiceTurn =
    { TurnIndex: int
      RequestId: string
      Transcript: string
      FinalText: string
      ToolCalls: AgentToolCallInfo array
      ToolResults: AgentToolResultInfo array
      WorkDir: string }

type private VoiceSession =
    { Id: string
      SystemPrompt: string
      Mode: string
      WorkDir: string
      CreatedUtc: DateTimeOffset
      SyncRoot: obj
      Turns: ResizeArray<VoiceTurn>
      mutable NextTurnIndex: int }

type private VoiceSessionStore(workDir: string) =
    let sessions = ConcurrentDictionary<string, VoiceSession>(StringComparer.Ordinal)

    let toInfo (session: VoiceSession) =
        { Id = session.Id
          ServiceName = "FsVoiceOpenSource"
          Mode = session.Mode
          SystemPrompt = session.SystemPrompt
          WebRtcOfferUrl = $"/api/open-source/sessions/{session.Id}/webrtc/offer"
          CreatedUtc = session.CreatedUtc }

    member _.Create(systemPrompt: string, mode: string) =
        let id = Guid.NewGuid().ToString("N")
        let sessionWorkDir = Path.Combine(workDir, id)
        Directory.CreateDirectory sessionWorkDir |> ignore

        let session =
            { Id = id
              SystemPrompt = systemPrompt
              Mode = mode
              WorkDir = sessionWorkDir
              CreatedUtc = DateTimeOffset.UtcNow
              SyncRoot = obj ()
              Turns = ResizeArray<VoiceTurn>()
              NextTurnIndex = 1 }

        sessions[id] <- session
        session

    member _.TryGet(id: string) =
        match sessions.TryGetValue id with
        | true, session -> Some session
        | false, _ -> None

    member this.TryGetInfo(id: string) = this.TryGet id |> Option.map toInfo

    member _.ToInfo(session: VoiceSession) = toInfo session

type GemmaSttRuntime
    (gemma: IGemmaRuntime, maxTokens: int, maxAudioSeconds: float, ?logThoughtText: bool, ?report: string -> unit) =
    let logThoughtText = defaultArg logThoughtText false
    let report = defaultArg report ignore

    let prompt =
        "Transcribe the following speech segment in its original language. Follow these specific instructions for formatting the answer:\n* Only output the transcription, with no newlines.\n* When transcribing numbers, write the digits, i.e. write 1.7 and not one point seven, and write 3 instead of three.\n\n<|audio|>"

    let cleanTranscript (text: string) =
        (if Object.ReferenceEquals(text, null) then "" else text).Replace("\r", " ").Replace("\n", " ").Trim()

    let reportParsedResponse (parsed: GemmaParsedResponse) =
        let thoughtChars = parsed.Thought |> Option.map _.Length |> Option.defaultValue 0

        if logThoughtText then
            report (
                JsonSerializer.Serialize(
                    {| event = "gemma.response.parsed"
                       phase = "asr"
                       thoughtPresent = parsed.Thought.IsSome
                       thoughtChars = thoughtChars
                       thought = parsed.Thought |}
                )
            )
        else
            report (
                JsonSerializer.Serialize(
                    {| event = "gemma.response.parsed"
                       phase = "asr"
                       thoughtPresent = parsed.Thought.IsSome
                       thoughtChars = thoughtChars |}
                )
            )

    interface ISttRuntime with
        member _.Status() =
            let status = gemma.Status()

            { Ready = status.Ready
              Runtime = "gemma4-audio"
              InputSampleRate = 24000
              OutputLanguage = "auto"
              Message =
                if status.Ready then
                    "Gemma audio transcription is ready."
                else
                    status.Message }

        member _.TranscribeAsync(samples24k, _outputDirectory, cancellationToken) =
            task {
                let stopwatch = Stopwatch.StartNew()
                let maxSamples = int (Math.Ceiling(maxAudioSeconds * 24000.0))

                let truncated =
                    if samples24k.Length > maxSamples then
                        samples24k[0 .. maxSamples - 1]
                    else
                        samples24k

                let userAudio16k = AudioPcm.resampleLinear 24000 16000 truncated

                let! transcriptionResult =
                    gemma.GenerateAsync(
                        { Messages = [| GemmaChatMessage.user prompt |]
                          Tools = Array.empty
                          Audio16k = Some userAudio16k
                          AddGenerationPrompt = true
                          MaxNewTokens = maxTokens
                          Temperature = 0.0
                          TopP = 1.0
                          TopK = 0 },
                        cancellationToken
                    )

                stopwatch.Stop()

                let transcript, parseMessage =
                    match GemmaResponse.parse transcriptionResult.Text with
                    | Ok parsed ->
                        reportParsedResponse parsed
                        cleanTranscript parsed.Content, "parsed"
                    | Error error ->
                        report (
                            JsonSerializer.Serialize(
                                {| event = "gemma.response.rejected"
                                   phase = "asr"
                                   reason = string error
                                   outputChars = transcriptionResult.Text.Length |}
                            )
                        )

                        "", $"rejected:{error}"

                return
                    { Transcript = transcript
                      InputSampleRate = 24000
                      InputSamples = truncated.Length
                      DurationMs = stopwatch.Elapsed.TotalMilliseconds
                      Message = $"Gemma ASR stop reason: {transcriptionResult.StopReason}; response={parseMessage}" }
            }

type GemmaVoiceAgentRuntime
    (
        options: OpenSourceVoiceOptions,
        ?gemmaRuntime: IGemmaRuntime,
        ?sttRuntime: ISttRuntime,
        ?ttsRuntime: ITtsRuntime,
        ?contextProviders: IQaContextProvider list,
        ?workDir: string,
        ?report: string -> unit
    ) =
    let pathBase =
        RuntimePaths.resolveBaseFromCandidates
            [| Directory.GetCurrentDirectory(); AppContext.BaseDirectory |]
            [| options.Gemma.ModelDir; options.Tts.ModelDir |]

    let fullPath path =
        RuntimePaths.resolveAgainst pathBase path

    let resolvedWorkDir = defaultArg workDir (fullPath options.WorkDir)
    let gemmaModelDir = fullPath options.Gemma.ModelDir
    let maxTurnAudioSeconds = Math.Max(0.1, options.MaxTurnAudioSeconds)
    let maxTurnAudioSamples24k = int (Math.Ceiling(maxTurnAudioSeconds * 24000.0))
    let maxGemmaAudioSeconds = Math.Max(0.1, options.Gemma.MaxAudioSeconds)
    let maxHistoryTurns = max 0 options.MaxHistoryTurns
    let asrMaxNewTokens = max 1 options.Gemma.AsrMaxNewTokens
    let reasoningMaxNewTokens = max 1 options.Gemma.ReasoningMaxNewTokens
    let toolMaxRounds = max 0 options.Gemma.ToolMaxRounds

    let jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let processor = GemmaProcessor()
    let store = VoiceSessionStore(resolvedWorkDir)
    let report = defaultArg report ignore

    let gemmaRuntimeKind =
        if String.IsNullOrWhiteSpace options.Gemma.Runtime then
            "raw-onnx"
        else
            options.Gemma.Runtime.Trim().ToLowerInvariant()

    let ownedGemma =
        match gemmaRuntime with
        | Some _ -> None
        | None ->
            match gemmaRuntimeKind with
            | "raw-onnx"
            | "raw-ort"
            | "onnx" ->
                let runner: IGemmaRuntime =
                    upcast
                        new GemmaOnnxRunner(
                            gemmaModelDir,
                            options.Gemma.Variant,
                            options.Gemma.ExecutionProvider,
                            options.Gemma.AudioEncoderExecutionProvider,
                            maxGemmaAudioSeconds
                        )

                Some runner
            | "ort-genai"
            | "ortgenai" ->
                Some(
                    new GemmaOrtGenAiRunner(
                        gemmaModelDir,
                        options.Gemma.Variant,
                        options.Gemma.ExecutionProvider,
                        maxGemmaAudioSeconds
                    )
                    :> IGemmaRuntime
                )
            | other ->
                invalidArg
                    (nameof options.Gemma.Runtime)
                    $"Unsupported Gemma runtime '{other}'. Use raw-onnx or ort-genai."

    let gemma = gemmaRuntime |> Option.defaultWith (fun () -> ownedGemma.Value)

    let ownedStt =
        match sttRuntime with
        | Some _ -> None
        | None ->
            Some(
                new GemmaSttRuntime(gemma, asrMaxNewTokens, maxGemmaAudioSeconds, options.Gemma.LogThoughtText, report)
                :> ISttRuntime
            )

    let stt = sttRuntime |> Option.defaultWith (fun () -> ownedStt.Value)

    let ownedTts =
        match ttsRuntime with
        | Some _ -> None
        | None -> Some(new PocketTtsOnnxV2Runtime(options.Tts, pathBase) :> ITtsRuntime)

    let tts = ttsRuntime |> Option.defaultWith (fun () -> ownedTts.Value)
    let contextProviders = defaultArg contextProviders []

    do Directory.CreateDirectory resolvedWorkDir |> ignore

    let safeId (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value |> Seq.forall (fun ch -> Char.IsLetterOrDigit ch || ch = '_' || ch = '-')

    let compactJson payload =
        JsonSerializer.Serialize(payload, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    let reportEvent payload = payload |> compactJson |> report

    let reportParsedResponse phase (parsed: GemmaParsedResponse) =
        let thoughtChars = parsed.Thought |> Option.map _.Length |> Option.defaultValue 0

        if options.Gemma.LogThoughtText then
            reportEvent
                {| event = "gemma.response.parsed"
                   phase = phase
                   thoughtPresent = parsed.Thought.IsSome
                   thoughtChars = thoughtChars
                   thought = parsed.Thought |}
        else
            reportEvent
                {| event = "gemma.response.parsed"
                   phase = phase
                   thoughtPresent = parsed.Thought.IsSome
                   thoughtChars = thoughtChars |}

    let reportRejectedResponse phase error (text: string) =
        reportEvent
            {| event = "gemma.response.rejected"
               phase = phase
               reason = string error
               outputChars =
                if Object.ReferenceEquals(text, null) then
                    0
                else
                    text.Length |}

    let jsonElement payload =
        let json = JsonSerializer.Serialize(payload, jsonOptions)
        use doc = JsonDocument.Parse json
        doc.RootElement.Clone()

    let writeDetails (path: string) payload =
        let json = JsonSerializer.Serialize(payload, jsonOptions)

        match Path.GetDirectoryName path with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        File.WriteAllText(path, json)
        jsonElement payload

    let turnDirectory (session: VoiceSession) (turnIndex: int) =
        Path.Combine(session.WorkDir, "turns", turnIndex.ToString("0000", CultureInfo.InvariantCulture))

    let reserveTurnIndex (session: VoiceSession) =
        lock session.SyncRoot (fun () ->
            let turnIndex = session.NextTurnIndex
            session.NextTurnIndex <- turnIndex + 1
            turnIndex)

    let addCompletedTurn (session: VoiceSession) turn =
        lock session.SyncRoot (fun () -> session.Turns.Add turn)

    let completedTurns (session: VoiceSession) =
        lock session.SyncRoot (fun () -> session.Turns.ToArray())

    let cleanText (text: string) =
        (if Object.ReferenceEquals(text, null) then "" else text).Trim()

    let sanitizeSpeechText (text: string) =
        let removeTrailingMarkup (value: string) =
            value.TrimEnd()
            |> Seq.rev
            |> Seq.skipWhile (fun ch -> ch = '"' || ch = ')' || ch = ']' || ch = '}' || ch = '>')
            |> Seq.rev
            |> String.Concat

        let withoutFormattingCharacters (value: string) =
            value
            |> Seq.filter (fun ch ->
                not (Char.IsControl ch)
                && CharUnicodeInfo.GetUnicodeCategory ch <> UnicodeCategory.Format)
            |> Seq.toArray
            |> String

        text
        |> cleanText
        |> WebUtility.HtmlDecode
        |> fun value -> value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ")
        |> fun value -> Regex.Replace(value, @"\[([^\]]+)\]\([^)]+\)", "$1")
        |> fun value -> value.Replace("`", "").Replace("**", "").Replace("__", "")
        |> fun value -> value.Replace("#", "").Replace("•", "")
        |> fun value ->
            value.Replace("%", " percent").Replace("°", " degrees").Replace("&", " and ").Replace("+", " plus ")
        |> fun value -> value.Replace("“", "").Replace("”", "").Replace("\"", "").Replace("‘", "'").Replace("’", "'")
        |> withoutFormattingCharacters
        |> fun value -> Regex.Replace(value, @"\s+", " ").Trim()
        |> removeTrailingMarkup
        |> fun value -> Regex.Replace(value, @"\s+", " ").Trim()

    let stripToolCall (text: string) =
        match processor.TryParseToolCall text with
        | Some call -> text.Replace(call.RawText, "").Trim()
        | None -> cleanText text

    let runtimeStatusJson () =
        let gemmaStatus = gemma.Status()
        let sttStatus = stt.Status()
        let ttsStatus = tts.Status()

        compactJson
            {| gemmaReady = gemmaStatus.Ready
               gemmaModelDir = gemmaStatus.ModelDir
               sttReady = sttStatus.Ready
               ttsReady = ttsStatus.Ready
               ttsRuntime = ttsStatus.Runtime
               ttsVoiceCloning = ttsStatus.SupportsVoiceCloning
               ttsVoiceSample = ttsStatus.VoiceSamplePath |}

    let toolCatalog = OpenSourceTooling.create contextProviders runtimeStatusJson report

    let spokenFillerArgument = "spoken_filler"

    let isSpokenFillerArgument name =
        String.Equals(name, spokenFillerArgument, StringComparison.OrdinalIgnoreCase)

    let reasoningToolDeclarationsWithFiller =
        if options.Gemma.UseStructuredToolFiller then
            toolCatalog.Declarations
            |> Array.map (fun tool ->
                { tool with
                    Parameters =
                        Array.append
                            tool.Parameters
                            [| { Name = spokenFillerArgument
                                 Description =
                                   "A short natural spoken filler phrase of 3 to 8 words with no tool syntax, tool names, braces, or implementation details."
                                 Type = "string"
                                 Required = true } |] })
        else
            toolCatalog.Declarations

    let reasoningToolDeclarations includeStructuredFiller =
        if includeStructuredFiller then
            reasoningToolDeclarationsWithFiller
        else
            toolCatalog.Declarations

    let fallbackFiller (toolName: string) =
        match toolName.Trim().ToLowerInvariant() with
        | "selected_source_search" -> "Let me check the study."
        | "source_inventory" -> "Let me check the documents."
        | "get_current_time" -> "Let me check the time."
        | "get_agent_status" -> "Let me check the system."
        | _ -> "Let me check that."

    let normalizeSpokenFiller (text: string) = sanitizeSpeechText text

    let selectStructuredFiller (call: GemmaToolCall) =
        let rawFiller =
            call.Arguments
            |> Map.toSeq
            |> Seq.tryPick (fun (name, value) -> if isSpokenFillerArgument name then Some value else None)

        let arguments =
            call.Arguments |> Map.filter (fun name _ -> not (isSpokenFillerArgument name))

        let fallback = fallbackFiller call.Name

        let selected, source, reason =
            match rawFiller |> Option.map normalizeSpokenFiller with
            | None -> fallback, "fallback", "missing"
            | Some value when String.IsNullOrWhiteSpace value -> fallback, "fallback", "empty"
            | Some value when value.Length > 96 -> fallback, "fallback", "length"
            | Some value ->
                let wordCount = Regex.Matches(value, @"\S+").Count

                let containsToolName =
                    toolCatalog.ToolNames
                    |> Array.exists (fun name -> value.Contains(name, StringComparison.OrdinalIgnoreCase))

                let containsToolSyntax =
                    value.Contains("<|", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("|>", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(value, @"\b(?:tool_)?call\s*:", RegexOptions.IgnoreCase)
                    || value.IndexOfAny([| '{'; '}'; '['; ']' |]) >= 0

                if wordCount < 3 || wordCount > 8 then
                    fallback, "fallback", "word_count"
                elif containsToolSyntax then
                    fallback, "fallback", "tool_syntax"
                elif containsToolName then
                    fallback, "fallback", "tool_name"
                else
                    value, "model", "accepted"

        { call with Arguments = arguments }, selected, source, reason

    let reasoningSystemPrompt (session: VoiceSession) includeStructuredFiller =
        let basePrompt =
            if String.IsNullOrWhiteSpace session.SystemPrompt then
                "You are a concise voice assistant. Use tools when useful. Reply with one or two short spoken sentences unless the user explicitly asks for detail."
            else
                session.SystemPrompt.Trim()

        let voicePrompt =
            basePrompt
            + "\n\nUse tools when they are useful. Content in the thought channel is private. Only content after the closed thought channel is user-facing and will be spoken verbatim through TTS. Keep the public response concise, natural, and easy to synthesize. Never place analysis, planning, drafting notes, or tool implementation details in the public response."

        let voicePrompt =
            if includeStructuredFiller then
                voicePrompt
                + " When calling a tool, place the canonical tool-call envelope after the closed thought channel with no surrounding public prose. Include spoken_filler as a short natural phrase of 3 to 8 words. The filler must not contain tool syntax, tool names, braces, or implementation details."
            else
                voicePrompt
                + " When calling a tool, place only the canonical tool-call envelope after the closed thought channel with no surrounding public prose or status phrase."

        if options.Gemma.EnableThinking then
            "<|think|>\n" + voicePrompt
        else
            voicePrompt

    let reasoningMessages (session: VoiceSession) transcript toolMessages includeStructuredFiller =
        let messages = ResizeArray<GemmaChatMessage>()
        messages.Add(GemmaChatMessage.system (reasoningSystemPrompt session includeStructuredFiller))

        completedTurns session
        |> Array.sortBy _.TurnIndex
        |> Array.rev
        |> Array.truncate maxHistoryTurns
        |> Array.rev
        |> Array.iter (fun turn ->
            messages.Add(GemmaChatMessage.user turn.Transcript)
            messages.Add(GemmaChatMessage.model turn.FinalText))

        messages.Add(GemmaChatMessage.user transcript)

        for message in toolMessages do
            messages.Add message

        messages.ToArray()

    let generateFillerText (session: VoiceSession) transcript (call: GemmaToolCall) cancellationToken =
        task {
            let! result =
                gemma.GenerateAsync(
                    { Messages =
                        [| GemmaChatMessage.system
                               "Generate exactly one short spoken filler phrase for a voice assistant while a tool is being called. No quotes. No mention of internal systems. Maximum 8 words."
                           GemmaChatMessage.user $"User said: {transcript}\nTool being called: {call.Name}" |]
                      Tools = Array.empty
                      Audio16k = None
                      AddGenerationPrompt = true
                      MaxNewTokens = 24
                      Temperature = 0.0
                      TopP = 1.0
                      TopK = 0 },
                    cancellationToken
                )

            match GemmaResponse.parse result.Text with
            | Ok parsed ->
                reportParsedResponse "filler" parsed
                let text = sanitizeSpeechText parsed.Content

                if String.IsNullOrWhiteSpace text then
                    return "Let me check that."
                else
                    return text.Replace("\r", " ").Replace("\n", " ")
            | Error error ->
                reportRejectedResponse "filler" error result.Text
                return "Let me check that."
        }

    let synthesize
        (session: VoiceSession)
        (requestId: string)
        (turnIndex: int)
        (turnDir: string)
        (phase: string)
        (outputFileName: string)
        (text: string)
        (emit: VoiceAgentStreamingEvent -> Task)
        (cancellationToken: CancellationToken)
        =
        task {
            let text =
                sanitizeSpeechText text
                |> fun value ->
                    if String.IsNullOrWhiteSpace value then
                        if String.Equals(phase, "filler", StringComparison.OrdinalIgnoreCase) then
                            fallbackFiller ""
                        else
                            "I could not produce a final answer."
                    else
                        value

            let status = tts.Status()

            if not status.Ready then
                do! emit (TtsUnavailable(session.Id, requestId, turnIndex, phase, status.Message))
                return None
            else
                do! emit (TtsSynthesisStarted(session.Id, requestId, turnIndex, phase, text))

                try
                    let voiceSample =
                        if String.IsNullOrWhiteSpace status.VoiceSamplePath then
                            None
                        else
                            Some status.VoiceSamplePath

                    let! result =
                        tts.SynthesizeAsync(
                            { Phase = phase
                              Text = text
                              OutputDirectory = turnDir
                              OutputFileName = outputFileName
                              VoiceSamplePath = voiceSample
                              VoiceSampleTranscript = None },
                            (fun samples ->
                                emit (
                                    TtsAudioChunk(
                                        session.Id,
                                        requestId,
                                        turnIndex,
                                        phase,
                                        status.OutputSampleRate,
                                        samples
                                    )
                                )),
                            cancellationToken
                        )

                    do! emit (TtsSynthesisDone(session.Id, requestId, turnIndex, result))
                    return Some result
                with
                | :? OperationCanceledException ->
                    do! emit (TtsSynthesisCanceled(session.Id, requestId, turnIndex, phase))
                    return raise (OperationCanceledException(cancellationToken))
                | ex ->
                    do! emit (TtsUnavailable(session.Id, requestId, turnIndex, phase, ex.Message))
                    return None
        }

    let normalizeMode mode =
        if String.IsNullOrWhiteSpace mode then
            "gemma-pocket-tts"
        else
            match mode.Trim().ToLowerInvariant() with
            | "open-source"
            | "opensource"
            | "gemma-tts"
            | "gemma-pocket-tts"
            | "gemma_pocket_tts" -> "gemma-pocket-tts"
            | other -> invalidArg (nameof mode) $"Unsupported open-source voice mode '{other}'. Use gemma-pocket-tts."

    interface IVoiceAgentRuntime with
        member _.MaxTurnAudioSamples24k = maxTurnAudioSamples24k

        member _.Status() =
            let gemmaStatus = gemma.Status()
            let sttStatus = stt.Status()
            let ttsStatus = tts.Status()

            { Ready = gemmaStatus.Ready && sttStatus.Ready && ttsStatus.Ready
              ServiceName = "FsVoiceOpenSource"
              Mode = "gemma-pocket-tts"
              WorkDir = resolvedWorkDir
              MaxHistoryTurns = maxHistoryTurns
              MaxTurnAudioSeconds = maxTurnAudioSeconds
              MaxTurnAudioSamples24k = maxTurnAudioSamples24k
              Gemma = gemmaStatus
              Stt = sttStatus
              Tts = ttsStatus
              Message =
                if gemmaStatus.Ready && sttStatus.Ready && ttsStatus.Ready then
                    $"FsVoice open-source backend is ready. TTS runtime: {ttsStatus.Runtime}."
                else
                    $"{gemmaStatus.Message} {sttStatus.Message} {ttsStatus.Message}" }

        member _.CreateSession(request: VoiceAgentSessionRequest) =
            let systemPrompt =
                if String.IsNullOrWhiteSpace request.SystemPrompt then
                    "You are a concise voice assistant. Use tools when useful. Reply with one or two short spoken sentences unless the user explicitly asks for detail."
                else
                    request.SystemPrompt.Trim()

            let session = store.Create(systemPrompt, normalizeMode request.Mode)
            store.ToInfo session

        member _.TryGetSession id =
            if safeId id then store.TryGetInfo id else None

        member _.RunTurnAsync(request, emit, cancellationToken) =
            task {
                if request.UserAudio24k.Length = 0 then
                    invalidArg "userAudio24k" "User turn audio is required."

                if request.UserAudio24k.Length > maxTurnAudioSamples24k then
                    invalidArg
                        "userAudio24k"
                        $"User turn audio is too large. The configured maximum is {maxTurnAudioSamples24k} Float32 samples at 24 kHz."

                match store.TryGet request.SessionId with
                | None -> return invalidArg "sessionId" "Open-source voice agent session was not found."
                | Some session ->
                    let requestId =
                        request.RequestId
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultWith (fun () -> $"{session.Id}_{Guid.NewGuid():N}")

                    let turnIndex = reserveTurnIndex session
                    let turnDir = turnDirectory session turnIndex
                    Directory.CreateDirectory turnDir |> ignore

                    File.WriteAllBytes(
                        Path.Combine(turnDir, "user_audio_24k.f32"),
                        AudioPcm.float32ToLittleEndianBytes request.UserAudio24k
                    )

                    Wave.writeMono16 (Path.Combine(turnDir, "user_audio.wav")) 24000 request.UserAudio24k

                    try
                        let! transcription = stt.TranscribeAsync(request.UserAudio24k, turnDir, cancellationToken)

                        let transcript =
                            if String.IsNullOrWhiteSpace transcription.Transcript then
                                "(no speech recognized)"
                            else
                                transcription.Transcript

                        File.WriteAllText(Path.Combine(turnDir, "transcript.txt"), transcript)
                        do! emit (VoiceAgentTranscription(session.Id, requestId, turnIndex, transcript))

                        let toolCalls = ResizeArray<AgentToolCallInfo>()
                        let toolResults = ResizeArray<AgentToolResultInfo>()
                        let toolMessages = ResizeArray<GemmaChatMessage>()
                        let fillerResults = ResizeArray<TtsSynthesisResult>()
                        let mutable finalText = ""
                        let mutable round = 0
                        let mutable fillerWasSynthesized = false
                        let mutable doneReasoning = false

                        while not doneReasoning do
                            cancellationToken.ThrowIfCancellationRequested()

                            let includeStructuredFiller = options.Gemma.UseStructuredToolFiller && round = 0

                            let! reasoning =
                                gemma.GenerateAsync(
                                    { Messages =
                                        reasoningMessages
                                            session
                                            transcript
                                            (toolMessages.ToArray())
                                            includeStructuredFiller
                                      Tools = reasoningToolDeclarations includeStructuredFiller
                                      Audio16k = None
                                      AddGenerationPrompt = true
                                      MaxNewTokens = reasoningMaxNewTokens
                                      Temperature = 0.0
                                      TopP = 1.0
                                      TopK = 0 },
                                    cancellationToken
                                )

                            match GemmaResponse.parse reasoning.Text with
                            | Error error ->
                                reportRejectedResponse "reasoning" error reasoning.Text
                                finalText <- "I could not produce a reliable final answer."
                                doneReasoning <- true
                            | Ok parsed ->
                                reportParsedResponse "reasoning" parsed

                                match processor.TryParseToolCall parsed.Content with
                                | Some rawCall when round < toolMaxRounds ->
                                    round <- round + 1

                                    let call, structuredFiller, fillerSource, fillerReason =
                                        if includeStructuredFiller then
                                            selectStructuredFiller rawCall
                                        else
                                            { rawCall with
                                                Arguments =
                                                    rawCall.Arguments
                                                    |> Map.filter (fun name _ -> not (isSpokenFillerArgument name)) },
                                            "",
                                            "suppressed",
                                            "subsequent_round"

                                    let callInfo =
                                        { Round = round
                                          Name = call.Name
                                          Arguments = call.Arguments
                                          RawText = call.RawText }

                                    toolCalls.Add callInfo

                                    reportEvent
                                        {| event = "tool.call"
                                           sessionId = session.Id
                                           requestId = requestId
                                           turnIndex = turnIndex
                                           round = round
                                           toolName = call.Name
                                           arguments = call.Arguments |}

                                    do! emit (VoiceAgentToolCall(session.Id, requestId, turnIndex, callInfo))

                                    let toolTask = toolCatalog.Invoke call.Name call.Arguments cancellationToken

                                    if fillerWasSynthesized then
                                        reportEvent
                                            {| event = "filler.suppressed"
                                               sessionId = session.Id
                                               requestId = requestId
                                               turnIndex = turnIndex
                                               round = round
                                               toolName = call.Name
                                               reason = "already_synthesized" |}
                                    else
                                        let! fillerText =
                                            if options.Gemma.UseStructuredToolFiller then
                                                Task.FromResult structuredFiller
                                            else
                                                generateFillerText session transcript call cancellationToken

                                        let fillerText =
                                            sanitizeSpeechText fillerText
                                            |> fun value ->
                                                if String.IsNullOrWhiteSpace value then
                                                    fallbackFiller call.Name
                                                else
                                                    value

                                        reportEvent
                                            {| event = "filler.selected"
                                               sessionId = session.Id
                                               requestId = requestId
                                               turnIndex = turnIndex
                                               round = round
                                               toolName = call.Name
                                               source = fillerSource
                                               reason = fillerReason
                                               text = fillerText |}

                                        do! emit (VoiceAgentFillerText(session.Id, requestId, turnIndex, fillerText))

                                        let! filler =
                                            synthesize
                                                session
                                                requestId
                                                turnIndex
                                                turnDir
                                                "filler"
                                                "filler_1.wav"
                                                fillerText
                                                emit
                                                cancellationToken

                                        filler |> Option.iter fillerResults.Add
                                        fillerWasSynthesized <- true

                                    let! success, result, error = toolTask

                                    let resultInfo =
                                        { Round = round
                                          Name = call.Name
                                          Success = success
                                          Result = if success then result else ""
                                          Error = error }

                                    toolResults.Add resultInfo

                                    reportEvent
                                        {| event = "tool.result"
                                           sessionId = session.Id
                                           requestId = requestId
                                           turnIndex = turnIndex
                                           round = round
                                           toolName = call.Name
                                           success = success
                                           resultLength = if success then result.Length else 0
                                           error = error |}

                                    do! emit (VoiceAgentToolResult(session.Id, requestId, turnIndex, resultInfo))
                                    toolMessages.Add(GemmaChatMessage.model reasoning.Text)

                                    toolMessages.Add(
                                        GemmaChatMessage.tool
                                            call.Name
                                            (if success then
                                                 result
                                             else
                                                 error |> Option.defaultValue "Tool failed.")
                                    )
                                | Some call ->
                                    finalText <- stripToolCall parsed.Content

                                    if String.IsNullOrWhiteSpace finalText then
                                        finalText <-
                                            $"I could not complete the requested tool call '{call.Name}' within the configured tool round limit."

                                    doneReasoning <- true
                                | None ->
                                    finalText <- parsed.Content
                                    doneReasoning <- true

                        if String.IsNullOrWhiteSpace finalText then
                            finalText <- "I could not produce a final answer."

                        finalText <- sanitizeSpeechText finalText

                        if String.IsNullOrWhiteSpace finalText then
                            finalText <- "I could not produce a final answer."

                        File.WriteAllText(Path.Combine(turnDir, "final_text.txt"), finalText)
                        do! emit (VoiceAgentFinalText(session.Id, requestId, turnIndex, finalText))

                        let! finalTts =
                            synthesize
                                session
                                requestId
                                turnIndex
                                turnDir
                                "final"
                                "audio.wav"
                                finalText
                                emit
                                cancellationToken

                        let audioPath = finalTts |> Option.bind _.OutputPath |> Option.filter File.Exists

                        let audioUrl =
                            audioPath
                            |> Option.map (fun _ ->
                                $"/api/open-source/sessions/{session.Id}/turns/{turnIndex}/audio.wav")

                        let detailsUrl =
                            $"/api/open-source/sessions/{session.Id}/turns/{turnIndex}/details.json"

                        let details =
                            {| id = session.Id
                               requestId = requestId
                               turnIndex = turnIndex
                               mode = session.Mode
                               transcript = transcript
                               finalText = finalText
                               toolCalls = toolCalls.ToArray()
                               toolResults = toolResults.ToArray()
                               fillerTts = fillerResults.ToArray()
                               finalTts = finalTts
                               audioUrl = audioUrl
                               stt = transcription
                               gemmaStatus = gemma.Status()
                               ttsStatus = tts.Status() |}

                        let detailsElement = writeDetails (Path.Combine(turnDir, "details.json")) details

                        let result =
                            { Id = session.Id
                              RequestId = requestId
                              TurnIndex = turnIndex
                              Transcript = transcript
                              FinalText = finalText
                              ToolCalls = toolCalls.ToArray()
                              ToolResults = toolResults.ToArray()
                              AudioUrl = audioUrl
                              DetailsUrl = detailsUrl
                              Details = detailsElement }

                        addCompletedTurn
                            session
                            { TurnIndex = turnIndex
                              RequestId = requestId
                              Transcript = transcript
                              FinalText = finalText
                              ToolCalls = toolCalls.ToArray()
                              ToolResults = toolResults.ToArray()
                              WorkDir = turnDir }

                        do! emit (VoiceAgentDone result)
                        return result
                    with :? OperationCanceledException as ex ->
                        do! emit (VoiceAgentCanceled(session.Id, Some requestId))
                        return raise ex
            }

        member _.TryGetTurnArtifact(sessionId, turnIndex, fileName) =
            if
                not (safeId sessionId)
                || turnIndex < 1
                || String.IsNullOrWhiteSpace fileName
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName.Contains(Path.DirectorySeparatorChar)
                || fileName.Contains(Path.AltDirectorySeparatorChar)
            then
                None
            else
                let contentType =
                    match fileName with
                    | "details.json" -> "application/json; charset=utf-8"
                    | "transcript.txt"
                    | "final_text.txt" -> "text/plain; charset=utf-8"
                    | file when file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) -> "audio/wav"
                    | _ -> "application/octet-stream"

                let tryArtifact path =
                    let fullPath = Path.GetFullPath path
                    let fullRoot = Path.GetFullPath resolvedWorkDir

                    let rootPrefix =
                        if fullRoot.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
                            fullRoot
                        else
                            fullRoot + string Path.DirectorySeparatorChar

                    if
                        (String.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                         || fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        && File.Exists fullPath
                    then
                        Some
                            { Path = fullPath
                              ContentType = contentType }
                    else
                        None

                match store.TryGet sessionId with
                | Some session -> tryArtifact (Path.Combine(turnDirectory session turnIndex, fileName))
                | None ->
                    let persisted =
                        Path.Combine(
                            resolvedWorkDir,
                            sessionId,
                            "turns",
                            turnIndex.ToString("0000", CultureInfo.InvariantCulture),
                            fileName
                        )

                    tryArtifact persisted

    interface IDisposable with
        member _.Dispose() =
            ownedGemma
            |> Option.iter (fun runtime ->
                match runtime with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ())

            ownedStt
            |> Option.iter (fun runtime ->
                match runtime with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ())

            ownedTts
            |> Option.iter (fun runtime ->
                match runtime with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ())
