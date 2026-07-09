module FsVoice.OpenSource.Tests

open System
open System.IO
open System.Net.Http.Json
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Logging
open Xunit
open FsVoice.OpenSource
open FsVoice.OpenSource.Server

type private FakeGemmaRuntime() =
    let mutable reasoningCalls = 0

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
            let text =
                if request.Audio16k.IsSome then
                    "What time is it?"
                elif request.Tools.Length = 0 && request.Messages |> Array.exists (fun message -> message.Content.Contains("Tool being called", StringComparison.OrdinalIgnoreCase)) then
                    "Let me check."
                elif request.Messages |> Array.exists (fun message -> message.Role = GemmaChatRole.Tool) then
                    "The current time is available from the tool result."
                else
                    reasoningCalls <- reasoningCalls + 1
                    if reasoningCalls = 1 then
                        "<|tool_call>call:get_current_time{}<tool_call|>"
                    else
                        "You are welcome."

            { Text = text
              Prompt = ""
              InputTokenCount = 1
              OutputTokenIds = Array.empty
              StopReason = "fake"
              TimingsMs = Map.empty }
            |> Task.FromResult

type private FakeTtsRuntime() =
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
            [| GemmaChatMessage.system "system prompt"
               GemmaChatMessage.user "hello" |],
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
let ``Open-source runtime supports two turns and a tool call`` () =
    task {
        let workDir = Path.Combine(Path.GetTempPath(), "fsvoice-open-source-tests", Guid.NewGuid().ToString("N"))
        use runtime =
            new GemmaVoiceAgentRuntime(
                runtimeOptions workDir,
                FakeGemmaRuntime(),
                ttsRuntime = FakeTtsRuntime()
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
        Assert.True(first.AudioUrl.IsSome)
        Assert.True(agent.TryGetTurnArtifact(session.Id, first.TurnIndex, "audio.wav").IsSome)
        Assert.Contains(events, function | VoiceAgentFillerText _ -> true | _ -> false)
        Assert.Contains(events, function | VoiceAgentToolResult(_, _, _, result) -> result.Success | _ -> false)
    }

[<Fact>]
let ``Chatterbox status reports missing assets without loading models`` () =
    let options = TtsRuntimeOptions()
    options.ModelDir <- Path.Combine(Path.GetTempPath(), "missing-chatterbox-assets", Guid.NewGuid().ToString("N"))
    let runtime = new ChatterboxOnnxTtsRuntime(options, Directory.GetCurrentDirectory()) :> ITtsRuntime
    let status = runtime.Status()
    Assert.False(status.Ready)
    Assert.NotEmpty(status.MissingFiles)

type private FakeAgentRuntime() =
    let session =
        { Id = "fake"
          ServiceName = "FsVoiceOpenSource"
          Mode = "gemma-chatterbox"
          SystemPrompt = "test"
          WebRtcOfferUrl = "/api/open-source/sessions/fake/webrtc/offer"
          CreatedUtc = DateTimeOffset.UtcNow }

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
        member _.TryGetSession id = if id = session.Id then Some session else None
        member _.RunTurnAsync(_, _, _) = invalidOp "not used"
        member _.TryGetTurnArtifact(_, _, _) = None

[<Fact>]
let ``Open-source web app exposes status and session route`` () =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseTestServer() |> ignore
        let agent = FakeAgentRuntime() :> IVoiceAgentRuntime
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        use store = new OpenSourceVoiceWebRtcSessionStore(agent, loggerFactory) :> IDisposable
        let app = builder.Build()
        OpenSourceVoiceWebApp.map app agent (store :?> OpenSourceVoiceWebRtcSessionStore) |> ignore
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
