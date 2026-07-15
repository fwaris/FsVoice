module FsVoice.OpenSource.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
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

type private FakeLlamaCppHandler() =
    inherit HttpMessageHandler()

    let requests = ResizeArray<string * string>()

    member _.Requests = requests.ToArray()

    override _.SendAsync(request, _cancellationToken) =
        let body =
            if isNull request.Content then
                ""
            else
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        requests.Add((request.RequestUri.AbsolutePath, body))

        let content =
            if request.RequestUri.AbsolutePath = "/health" then
                """{"status":"ok"}"""
            else
                """{"content":"A concise answer.","tokens":[10,11,12],"tokens_predicted":3,"tokens_evaluated":17,"stop_type":"eos","timings":{"prompt_ms":4.5,"predicted_ms":8.0,"predicted_per_second":375.0}}"""

        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(content, Encoding.UTF8, "application/json")
        Task.FromResult response

type private FakeSttRuntime(?transcript: string) =
    let transcript = defaultArg transcript "What time is it?"

    interface ISttRuntime with
        member _.Status() =
            { Ready = true
              Runtime = "fake-stt"
              InputSampleRate = 24000
              OutputLanguage = "en"
              Message = "Fake STT is ready." }

        member _.TranscribeAsync(samples24k, _outputDirectory, _cancellationToken) =
            { Transcript = transcript
              InputSampleRate = 24000
              InputSamples = samples24k.Length
              DurationMs = 1.0
              Message = "Fake STT completed." }
            |> Task.FromResult

type private SequencedFakeSttRuntime() =
    let mutable turnIndex = 0

    interface ISttRuntime with
        member _.Status() =
            { Ready = true
              Runtime = "fake-stt"
              InputSampleRate = 24000
              OutputLanguage = "en"
              Message = "Fake STT is ready." }

        member _.TranscribeAsync(samples24k, _outputDirectory, _cancellationToken) =
            let currentTurn = Interlocked.Increment(&turnIndex)

            { Transcript = $"Question {currentTurn}"
              InputSampleRate = 24000
              InputSamples = samples24k.Length
              DurationMs = 1.0
              Message = "Fake STT completed." }
            |> Task.FromResult

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
                if
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

type private FakeSearchContextProvider(?requests: ResizeArray<QaContextRequest>) =
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
            requests |> Option.iter _.Add(request)

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
    options.MaxHistoryTurns <- 10
    options.Gemma.AdaptiveReasoning <- false
    options.Gemma.EnableDeterministicToolRouting <- false
    options

[<Theory>]
[<InlineData("Hello", "fast", 192, false)>]
[<InlineData("What is 2 + 2?", "fast", 192, false)>]
[<InlineData("Summarize the release notes", "balanced", 768, true)>]
[<InlineData("What time will it be 47 minutes after 3:48 PM?", "deep", 1024, true)>]
[<InlineData("Mira has twice as many books as Jo. Jo has 7 fewer than 19. How many books does Mira have?",
             "deep",
             1024,
             true)>]
let ``Adaptive reasoning assigns conservative token budgets`` transcript expectedDepth expectedTokens expectedThinking =
    let options = GemmaRuntimeOptions()
    let decision = ReasoningPolicy.decide options transcript

    Assert.Equal(expectedDepth, ReasoningDepth.name decision.Depth)
    Assert.Equal(expectedTokens, decision.MaxNewTokens)
    Assert.Equal(expectedThinking, decision.EnableThinking)

[<Theory>]
[<InlineData("What time is it?", "get_current_time")>]
[<InlineData("Show the runtime status", "get_agent_status")>]
[<InlineData("What files are loaded?", "source_inventory")>]
[<InlineData("According to the study, what was the primary outcome?", "selected_source_search")>]
let ``Deterministic routing recognizes unambiguous tool requests`` transcript expectedTool =
    let route = ReasoningPolicy.tryPreRoute transcript
    Assert.True(route.IsSome)
    Assert.Equal(expectedTool, route.Value.Name)

[<Fact>]
let ``Deterministic routing avoids the model tool-selection round`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()
        let options = runtimeOptions workDir
        options.Gemma.AdaptiveReasoning <- true
        options.Gemma.EnableDeterministicToolRouting <- true

        use runtime =
            new GemmaVoiceAgentRuntime(
                options,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 0, requests = gemmaRequests),
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime()
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
            )

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "pre-routed-time" },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        let reasoningRequests =
            gemmaRequests
            |> Seq.filter (fun request -> request.Tools.Length > 0)
            |> Seq.toArray

        Assert.Single(reasoningRequests) |> ignore
        Assert.Equal(192, reasoningRequests[0].MaxNewTokens)

        let systemPrompt =
            reasoningRequests[0].Messages
            |> Array.find (fun message -> message.Role = GemmaChatRole.System)
            |> _.Content

        Assert.False(systemPrompt.StartsWith("<|think|>", StringComparison.Ordinal))

        Assert.Equal("fast", result.Details.GetProperty("reasoningPolicy").GetProperty("depth").GetString())
        Assert.Equal(1, result.Details.GetProperty("reasoningRounds").GetArrayLength())
        let firstToolExecution = result.Details.GetProperty("toolExecutions")[0]
        Assert.True(firstToolExecution.GetProperty("preRouted").GetBoolean())
        Assert.Equal("get_current_time", result.ToolCalls[0].Name)
    }

[<Fact>]
let ``Balanced reasoning applies the tested concise prompt and budget`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()
        let options = runtimeOptions workDir
        options.Gemma.AdaptiveReasoning <- true

        use runtime =
            new GemmaVoiceAgentRuntime(
                options,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 0, requests = gemmaRequests),
                sttRuntime = FakeSttRuntime("Summarize the release notes"),
                ttsRuntime = FakeTtsRuntime()
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
            )

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "balanced-reasoning" },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        let reasoningRequest =
            gemmaRequests |> Seq.find (fun request -> request.Tools.Length > 0)

        let systemPrompt =
            reasoningRequest.Messages
            |> Array.find (fun message -> message.Role = GemmaChatRole.System)
            |> _.Content

        Assert.Equal(768, reasoningRequest.MaxNewTokens)
        Assert.StartsWith("<|think|>", systemPrompt)
        Assert.Contains(ReasoningPolicy.balancedGuidance, systemPrompt)
        Assert.Equal("balanced", result.Details.GetProperty("reasoningPolicy").GetProperty("depth").GetString())
    }

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
let ``llama cpp Gemma runtime preserves native prompt framing and timings`` () =
    task {
        let options = GemmaRuntimeOptions()
        options.LlamaCppEndpoint <- "http://llama.test:8080"
        options.LlamaCppModel <- "gemma-test.gguf"

        use handler = new FakeLlamaCppHandler()
        use client = new HttpClient(handler)
        use runtime = new GemmaLlamaCppRunner(options, client)
        let gemma = runtime :> IGemmaRuntime
        Assert.True(gemma.Status().Ready)

        let! result =
            gemma.GenerateAsync(
                { Messages =
                    [| GemmaChatMessage.system "You are concise."
                       GemmaChatMessage.user "Answer the question." |]
                  Tools =
                    [| { Name = "lookup"
                         Description = "Look up a value."
                         Parameters = Array.empty } |]
                  AddGenerationPrompt = true
                  MaxNewTokens = 32
                  Temperature = 0.0
                  TopP = 1.0
                  TopK = 0 },
                CancellationToken.None
            )

        Assert.Equal("A concise answer.", result.Text)
        Assert.Equal<int>([| 10; 11; 12 |], result.OutputTokenIds)
        Assert.Equal(17, result.InputTokenCount)
        Assert.Equal("eos", result.StopReason)
        Assert.Equal(8.0, result.TimingsMs["decodeMs"])

        let _, completionBody = handler.Requests |> Array.find (fst >> (=) "/completion")

        use payload = JsonDocument.Parse completionBody
        let prompt = payload.RootElement.GetProperty("prompt").GetString()
        Assert.False(prompt.StartsWith("<bos>", StringComparison.Ordinal))
        Assert.Contains("<|tool>declaration:lookup", prompt)
        Assert.EndsWith("<|turn>model\n", prompt)
    }

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
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
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
        Assert.True(first.Details.GetProperty("responseToFirstAnswerAudioMs").GetDouble() >= 0.0)

        Assert.Contains(
            events,
            function
            | ResponseToFirstAnswerAudio(_, _, _, durationMs) -> durationMs >= 0.0
            | _ -> false
        )

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
let ``Open-source runtime keeps a rolling ten-turn Gemma context`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()
        let options = runtimeOptions workDir
        options.MaxHistoryTurns <- 10

        use runtime =
            new GemmaVoiceAgentRuntime(
                options,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 0, finalText = "Answer", requests = gemmaRequests),
                sttRuntime = SequencedFakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime()
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
            )

        for turnIndex in 1..12 do
            let! result =
                agent.RunTurnAsync(
                    { SessionId = session.Id
                      UserAudio24k = Array.create 2400 0.02f
                      RequestId = Some $"rolling-{turnIndex}" },
                    (fun _ -> Task.CompletedTask),
                    CancellationToken.None
                )

            Assert.Equal(turnIndex, result.TurnIndex)

        let reasoningRequests =
            gemmaRequests
            |> Seq.filter (fun request -> request.Tools.Length > 0)
            |> Seq.toArray

        Assert.Equal(12, reasoningRequests.Length)

        let lastMessages = reasoningRequests[11].Messages

        let userMessages =
            lastMessages
            |> Array.filter (fun message -> message.Role = GemmaChatRole.User)
            |> Array.map _.Content

        let modelMessages =
            lastMessages |> Array.filter (fun message -> message.Role = GemmaChatRole.Model)

        Assert.Equal(11, userMessages.Length)
        Assert.Equal(10, modelMessages.Length)
        Assert.Equal("Question 2", userMessages[0])
        Assert.Equal("Question 12", userMessages[10])
        Assert.DoesNotContain("Question 1", userMessages)
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
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
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
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime(),
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
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
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime(ttsRequests)
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
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
                        sttRuntime = FakeSttRuntime(),
                        ttsRuntime = FakeTtsRuntime(),
                        report = logs.Add
                    )

                let agent = runtime :> IVoiceAgentRuntime

                let session =
                    agent.CreateSession(
                        { SystemPrompt = "You are concise."
                          Mode = "gemma-pocket-tts" }
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
                sttRuntime = FakeSttRuntime(),
                ttsRuntime = FakeTtsRuntime(ttsRequests)
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
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
let ``RAG-first supplies source context to the initial reasoning request`` () =
    task {
        let workDir =
            Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))

        let gemmaRequests = ResizeArray<GemmaGenerationRequest>()
        let retrievalRequests = ResizeArray<QaContextRequest>()
        let logs = ResizeArray<string>()
        let options = runtimeOptions workDir
        options.Gemma.EnableRagFirst <- true
        options.Gemma.RagFirstMaxResults <- 5
        options.Gemma.EnableDeterministicToolRouting <- true

        use runtime =
            new GemmaVoiceAgentRuntime(
                options,
                FakeGemmaRuntime(toolCallsBeforeAnswer = 0, requests = gemmaRequests),
                sttRuntime = FakeSttRuntime("According to the study, what was the primary outcome?"),
                ttsRuntime = FakeTtsRuntime(),
                contextProviders = [ FakeSearchContextProvider(retrievalRequests) :> IQaContextProvider ],
                report = logs.Add
            )

        let agent = runtime :> IVoiceAgentRuntime

        let session =
            agent.CreateSession(
                { SystemPrompt = "You are concise."
                  Mode = "gemma-pocket-tts" }
            )

        let! result =
            agent.RunTurnAsync(
                { SessionId = session.Id
                  UserAudio24k = Array.create 2400 0.02f
                  RequestId = Some "rag-first" },
                (fun _ -> Task.CompletedTask),
                CancellationToken.None
            )

        let reasoningRequest =
            gemmaRequests |> Seq.find (fun request -> request.Tools.Length > 0)

        let currentUserMessage =
            reasoningRequest.Messages
            |> Array.filter (fun message -> message.Role = GemmaChatRole.User)
            |> Array.last

        Assert.Single(retrievalRequests) |> ignore
        Assert.Equal("According to the study, what was the primary outcome?", retrievalRequests[0].query)
        Assert.Equal(5, retrievalRequests[0].maxResults)
        Assert.Contains("Pre-retrieved source context", currentUserMessage.Content)
        Assert.Contains("Result for According to the study, what was the primary outcome?", currentUserMessage.Content)
        Assert.Empty(result.ToolCalls)
        Assert.Contains(logs, fun log -> log.Contains("\"event\":\"rag.prefetch.completed\""))

        Assert.Contains(
            logs,
            fun log ->
                log.Contains("\"event\":\"tool.pre_routed.suppressed\"")
                && log.Contains("\"reason\":\"rag_first_context_available\"")
        )
    }

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
let ``Pocket TTS factory selects the direct ONNX runtime without loading models`` () =
    let options = TtsRuntimeOptions()
    options.ExecutionProvider <- "cpu"
    options.Precision <- "int8"
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-assets", Guid.NewGuid().ToString("N"))
    options.VoiceSamplePath <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-voice.wav")

    use runtime = new PocketTtsOnnxV2Runtime(options, Directory.GetCurrentDirectory())

    let status = (runtime :> ITtsRuntime).Status()
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
    options.ExecutionProvider <- "cpu"
    options.Precision <- precision
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-assets", Guid.NewGuid().ToString("N"))
    options.VoiceSamplePath <- Path.Combine(Path.GetTempPath(), "missing-pocket-tts-v2-voice.wav")

    use runtime = new PocketTtsOnnxV2Runtime(options, Directory.GetCurrentDirectory())

    let status = (runtime :> ITtsRuntime).Status()

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
        options.ExecutionProvider <- "cpu"
        options.Precision <- "int8"
        options.ModelDir <- root
        options.VoiceSamplePath <- voicePath

        use runtime = new PocketTtsOnnxV2Runtime(options, root)
        let status = (runtime :> ITtsRuntime).Status()
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

type private FakeAgentRuntime(?isReady: bool) =
    let mutable turnIndex = 0
    let mutable lastInputSamples = 0
    let isReady = defaultArg isReady true

    let session =
        { Id = "fake"
          ServiceName = "FsVoiceOpenSource"
          Mode = "gemma-pocket-tts"
          SystemPrompt = "test"
          WebRtcOfferUrl = "/api/open-source/sessions/fake/webrtc/offer"
          CreatedUtc = DateTimeOffset.UtcNow }

    member _.LastInputSamples = lastInputSamples

    interface IVoiceAgentRuntime with
        member _.MaxTurnAudioSamples24k = 720000

        member _.Status() =
            let gemma =
                { Ready = isReady
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

            { Ready = isReady
              ServiceName = "FsVoiceOpenSource"
              Mode = "gemma-pocket-tts"
              WorkDir = "fake"
              MaxHistoryTurns = 10
              MaxTurnAudioSeconds = 30
              MaxTurnAudioSamples24k = 720000
              Gemma = gemma
              Stt = stt
              Tts = tts
              Index =
                { Ready = true
                  BundleDirectory = "fake"
                  BundleId = "fake-index"
                  BundleVersion = "1.0.0"
                  ModelId = "fake-model"
                  SourceCount = 1
                  Message = "ok" }
              Message = if isReady then "ready" else "not ready" }

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

type private EnergyVadSession() =
    interface IVadSession with
        member _.Reset() = ()

        member _.SpeechProbability(samples16k) =
            let meanSquare = samples16k |> Array.averageBy (fun sample -> sample * sample)
            if meanSquare >= 0.0001f then 0.9f else 0.0f

type private FakeVadRuntime(?isReady: bool) =
    let isReady = defaultArg isReady true

    interface IVadRuntime with
        member _.Status() =
            { Ready = isReady
              Runtime = "fake-vad"
              ModelPath = "fake-vad.onnx"
              ModelVersion = "test"
              ExecutionProvider = "cpu"
              InputSampleRate = 16000
              FrameSamples = 512
              AllowBargeIn = true
              Threshold = 0.5
              NegativeThreshold = 0.35
              MinSpeechDurationMs = 250
              MinSilenceDurationMs = 700
              PreRollMs = 300
              SpeechPadMs = 100
              Message = if isReady then "ready" else "not ready" }

        member _.CreateSession() = EnergyVadSession() :> IVadSession

type private CancelableAgentRuntime() =
    let inner = FakeAgentRuntime() :> IVoiceAgentRuntime

    let started =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let canceled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable runCount = 0

    member _.RunCount = Volatile.Read(&runCount)
    member _.Started = started.Task
    member _.Canceled = canceled.Task

    interface IVoiceAgentRuntime with
        member _.MaxTurnAudioSamples24k = inner.MaxTurnAudioSamples24k
        member _.Status() = inner.Status()
        member _.CreateSession request = inner.CreateSession request
        member _.TryGetSession id = inner.TryGetSession id

        member _.TryGetTurnArtifact(sessionId, turnIndex, fileName) =
            inner.TryGetTurnArtifact(sessionId, turnIndex, fileName)

        member _.RunTurnAsync(_, _, cancellationToken) =
            task {
                Interlocked.Increment(&runCount) |> ignore
                started.TrySetResult() |> ignore

                try
                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                    return invalidOp "The controlled test turn should be canceled."
                finally
                    if cancellationToken.IsCancellationRequested then
                        canceled.TrySetResult() |> ignore
            }

type private ScriptedVadSession(probabilities: float32 list) =
    let values = Queue<float32>(probabilities)

    interface IVadSession with
        member _.Reset() = ()

        member _.SpeechProbability(_) =
            if values.Count > 0 then values.Dequeue() else 0.0f

[<Fact>]
let ``Streaming resampler preserves interpolation across chunk boundaries`` () =
    let input = Array.init 1000 float32
    let expected = AudioPcm.resampleLinear 48000 24000 input
    let resampler = AudioPcm.StreamingLinearResampler(48000, 24000)

    let actual =
        [ input[0..126]; input[127..333]; input[334..700]; input[701..] ]
        |> List.collect (resampler.Append >> Array.toList)
        |> List.toArray

    Assert.Equal<float32>(expected, actual)

[<Fact>]
let ``VAD endpoint confirms speech and trims endpointing silence`` () =
    let options = VadRuntimeOptions()
    options.MinSpeechDurationMs <- 64
    options.MinSilenceDurationMs <- 64
    options.PreRollMs <- 32
    options.SpeechPadMs <- 32
    let vad = ScriptedVadSession([ 0.9f; 0.9f; 0.0f; 0.0f ]) :> IVadSession
    let endpoint = VoiceActivityEndpoint(vad, options, 24000)

    let events =
        [| Array.create 768 0.1f
           Array.create 768 0.1f
           Array.zeroCreate 768
           Array.zeroCreate 768 |]
        |> Array.collect endpoint.Append

    Assert.Contains(SpeechStarted, events)

    match
        events
        |> Array.tryPick (function
            | SpeechStopped(samples, durationMs, Silence) -> Some(samples, durationMs)
            | _ -> None)
    with
    | None -> failwith "Expected a silence endpoint."
    | Some(samples, durationMs) ->
        Assert.Equal(2304, samples.Length)
        Assert.Equal(96.0, durationMs, 3)

[<Fact>]
let ``VAD endpoint rejects speech shorter than the configured minimum`` () =
    let options = VadRuntimeOptions()
    options.MinSpeechDurationMs <- 96
    options.MinSilenceDurationMs <- 32
    let vad = ScriptedVadSession([ 0.9f; 0.0f; 0.0f ]) :> IVadSession
    let endpoint = VoiceActivityEndpoint(vad, options, 24000)

    let events =
        [| Array.create 768 0.1f; Array.zeroCreate 768; Array.zeroCreate 768 |]
        |> Array.collect endpoint.Append

    Assert.Empty events

[<Fact>]
let ``VAD endpoint enforces the maximum turn duration`` () =
    let options = VadRuntimeOptions()
    options.MinSpeechDurationMs <- 32
    options.PreRollMs <- 0
    let vad = ScriptedVadSession([ 0.9f; 0.9f ]) :> IVadSession
    let endpoint = VoiceActivityEndpoint(vad, options, 1536)

    let events =
        [| Array.create 768 0.1f; Array.create 768 0.1f |]
        |> Array.collect endpoint.Append

    Assert.Contains(SpeechStarted, events)

    match
        events
        |> Array.tryPick (function
            | SpeechStopped(samples, _, MaxDuration) -> Some samples
            | _ -> None)
    with
    | None -> failwith "Expected a maximum-duration endpoint."
    | Some samples -> Assert.Equal(1536, samples.Length)

[<Fact>]
let ``Silero VAD runtime reports a missing external model clearly`` () =
    let options = VadRuntimeOptions()
    options.ModelPath <- Path.Combine("missing", Guid.NewGuid().ToString("N"), "silero_vad.onnx")

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            use _runtime = new SileroVadRuntime(options, Path.GetTempPath())
            ())

    Assert.Contains("download-silero-vad-onnx-assets.ps1", error.Message)

[<Fact>]
let ``Silero VAD runtime rejects an incompatible ONNX model`` () =
    let root =
        Path.Combine(Path.GetTempPath(), $"fsvoice-vad-corrupt-{Guid.NewGuid():N}")

    Directory.CreateDirectory root |> ignore
    let modelPath = Path.Combine(root, "silero_vad.onnx")
    File.WriteAllBytes(modelPath, [| 1uy; 2uy; 3uy |])

    try
        let options = VadRuntimeOptions()
        options.ModelPath <- modelPath

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                use _runtime = new SileroVadRuntime(options, root)
                ())

        Assert.Contains("could not be loaded", error.Message)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``Silero VAD official model passes startup inference when supplied`` () =
    match Environment.GetEnvironmentVariable("FSVOICE_TEST_SILERO_MODEL") with
    | null
    | "" -> ()
    | modelPath ->
        let options = VadRuntimeOptions()
        options.ModelPath <- modelPath
        use runtime = new SileroVadRuntime(options, Directory.GetCurrentDirectory())
        let vad = runtime :> IVadRuntime
        let probability = vad.CreateSession().SpeechProbability(Array.zeroCreate 512)
        Assert.True(vad.Status().Ready)
        Assert.InRange(probability, 0.0f, 1.0f)

[<Fact>]
let ``VAD barge-in cancels an active response after confirmed speech`` () =
    task {
        let options = OpenSourceVoiceOptions()
        options.Vad.AllowBargeIn <- true
        options.Vad.MinSpeechDurationMs <- 32
        options.Vad.MinSilenceDurationMs <- 32
        options.Vad.PreRollMs <- 0
        options.Vad.SpeechPadMs <- 0
        let agent = CancelableAgentRuntime()

        let session =
            (agent :> IVoiceAgentRuntime).CreateSession { SystemPrompt = "test"; Mode = "test" }

        let vad = FakeVadRuntime() :> IVadRuntime
        use loggerFactory = LoggerFactory.Create(fun _ -> ())
        let events = ResizeArray<VadEndpointEvent>()

        use coordinator =
            new OpenSourceVoiceTurnCoordinator(
                agent,
                vad,
                options,
                session,
                events.Add,
                (fun _ _ -> Task.CompletedTask),
                raise,
                loggerFactory.CreateLogger("vad-test")
            )

        coordinator.Append24k(Array.create 768 0.1f)
        coordinator.Append24k(Array.zeroCreate 768)
        do! agent.Started.WaitAsync(TimeSpan.FromSeconds 2.0)
        coordinator.Append24k(Array.create 768 0.1f)
        do! agent.Canceled.WaitAsync(TimeSpan.FromSeconds 2.0)
        Assert.Equal(1, agent.RunCount)
        Assert.Equal(2, events |> Seq.filter ((=) SpeechStarted) |> Seq.length)
    }

[<Fact>]
let ``VAD half duplex ignores microphone audio while the agent is responding`` () =
    task {
        let options = OpenSourceVoiceOptions()
        options.Vad.AllowBargeIn <- false
        options.Vad.MinSpeechDurationMs <- 32
        options.Vad.MinSilenceDurationMs <- 32
        options.Vad.PreRollMs <- 0
        options.Vad.SpeechPadMs <- 0
        let agent = CancelableAgentRuntime()

        let session =
            (agent :> IVoiceAgentRuntime).CreateSession { SystemPrompt = "test"; Mode = "test" }

        let vad = FakeVadRuntime() :> IVadRuntime
        use loggerFactory = LoggerFactory.Create(fun _ -> ())
        let events = ResizeArray<VadEndpointEvent>()

        use coordinator =
            new OpenSourceVoiceTurnCoordinator(
                agent,
                vad,
                options,
                session,
                events.Add,
                (fun _ _ -> Task.CompletedTask),
                raise,
                loggerFactory.CreateLogger("vad-test")
            )

        coordinator.Append24k(Array.create 768 0.1f)
        coordinator.Append24k(Array.zeroCreate 768)
        do! agent.Started.WaitAsync(TimeSpan.FromSeconds 2.0)
        coordinator.Append24k(Array.create 768 0.1f)
        coordinator.Append24k(Array.zeroCreate 768)
        do! Task.Delay 100
        Assert.Equal(1, agent.RunCount)
        Assert.Single(events |> Seq.filter ((=) SpeechStarted)) |> ignore
        coordinator.Cancel()
        do! agent.Canceled.WaitAsync(TimeSpan.FromSeconds 2.0)
    }

[<Fact>]
let ``Open-source web app exposes status and session route`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let fakeAgent = FakeAgentRuntime()
        let agent = fakeAgent :> IVoiceAgentRuntime
        let vad = FakeVadRuntime() :> IVadRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let options = OpenSourceVoiceOptions()

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, vad, options, loggerFactory) :> IDisposable

        let app = builder.Build()

        OpenSourceVoiceWebApp.map app agent vad options (store :?> OpenSourceVoiceWebRtcSessionStore)
        |> ignore

        do! app.StartAsync()
        let client = app.GetTestClient()

        let! status = client.GetFromJsonAsync<JsonElement>("/api/status")
        Assert.True(status.GetProperty("ready").GetBoolean())
        Assert.True(status.GetProperty("vad").GetProperty("ready").GetBoolean())
        Assert.Equal("local", status.GetProperty("assets").GetProperty("mode").GetString())
        Assert.Equal("fake-index", status.GetProperty("index").GetProperty("bundleId").GetString())

        let! readyResponse = client.GetAsync("/healthz/ready")
        readyResponse.EnsureSuccessStatusCode() |> ignore

        let! page = client.GetStringAsync("/")
        Assert.Contains("Response → first answer audio", page)
        Assert.Contains("firstAnswerAudioMetric", page)
        Assert.Contains("id=\"mic\"", page)
        Assert.DoesNotContain("Start Turn", page)
        Assert.DoesNotContain("End Turn", page)

        let! response =
            client.PostAsJsonAsync(
                "/api/open-source/sessions",
                {| systemPrompt = "hello"
                   mode = "gemma-pocket-tts" |}
            )

        response.EnsureSuccessStatusCode() |> ignore
        let! created = response.Content.ReadFromJsonAsync<JsonElement>()
        Assert.Equal("fake", created.GetProperty("id").GetString())
    }

[<Fact>]
let ``Open-source readiness returns service unavailable while a runtime is degraded`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let agent = FakeAgentRuntime(false) :> IVoiceAgentRuntime
        let vad = FakeVadRuntime() :> IVadRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let options = OpenSourceVoiceOptions()

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, vad, options, loggerFactory) :> IDisposable

        let app = builder.Build()

        OpenSourceVoiceWebApp.map app agent vad options (store :?> OpenSourceVoiceWebRtcSessionStore)
        |> ignore

        do! app.StartAsync()
        let client = app.GetTestClient()
        let! response = client.GetAsync("/healthz/ready")
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode)
        let! payload = response.Content.ReadFromJsonAsync<JsonElement>()
        Assert.False(payload.GetProperty("ready").GetBoolean())
        Assert.False(payload.GetProperty("gemmaReady").GetBoolean())
        Assert.True(payload.GetProperty("vadReady").GetBoolean())
        Assert.True(payload.GetProperty("indexReady").GetBoolean())
    }

[<Fact>]
let ``Open-source readiness includes degraded VAD state`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let agent = FakeAgentRuntime() :> IVoiceAgentRuntime
        let vad = FakeVadRuntime(false) :> IVadRuntime
        let options = OpenSourceVoiceOptions()
        use loggerFactory = LoggerFactory.Create(fun _ -> ())

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, vad, options, loggerFactory)

        let app = builder.Build()
        OpenSourceVoiceWebApp.map app agent vad options store |> ignore
        do! app.StartAsync()
        let client = app.GetTestClient()
        let! response = client.GetAsync("/healthz/ready")
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode)
        let! payload = response.Content.ReadFromJsonAsync<JsonElement>()
        Assert.False(payload.GetProperty("ready").GetBoolean())
        Assert.False(payload.GetProperty("vadReady").GetBoolean())
        Assert.True(payload.GetProperty("gemmaReady").GetBoolean())
    }

[<Fact>]
let ``Open-source WebSocket fallback carries microphone PCM and agent events`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        builder.Services.AddLogging() |> ignore
        let fakeAgent = FakeAgentRuntime()
        let agent = fakeAgent :> IVoiceAgentRuntime
        let vad = FakeVadRuntime() :> IVadRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let options = OpenSourceVoiceOptions()

        use store =
            new OpenSourceVoiceWebRtcSessionStore(agent, vad, options, loggerFactory) :> IDisposable

        let app = builder.Build()

        OpenSourceVoiceWebApp.map app agent vad options (store :?> OpenSourceVoiceWebRtcSessionStore)
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
        let readyJson = Encoding.UTF8.GetString readyBytes
        Assert.Contains("session.ready", readyJson)
        Assert.Contains("server_vad", readyJson)

        do! sendText "{\"type\":\"audio.config\",\"sampleRate\":48000}"

        let samples =
            Array.concat
                [ Array.zeroCreate<float32> 14400
                  Array.init 16800 (fun index -> if index % 2 = 0 then 0.05f else -0.05f)
                  Array.zeroCreate<float32> 38400 ]

        let pcmBytes = Array.zeroCreate<byte> (samples.Length * sizeof<float32>)
        Buffer.BlockCopy(samples, 0, pcmBytes, 0, pcmBytes.Length)
        do! socket.SendAsync(ArraySegment<byte>(pcmBytes), WebSocketMessageType.Binary, true, CancellationToken.None)

        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        let mutable doneReceived = false
        let mutable audioReceived = false
        let mutable speechStarted = false
        let mutable speechStopped = false

        while not doneReceived do
            let buffer = Array.zeroCreate<byte> 16384
            let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), timeout.Token)

            if result.MessageType = WebSocketMessageType.Binary then
                audioReceived <-
                    result.Count >= 12
                    && buffer[0..3] = [| byte 'F'; byte 'S'; byte 'A'; byte '1' |]
            elif result.MessageType = WebSocketMessageType.Text then
                let text = Encoding.UTF8.GetString(buffer, 0, result.Count)
                speechStarted <- speechStarted || text.Contains("vad.speech_started")
                speechStopped <- speechStopped || text.Contains("vad.speech_stopped")
                doneReceived <- text.Contains("agent.done")

        Assert.True(audioReceived)
        Assert.True(speechStarted)
        Assert.True(speechStopped)
        Assert.InRange(fakeAgent.LastInputSamples, 15000, 20000)
        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
    }
