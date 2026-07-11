module FsVoice.OpenSource.Tests

open System
open System.IO
open System.Net.Http.Json
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open FsVoice.Ctx
open FsVoice.OpenSource
open FsVoice.OpenSource.Server

type private FakeGemmaRuntime
    (
        ?fillerText: string,
        ?toolCallsBeforeAnswer: int,
        ?finalText: string,
        ?wrapThoughts: bool,
        ?requests: ResizeArray<GemmaGenerationRequest>
    ) =
    let mutable toolCallsEmitted = 0
    let fillerText = defaultArg fillerText "Let me check the current time."
    let toolCallsBeforeAnswer = defaultArg toolCallsBeforeAnswer 1

    let finalText =
        defaultArg finalText "The current time is available from the tool result."

    let wrapThoughts = defaultArg wrapThoughts false

    let response content =
        if wrapThoughts then
            $"<|channel>thought\nPrivate reasoning for the current generation.<channel|>{content}"
        else
            content

    let toolCall () =
        toolCallsEmitted <- toolCallsEmitted + 1

        response (
            "<|tool_call>call:get_current_time{spoken_filler:<|\"|>"
            + fillerText
            + "<|\"|>}<tool_call|>"
        )

    interface IGemmaRuntime with
        member _.Status() =
            { Ready = true
              ModelDir = "fake-gemma"
              Variant = "fake"
              ExecutionProvider = "cpu"
              MissingFiles = Array.empty
              LoadedSessions = [| "fake" |]
              Message = "Fake Gemma is ready." }

        member _.GenerateAsync(request, _cancellationToken) =
            requests |> Option.iter (fun values -> values.Add request)

            let text =
                if request.Audio16k.IsSome then
                    response "What time is it?"
                elif
                    request.Tools.Length = 0
                    && request.Messages
                       |> Array.exists (fun message ->
                           message.Content.Contains("Tool being called", StringComparison.OrdinalIgnoreCase))
                then
                    response "Let me check."
                elif
                    request.Messages
                    |> Array.exists (fun message -> message.Role = GemmaChatRole.Tool)
                then
                    if toolCallsEmitted < toolCallsBeforeAnswer then
                        toolCall ()
                    else
                        response finalText
                else if toolCallsEmitted < toolCallsBeforeAnswer then
                    toolCall ()
                else
                    response finalText

            { Text = text
              Prompt = ""
              InputTokenCount = 1
              OutputTokenIds = Array.empty
              StopReason = "fake"
              TimingsMs = Map.empty }
            |> Task.FromResult

type private FakeTtsRuntime(?requests: ResizeArray<TtsSynthesisRequest>) =
    interface ITtsRuntime with
        member _.Status() =
            { Ready = true
              SupportsVoiceCloning = true
              SupportsStreaming = false
              Runtime = "fake-tts"
              ModelDir = "fake"
              ExecutionProvider = "cpu"
              OutputSampleRate = 24000
              VoiceSamplePath = ""
              MissingFiles = Array.empty
              Message = "Fake TTS is ready." }

        member _.SynthesizeAsync(request, emitChunk, _cancellationToken) =
            task {
                requests |> Option.iter (fun values -> values.Add request)
                let samples = Array.init 240 (fun index -> if index % 2 = 0 then 0.1f else -0.1f)
                Directory.CreateDirectory request.OutputDirectory |> ignore
                let path = Path.Combine(request.OutputDirectory, request.OutputFileName)
                Wave.writeMono16 path 24000 samples
                do! emitChunk samples

                return
                    { Phase = request.Phase
                      Text = request.Text
                      OutputPath = Some path
                      SampleRate = 24000
                      Samples = samples.Length
                      DurationMs = 10.0
                      InferenceTimeMs = 1.0
                      Message = "Fake TTS completed." }
            }

type private FakeSearchContextProvider() =
    let source =
        { kind = Pdf
          location = "fake.pdf"
          enabled = true }

    interface IQaContextProvider with
        member _.ProviderId = "fake.search"
        member _.DisplayName = "Fake Search"
        member _.Sources = [ source ]
        member _.LoadAsync _ = Task.FromResult([])

        member _.RetrieveAsync(request, _) =
            [ { source = source
                index = 3
                sectionPath = []
                contentRole = SourceContentRole.Unknown
                pageNumbers = [ 4 ]
                layoutLabels = []
                captions = []
                text = $"Result for {request.query}"
                score = 0.9f } ]
            |> Task.FromResult

        member _.InventoryAsync _ = Task.FromResult("fake.pdf")
        member _.DisposeAsync() = ValueTask()

let private runtimeOptions workDir =
    let options = OpenSourceVoiceOptions()
    options.WorkDir <- workDir
    options.MaxHistoryTurns <- 8
    options.Tts.Runtime <- "chatterbox-onnx"
    options

[<Fact>]
let ``Gemma processor renders tool declarations and parses tool calls`` () =
    let processor = GemmaProcessor()

    let prompt =
        processor.RenderChat(
            [| GemmaChatMessage.system "system prompt"; GemmaChatMessage.user "hello" |],
            [| { Name = "get_current_time"
                 Description = "Return time"
                 Parameters = Array.empty } |],
            true
        )

    Assert.Contains("<|turn>system", prompt)
    Assert.Contains("<|tool>declaration:get_current_time", prompt)
    Assert.Contains("<|turn>model", prompt)

    let call =
        processor.TryParseToolCall("""<|tool_call>call:get_current_time{}<tool_call|>""")

    Assert.True(call.IsSome)
    Assert.Equal("get_current_time", call.Value.Name)

[<Fact>]
let ``Gemma response parser separates official thought channel from public content`` () =
    let response =
        "<|channel>thought\nThinking Process:\n1. Analyze the Request.<channel|>The public answer."

    match GemmaResponse.parse response with
    | Ok parsed ->
        Assert.Equal(Some "Thinking Process:\n1. Analyze the Request.", parsed.Thought)
        Assert.Equal("The public answer.", parsed.Content)
    | Error error -> failwith $"Expected a parsed Gemma response, got {error}."

[<Fact>]
let ``Gemma response parser exposes tool call only as public content`` () =
    let response =
        "<|channel>thought\nI should call the time tool.<channel|><|tool_call>call:get_current_time{}<tool_call|>"

    match GemmaResponse.parse response with
    | Ok parsed ->
        Assert.Equal(Some "I should call the time tool.", parsed.Thought)
        Assert.StartsWith("<|tool_call>", parsed.Content)
    | Error error -> failwith $"Expected a parsed Gemma tool response, got {error}."

[<Theory>]
[<InlineData("")>]
[<InlineData("<|channel>thought\nunclosed")>]
[<InlineData("orphan<channel|>answer")>]
[<InlineData("prefix<|channel>thought\nprivate<channel|>answer")>]
[<InlineData("<|channel>thought\none<channel|><|channel>thought\ntwo<channel|>answer")>]
[<InlineData("<|channel>thought\nprivate<channel|>")>]
[<InlineData("Thinking Process: 1. Analyze the Request.")>]
let ``Gemma response parser rejects unsafe response framing`` response =
    Assert.True(GemmaResponse.parse response |> Result.isError)

[<Fact>]
let ``Open-source runtime supports two turns and a tool call`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let logs = ResizeArray<string>()

        use runtime =
            new GemmaVoiceAgentRuntime(
                runtimeOptions workDir,
                FakeGemmaRuntime(),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-chatterbox" }
            )

        let events = ResizeArray<VoiceAgentStreamingEvent>()

        let emit event =
            events.Add event
            Task.CompletedTask

        let userAudio = Array.create 2400 0.02f

        let! first =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = userAudio
                  RequestId = Some "turn-1" },
                emit,
                CancellationToken.None
            )

        let! second =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = userAudio
                  RequestId = Some "turn-2" },
                emit,
                CancellationToken.None
            )

        Assert.Equal(1, first.TurnIndex)
        Assert.Equal(2, second.TurnIndex)
        Assert.Contains(first.ToolCalls, fun call -> call.Name = "get_current_time")
        Assert.DoesNotContain(first.ToolCalls, fun call -> call.Arguments.ContainsKey "spoken_filler")
        Assert.True(first.AudioUrl.IsSome)
        Assert.True(agent.TryGetTurnArtifact(session.Id, first.TurnIndex, "audio.wav").IsSome)

        Assert.Contains(
            events,
            function
            | VoiceAgentFillerText(_, _, _, text) -> text = "Let me check the current time."
            | _ -> false
        )

        Assert.Contains(
            events,
            function
            | VoiceAgentToolResult(_, _, _, result) -> result.Success
            | _ -> false
        )

        Assert.Contains(logs, fun log -> log.Contains("\"event\":\"tool.call\""))
        Assert.Contains(logs, fun log -> log.Contains("\"event\":\"tool.result\""))

        Assert.Contains(
            logs,
            fun log ->
                log.Contains("\"event\":\"filler.selected\"")
                && log.Contains("\"source\":\"model\"")
        )
    }

[<Fact>]
let ``Structured tool filler rejects tool syntax before events and TTS`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let logs = ResizeArray<string>()

        use runtime =
            new GemmaVoiceAgentRuntime(
                runtimeOptions workDir,
                FakeGemmaRuntime(fillerText = "Searching now call:selected_source_search"),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-chatterbox" }
            )

        let events = ResizeArray<VoiceAgentStreamingEvent>()

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "unsafe-filler" },
                (fun event ->
                    events.Add event
                    Task.CompletedTask),
                CancellationToken.None
            )

        Assert.Contains(
            events,
            function
            | VoiceAgentFillerText(_, _, _, text) -> text = "Let me check the time."
            | _ -> false
        )

        let fillerTts = result.Details.GetProperty("fillerTts")[0]
        Assert.Equal("Let me check the time.", fillerTts.GetProperty("text").GetString())
        Assert.DoesNotContain("call:", fillerTts.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase)

        Assert.Contains(
            logs,
            fun log ->
                log.Contains("\"source\":\"fallback\"")
                && log.Contains("\"reason\":\"tool_syntax\"")
        )
    }

[<Fact>]
let ``Open-source runtime synthesizes one filler across multiple tool rounds`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let logs = ResizeArray<string>()
        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()

        use runtime =
            new GemmaVoiceAgentRuntime(
                runtimeOptions workDir,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 2, requests = gemmaRequests),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-chatterbox" }
            )

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "two-tools-one-filler" },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        Assert.Equal(2, result.ToolCalls.Length)
        Assert.Equal(1, result.Details.GetProperty("fillerTts").GetArrayLength())
        Assert.True(File.Exists(Path.Combine(workDir, session.Id, "turns", "0001", "filler_1.wav")))
        Assert.False(File.Exists(Path.Combine(workDir, session.Id, "turns", "0001", "filler_2.wav")))
        Assert.Contains(logs, fun log -> log.Contains("\"event\":\"filler.suppressed\"") && log.Contains("\"round\":2"))

        let reasoningRequests =
            gemmaRequests
            |> Seq.filter (fun request -> request.Tools.Length > 0)
            |> Seq.toArray

        Assert.Equal(3, reasoningRequests.Length)

        Assert.Contains(
            reasoningRequests[0].Tools |> Array.collect _.Parameters,
            fun parameter -> parameter.Name = "spoken_filler" && parameter.Required
        )

        for request in reasoningRequests |> Array.skip 1 do
            Assert.DoesNotContain(
                request.Tools |> Array.collect _.Parameters,
                fun parameter -> parameter.Name = "spoken_filler"
            )

            let systemPrompt =
                request.Messages
                |> Array.find (fun message -> message.Role = GemmaChatRole.System)

            Assert.DoesNotContain("Include spoken_filler", systemPrompt.Content)
            Assert.Contains("no surrounding public prose or status phrase", systemPrompt.Content)
    }

[<Fact>]
let ``Open-source runtime hides thoughts across ASR filler tools and final speech`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()
        let ttsRequests = ResizeArray<TtsSynthesisRequest>()
        let options = runtimeOptions workDir
        options.Gemma.UseStructuredToolFiller <- false

        use runtime =
            new GemmaVoiceAgentRuntime(
                options,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 2, wrapThoughts = true, requests = gemmaRequests),
                ttsRuntime = FakeTtsRuntime(ttsRequests)
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-chatterbox" }
            )

        let run requestId =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some requestId },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        let! first = run "hidden-thoughts-1"
        let! second = run "hidden-thoughts-2"

        Assert.Equal("What time is it?", first.Transcript)
        Assert.Equal("The current time is available from the tool result.", first.FinalText)
        Assert.DoesNotContain("Private reasoning", first.Details.ToString())
        Assert.DoesNotContain("Private reasoning", second.Details.ToString())
        Assert.DoesNotContain(ttsRequests, fun request -> request.Text.Contains("Private reasoning"))

        let reasoningRequests =
            gemmaRequests
            |> Seq.filter (fun request -> request.Tools.Length > 0)
            |> Seq.toArray

        Assert.NotEmpty(reasoningRequests)

        Assert.All(
            reasoningRequests,
            fun request ->
                Assert.StartsWith(
                    "<|think|>",
                    request.Messages
                    |> Array.find (fun message -> message.Role = GemmaChatRole.System)
                    |> _.Content
                )
        )

        Assert.Contains(
            reasoningRequests,
            fun request ->
                request.Messages
                |> Array.exists (fun message ->
                    message.Role = GemmaChatRole.Model
                    && message.Content.Contains("Private reasoning"))
        )

        let finalReasoningRequest = reasoningRequests |> Array.last

        Assert.DoesNotContain(
            finalReasoningRequest.Messages,
            fun message ->
                message.Role = GemmaChatRole.Model
                && message.Content.Contains("Private reasoning")
        )
    }

[<Fact>]
let ``Gemma thought text logging is opt in`` () =
    task {
        let run logThoughtText =
            task {
                let workDir =
                    Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

                let logs = ResizeArray<string>()
                let options = runtimeOptions workDir
                options.Gemma.LogThoughtText <- logThoughtText

                use runtime =
                    new GemmaVoiceAgentRuntime(
                        options,
                        FakeGemmaRuntime(wrapThoughts = true),
                        ttsRuntime = FakeTtsRuntime(),
                        report = logs.Add
                    )

                let agent = runtime :> IVoiceAgentRuntime

                let session =
                    agent.CreateSession(
                        { SystemPrompt = "You are concise."
                          Mode = "gemma-chatterbox" }
                    )

                let! _ =
                    agent.RunTurnAsync(
                        { SessionId = session.Id
                          UserAudio24k = Array.create 2400 0.02f
                          RequestId = Some "thought-logging" },
                        (fun _ -> Task.CompletedTask),
                        CancellationToken.None
                    )

                return logs
            }

        let! safeLogs = run false
        let! debugLogs = run true
        Assert.Contains(safeLogs, fun log -> log.Contains("\"thoughtChars\":"))
        Assert.DoesNotContain(safeLogs, fun log -> log.Contains("Private reasoning"))
        Assert.Contains(debugLogs, fun log -> log.Contains("Private reasoning"))
    }

[<Fact>]
let ``Open-source runtime cleans filler and final text before TTS`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let ttsRequests = ResizeArray<TtsSynthesisRequest>()

        use runtime =
            new GemmaVoiceAgentRuntime(
                runtimeOptions workDir,
                FakeGemmaRuntime(
                    fillerText = "\"Let me check & wait.\"",
                    finalText = "**Result:** [50%](https://example.test) & rising+\""
                ),
                ttsRuntime = FakeTtsRuntime(ttsRequests)
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-chatterbox" }
            )

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "speech-text-cleanup" },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        let expectedFiller = "Let me check and wait."
        let expectedFinal = "Result: 50 percent and rising plus"
        Assert.Equal(expectedFinal, result.FinalText)
        Assert.Equal<string>([| expectedFiller; expectedFinal |], ttsRequests |> Seq.map _.Text |> Seq.toArray)

        Assert.Equal(
            expectedFinal,
            File.ReadAllText(Path.Combine(workDir, session.Id, "turns", "0001", "final_text.txt"))
        )

        Assert.DoesNotContain("\"", result.FinalText)
        Assert.DoesNotContain("`", result.FinalText)
    }

[<Fact>]
let ``Open-source source search reports query and result count`` () =
    task {
        let logs = ResizeArray<string>()

        let catalog =
            OpenSourceTooling.create [ FakeSearchContextProvider() :> IQaContextProvider ] (fun () -> "{}") logs.Add

        let! success, result, error =
            catalog.Invoke
                "selected_source_search"
                (Map [ "question", "wearable pulse"; "max_results", "5" ])
                CancellationToken.None

        Assert.True(success, error |> Option.defaultValue "Search failed.")
        Assert.Contains("Result for wearable pulse", result)
        Assert.Contains(logs, fun log -> log.Contains("\"event\":\"search.started\"") && log.Contains("wearable pulse"))

        Assert.Contains(
            logs,
            fun log ->
                log.Contains("\"event\":\"search.completed\"")
                && log.Contains("\"resultCount\":1")
        )
    }

[<Fact>]
let ``Chatterbox status reports missing assets without loading models`` () =
    let options = TtsRuntimeOptions()
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-chatterbox-assets", Guid.NewGuid().ToString("N"))

    let runtime =
        new ChatterboxOnnxTtsRuntime(options, Directory.GetCurrentDirectory()) :> ITtsRuntime

    let status = runtime.Status()
    Assert.False(status.Ready)
    Assert.NotEmpty(status.MissingFiles)

[<Fact>]
let ``Pocket TTS v2 reference preprocessing trims only edge silence and keeps padding`` () =
    let sampleRate = 1000

    let input =
        Array.concat
            [ Array.zeroCreate<float32> 100
              Array.create 200 0.1f
              Array.zeroCreate<float32> 100 ]

    let trimmed = AudioPcm.trimEdgeSilence sampleRate -40.0 0.02 input
    Assert.Equal(240, trimmed.Length)
    Assert.All(trimmed[0..19], fun sample -> Assert.Equal(0.0f, sample))
    Assert.All(trimmed[20..219], fun sample -> Assert.Equal(0.1f, sample))
    Assert.All(trimmed[220..239], fun sample -> Assert.Equal(0.0f, sample))
    Assert.Empty(AudioPcm.trimEdgeSilence sampleRate -40.0 0.02 (Array.zeroCreate 400))

[<Fact>]
let ``Pocket TTS v2 reference preprocessing resamples to 24 kHz without changing duration`` () =
    let sourceRate = 32000
    let targetRate = 24000

    let source =
        Array.init sourceRate (fun index -> Math.Sin(2.0 * Math.PI * 220.0 * float index / float sourceRate) |> float32)

    let resampled = AudioPcm.resampleBandLimited sourceRate targetRate source
    Assert.Equal(targetRate, resampled.Length)
    Assert.DoesNotContain(resampled, fun sample -> Single.IsNaN sample || Single.IsInfinity sample)

    let peak = resampled |> Array.map abs |> Array.max
    Assert.InRange(peak, 0.95f, 1.0f)

[<Fact>]
let ``Pocket TTS factory status reports streaming and missing assets without loading models`` () =
    let options = TtsRuntimeOptions()
    options.Runtime <- "pocket-tts-onnx"
    options.ExecutionProvider <- "cpu"
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-assets", Guid.NewGuid().ToString("N"))
    options.VoiceSamplePath <- ""

    use runtime =
        TtsRuntimeFactory.create options (Directory.GetCurrentDirectory()) :?> IDisposable

    let status = (runtime :?> ITtsRuntime).Status()
    Assert.Equal("pocket-tts-onnx", status.Runtime)
    Assert.True(status.SupportsStreaming)
    Assert.True(status.SupportsVoiceCloning)
    Assert.False(status.Ready)
    Assert.NotEmpty(status.MissingFiles)

[<Theory>]
[<InlineData("pocket-tts-onnx-v2")>]
[<InlineData("pocket-tts-v2")>]
let ``Pocket TTS v2 factory aliases select the direct ONNX runtime without loading models`` runtimeName =
    let options = TtsRuntimeOptions()
    options.Runtime <- runtimeName
    options.ExecutionProvider <- "cpu"
    options.Precision <- "int8"
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-assets", Guid.NewGuid().ToString("N"))
    options.VoiceSamplePath <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-voice.wav")

    use runtime =
        TtsRuntimeFactory.create options (Directory.GetCurrentDirectory()) :?> IDisposable

    let status = (runtime :?> ITtsRuntime).Status()
    Assert.Equal("pocket-tts-onnx-v2", status.Runtime)
    Assert.Equal("cpu", status.ExecutionProvider)
    Assert.Equal(24000, status.OutputSampleRate)
    Assert.True(status.SupportsStreaming)
    Assert.True(status.SupportsVoiceCloning)
    Assert.False(status.Ready)
    Assert.NotEmpty(status.MissingFiles)

[<Theory>]
[<InlineData("int8", "flow_lm_main_int8.onnx", "flow_lm_main.onnx")>]
[<InlineData("fp32", "flow_lm_main.onnx", "flow_lm_main_int8.onnx")>]
let ``Pocket TTS v2 status selects precision-specific model assets`` precision requiredMain excludedMain =
    let options = TtsRuntimeOptions()
    options.Runtime <- "pocket-tts-onnx-v2"
    options.ExecutionProvider <- "cpu"
    options.Precision <- precision
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-assets", Guid.NewGuid().ToString("N"))
    options.VoiceSamplePath <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-voice.wav")

    use runtime =
        TtsRuntimeFactory.create options (Directory.GetCurrentDirectory()) :?> IDisposable

    let status = (runtime :?> ITtsRuntime).Status()

    let missingNames = status.MissingFiles |> Array.map Path.GetFileName |> Set.ofArray

    let requiredFlow, requiredDecoder, excludedFlow, excludedDecoder =
        if precision = "int8" then
            "flow_lm_flow_int8.onnx", "mimi_decoder_int8.onnx", "flow_lm_flow.onnx", "mimi_decoder.onnx"
        else
            "flow_lm_flow.onnx", "mimi_decoder.onnx", "flow_lm_flow_int8.onnx", "mimi_decoder_int8.onnx"

    for required in
        [ "bundle.json"
          "tokenizer.model"
          "bos_before_voice.npy"
          "mimi_encoder.onnx"
          "text_conditioner.onnx"
          requiredMain
          requiredFlow
          requiredDecoder ] do
        Assert.Contains(required, missingNames)

    Assert.DoesNotContain(excludedMain, missingNames)
    Assert.DoesNotContain(excludedFlow, missingNames)
    Assert.DoesNotContain(excludedDecoder, missingNames)

[<Fact>]
let ``Pocket TTS v2 status resolves a complete nested bundle without loading ONNX sessions`` () =
    let tempBase =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests"))

    let root = Path.GetFullPath(Path.Combine(tempBase, Guid.NewGuid().ToString("N")))
    let bundleDir = Path.Combine(root, "english_2026-04")
    let voicePath = Path.Combine(root, "voice.wav")
    Directory.CreateDirectory bundleDir |> ignore

    try
        File.WriteAllText(
            Path.Combine(bundleDir, "bundle.json"),
            """{
                "sample_rate": 24000,
                "frame_rate": 12.5,
                "latent_dim": 32,
                "conditioning_dim": 1024,
                "tokenizer_file": "tokenizer.model",
                "bos_before_voice_file": "bos_before_voice.npy",
                "flow_lm_state_manifest": [],
                "mimi_state_manifest": []
            }"""
        )

        for file in
            [ "tokenizer.model"
              "bos_before_voice.npy"
              "mimi_encoder.onnx"
              "text_conditioner.onnx"
              "flow_lm_main_int8.onnx"
              "flow_lm_flow_int8.onnx"
              "mimi_decoder_int8.onnx" ] do
            File.WriteAllBytes(Path.Combine(bundleDir, file), Array.empty)

        Wave.writeMono16 voicePath 24000 [| 0.0f |]

        let options = TtsRuntimeOptions()
        options.Runtime <- "pocket-tts-onnx-v2"
        options.ExecutionProvider <- "cpu"
        options.Precision <- "int8"
        options.ModelDir <- root
        options.VoiceSamplePath <- voicePath

        use runtime = TtsRuntimeFactory.create options root :?> IDisposable
        let status = (runtime :?> ITtsRuntime).Status()
        Assert.True(status.Ready, status.Message)
        Assert.Empty(status.MissingFiles)
        Assert.Equal(Path.GetFullPath bundleDir, status.ModelDir)
        Assert.Equal(Path.GetFullPath voicePath, status.VoiceSamplePath)
    finally
        let safePrefix =
            tempBase.TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        if
            root.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists root
        then
            Directory.Delete(root, true)

type private FakeAgentRuntime() =
    let mutable turnIndex = 0
    let mutable lastInputSamples = 0

    let session =
        { Id = "fake"
          ServiceName = "FsVoiceOpenSource"
          Mode = "gemma-chatterbox"
          SystemPrompt = "test"
          WebRtcOfferUrl = "/api/open-source/sessions/fake/webrtc/offer"
          CreatedUtc = DateTimeOffset.UtcNow }

    member _.LastInputSamples = lastInputSamples

    interface IVoiceAgentRuntime with
        member _.MaxTurnAudioSamples24k = 24000

        member _.Status() =
            let gemma =
                { Ready = true
                  ModelDir = "fake"
                  Variant = "fake"
                  ExecutionProvider = "cpu"
                  MissingFiles = Array.empty
                  LoadedSessions = Array.empty
                  Message = "ok" }

            let stt =
                { Ready = true
                  Runtime = "fake"
                  InputSampleRate = 24000
                  OutputLanguage = "auto"
                  Message = "ok" }

            let tts =
                { Ready = true
                  SupportsVoiceCloning = true
                  SupportsStreaming = false
                  Runtime = "fake"
                  ModelDir = "fake"
                  ExecutionProvider = "cpu"
                  OutputSampleRate = 24000
                  VoiceSamplePath = ""
                  MissingFiles = Array.empty
                  Message = "ok" }

            { Ready = true
              ServiceName = "FsVoiceOpenSource"
              Mode = "gemma-chatterbox"
              WorkDir = "fake"
              MaxHistoryTurns = 8
              MaxTurnAudioSeconds = 30
              MaxTurnAudioSamples24k = 24000
              Gemma = gemma
              Stt = stt
              Tts = tts
              Message = "ready" }

        member _.CreateSession _ = session

        member _.TryGetSession id =
            if id = session.Id then Some session else None

        member _.RunTurnAsync(request, emit, _) =
            task {
                turnIndex <- turnIndex + 1
                lastInputSamples <- request.UserAudio24k.Length
                let requestId = $"fake-{turnIndex}"
                do! emit (VoiceAgentTranscription(session.Id, requestId, turnIndex, "hello"))
                do! emit (VoiceAgentFinalText(session.Id, requestId, turnIndex, "Hello back."))
                do! emit (TtsAudioChunk(session.Id, requestId, turnIndex, "final", 24000, Array.create 240 0.1f))
                use details = JsonDocument.Parse("{}")

                let result =
                    { Id = session.Id
                      RequestId = requestId
                      TurnIndex = turnIndex
                      Transcript = "hello"
                      FinalText = "Hello back."
                      ToolCalls = Array.empty
                      ToolResults = Array.empty
                      AudioUrl = None
                      DetailsUrl = $"/api/open-source/sessions/{session.Id}/turns/{turnIndex}/details.json"
                      Details = details.RootElement.Clone() }

                do! emit (VoiceAgentDone result)
                return result
            }

        member _.TryGetTurnArtifact(_, _, _) = None

[<Fact>]
let ``Open-source web app exposes status and session route`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let fakeAgent = FakeAgentRuntime()
        let agent = fakeAgent :> IVoiceAgentRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let options = OpenSourceVoiceOptions()

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, options, loggerFactory) :> IDisposable

        let app = builder.Build()

        OpenSourceVoiceWebApp.map app agent (store :?> OpenSourceVoiceWebRtcSessionStore)
        |> ignore

        do! app.StartAsync()
        let client = app.GetTestClient()

        let! status = client.GetFromJsonAsync<JsonElement>("/api/status")
        Assert.True(status.GetProperty("ready").GetBoolean())

        let! response =
            client.PostAsJsonAsync(
                "/api/open-source/sessions",
                {| systemPrompt = "hello"
                   mode = "gemma-chatterbox" |}
            )

        response.EnsureSuccessStatusCode() |> ignore
        let! created = response.Content.ReadFromJsonAsync<JsonElement>()
        Assert.Equal("fake", created.GetProperty("id").GetString())
    }

[<Fact>]
let ``Open-source WebSocket fallback carries microphone PCM and agent events`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let fakeAgent = FakeAgentRuntime()
        let agent = fakeAgent :> IVoiceAgentRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let options = OpenSourceVoiceOptions()

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, options, loggerFactory) :> IDisposable

        let app = builder.Build()

        OpenSourceVoiceWebApp.map app agent (store :?> OpenSourceVoiceWebRtcSessionStore)
        |> ignore

        do! app.StartAsync()

        let client = app.GetTestServer().CreateWebSocketClient()

        use! socket =
            client.ConnectAsync(Uri("ws://localhost/api/open-source/sessions/fake/ws"), CancellationToken.None)

        let sendText (value: string) =
            let bytes = Encoding.UTF8.GetBytes value
            socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)

        let receiveMessage () =
            task {
                let buffer = Array.zeroCreate<byte> 8192
                let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), CancellationToken.None)
                return result.MessageType, buffer[0 .. result.Count - 1]
            }

        let! readyType, readyBytes = receiveMessage ()
        Assert.Equal(WebSocketMessageType.Text, readyType)
        Assert.Contains("session.ready", Encoding.UTF8.GetString readyBytes)

        do! sendText "{\"type\":\"audio.config\",\"sampleRate\":48000}"
        do! sendText "{\"type\":\"turn.start\"}"
        let! _, acceptedBytes = receiveMessage ()
        Assert.Contains("turn.accepted", Encoding.UTF8.GetString acceptedBytes)

        let samples = Array.init 4800 (fun index -> if index % 2 = 0 then 0.05f else -0.05f)
        let pcmBytes = Array.zeroCreate<byte> (samples.Length * sizeof<float32>)
        Buffer.BlockCopy(samples, 0, pcmBytes, 0, pcmBytes.Length)
        do! socket.SendAsync(ArraySegment<byte>(pcmBytes), WebSocketMessageType.Binary, true, CancellationToken.None)
        do! sendText "{\"type\":\"turn.end\"}"

        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        let mutable doneReceived = false
        let mutable audioReceived = false

        while not doneReceived do
            let buffer = Array.zeroCreate<byte> 16384
            let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), timeout.Token)

            if result.MessageType = WebSocketMessageType.Binary then
                audioReceived <-
                    result.Count >= 12
                    && buffer[0..3] = [| byte 'F'; byte 'S'; byte 'A'; byte '1' |]
            elif result.MessageType = WebSocketMessageType.Text then
                doneReceived <- Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("agent.done")

        Assert.True(audioReceived)
        Assert.Equal(2400, fakeAgent.LastInputSamples)
        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
    }
