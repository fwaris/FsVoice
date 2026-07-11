namespace FsVoice.OpenSource.Server

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open FsVoice.OpenSource

type OpenSourceVoiceWebSocketSession
    (
        agent: IVoiceAgentRuntime,
        session: VoiceAgentSessionInfo,
        socket: WebSocket,
        logger: ILogger<OpenSourceVoiceWebSocketSession>
    ) =
    let jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let sendLock = new SemaphoreSlim(1, 1)
    let syncRoot = obj()
    let inputSamples = ResizeArray<float32>()
    let mutable inputSampleRate = 48000
    let mutable recording = false
    let mutable activeTurnCancellation: CancellationTokenSource option = None

    let nullableString = function Some value -> value :> obj | None -> null

    let sendBytes (messageType: WebSocketMessageType) (bytes: byte array) (cancellationToken: CancellationToken) =
        task {
            do! sendLock.WaitAsync cancellationToken
            try
                if socket.State = WebSocketState.Open then
                    do! socket.SendAsync(ArraySegment<byte>(bytes), messageType, true, cancellationToken)
            finally
                sendLock.Release() |> ignore
        }

    let sendJson (payload: obj) (cancellationToken: CancellationToken) =
        JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions)
        |> fun bytes -> sendBytes WebSocketMessageType.Text bytes cancellationToken

    let sendAudio (phase: string) (sampleRate: int) (samples: float32 array) (cancellationToken: CancellationToken) =
        task {
            if samples.Length > 0 then
                use stream = new MemoryStream(12 + samples.Length * sizeof<float32>)
                use writer = new BinaryWriter(stream, Encoding.ASCII, true)
                writer.Write([| byte 'F'; byte 'S'; byte 'A'; byte '1' |])
                writer.Write(sampleRate)
                writer.Write(if String.Equals(phase, "final", StringComparison.OrdinalIgnoreCase) then 1uy else 0uy)
                writer.Write([| 0uy; 0uy; 0uy |])
                for sample in samples do writer.Write(sample)
                do! sendBytes WebSocketMessageType.Binary (stream.ToArray()) cancellationToken
        }

    let cancelActiveTurn () =
        activeTurnCancellation |> Option.iter (fun cts -> try cts.Cancel() with _ -> ())

    let eventPayload event =
        match event with
        | VoiceAgentTranscription(id, requestId, turnIndex, transcript) ->
            Some(box {| ``type`` = "agent.transcription"; id = id; requestId = requestId; turnIndex = turnIndex; transcript = transcript |})
        | VoiceAgentToolCall(id, requestId, turnIndex, call) ->
            Some(box {| ``type`` = "agent.tool_call"; id = id; requestId = requestId; turnIndex = turnIndex; round = call.Round; name = call.Name; arguments = call.Arguments; rawText = call.RawText |})
        | VoiceAgentToolResult(id, requestId, turnIndex, result) ->
            Some(box {| ``type`` = "agent.tool_result"; id = id; requestId = requestId; turnIndex = turnIndex; round = result.Round; name = result.Name; success = result.Success; result = result.Result; error = nullableString result.Error |})
        | VoiceAgentFillerText(id, requestId, turnIndex, value) ->
            Some(box {| ``type`` = "agent.filler_text"; id = id; requestId = requestId; turnIndex = turnIndex; text = value |})
        | VoiceAgentFinalText(id, requestId, turnIndex, value) ->
            Some(box {| ``type`` = "agent.final_text"; id = id; requestId = requestId; turnIndex = turnIndex; text = value |})
        | TtsSynthesisStarted(id, requestId, turnIndex, phase, value) ->
            Some(box {| ``type`` = $"tts.{phase}.started"; id = id; requestId = requestId; turnIndex = turnIndex; phase = phase; text = value |})
        | TtsAudioChunk(id, requestId, turnIndex, phase, sampleRate, samples) ->
            Some(box {| ``type`` = $"tts.{phase}.chunk"; id = id; requestId = requestId; turnIndex = turnIndex; phase = phase; sampleRate = sampleRate; samples = samples.Length |})
        | TtsSynthesisDone(id, requestId, turnIndex, result) ->
            Some(box {| ``type`` = $"tts.{result.Phase}.done"; id = id; requestId = requestId; turnIndex = turnIndex; phase = result.Phase; text = result.Text; outputPath = nullableString result.OutputPath; sampleRate = result.SampleRate; samples = result.Samples; durationMs = result.DurationMs; inferenceTimeMs = result.InferenceTimeMs; message = result.Message |})
        | TtsSynthesisCanceled(id, requestId, turnIndex, phase) ->
            Some(box {| ``type`` = $"tts.{phase}.canceled"; id = id; requestId = requestId; turnIndex = turnIndex; phase = phase |})
        | TtsUnavailable(id, requestId, turnIndex, phase, message) ->
            Some(box {| ``type`` = "tts.unavailable"; id = id; requestId = requestId; turnIndex = turnIndex; phase = phase; message = message |})
        | VoiceAgentDone result ->
            Some(box {| ``type`` = "agent.done"; id = result.Id; requestId = result.RequestId; turnIndex = result.TurnIndex; transcript = result.Transcript; finalText = result.FinalText; audioUrl = nullableString result.AudioUrl; detailsUrl = result.DetailsUrl; toolCalls = result.ToolCalls; toolResults = result.ToolResults |})
        | VoiceAgentCanceled(id, requestId) ->
            Some(box {| ``type`` = "generation.canceled"; id = id; requestId = nullableString requestId |})

    let emitFromAgent cancellationToken event =
        task {
            match eventPayload event with
            | Some payload -> do! sendJson payload cancellationToken
            | None -> ()

            match event with
            | TtsAudioChunk(_, _, _, phase, sampleRate, samples) ->
                do! sendAudio phase sampleRate samples cancellationToken
            | _ -> ()
        }
        :> Task

    let runTurn (samples: float32 array) sampleRate =
        task {
            let samples24k = AudioPcm.resampleLinear sampleRate 24000 samples
            if samples24k.Length = 0 then
                do! sendJson (box {| ``type`` = "error"; message = "No audio was captured for this turn." |}) CancellationToken.None
            else
                cancelActiveTurn()
                let turnCancellation = new CancellationTokenSource()
                activeTurnCancellation <- Some turnCancellation
                try
                    try
                        let! _ =
                            agent.RunTurnAsync(
                                { SessionId = session.Id; UserAudio24k = samples24k; RequestId = None },
                                emitFromAgent turnCancellation.Token,
                                turnCancellation.Token)
                        ()
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        logger.LogError(ex, "WebSocket voice turn failed for session {SessionId}.", session.Id)
                        do! sendJson (box {| ``type`` = "error"; message = ex.Message |}) CancellationToken.None
                finally
                    if activeTurnCancellation |> Option.exists (fun current -> obj.ReferenceEquals(current, turnCancellation)) then
                        activeTurnCancellation <- None
                    turnCancellation.Dispose()
        }

    let handleText (message: string) cancellationToken =
        task {
            use doc = JsonDocument.Parse message
            let eventType =
                match doc.RootElement.TryGetProperty("type") with
                | true, value -> value.GetString()
                | _ -> ""

            match eventType with
            | "audio.config" ->
                match doc.RootElement.TryGetProperty("sampleRate") with
                | true, value when value.TryGetInt32() |> fst ->
                    let rate = value.GetInt32()
                    if rate >= 8000 && rate <= 192000 then inputSampleRate <- rate
                | _ -> ()
            | "turn.start" ->
                cancelActiveTurn()
                lock syncRoot (fun () -> inputSamples.Clear(); recording <- true)
                do! sendJson (box {| ``type`` = "turn.accepted"; id = session.Id; transport = "websocket" |}) cancellationToken
            | "turn.cancel" ->
                cancelActiveTurn()
                lock syncRoot (fun () -> inputSamples.Clear(); recording <- false)
                do! sendJson (box {| ``type`` = "generation.canceled"; id = session.Id |}) cancellationToken
            | "turn.end" ->
                let samples, sampleRate =
                    lock syncRoot (fun () ->
                        recording <- false
                        let copy = inputSamples.ToArray()
                        inputSamples.Clear()
                        copy, inputSampleRate)
                Task.Run(fun () -> runTurn samples sampleRate :> Task) |> ignore
            | _ ->
                do! sendJson (box {| ``type`` = "error"; message = $"Unknown control event '{eventType}'." |}) cancellationToken
        }

    let handleBinary (bytes: byte array) =
        if bytes.Length % sizeof<float32> = 0 then
            let maxSamples =
                min
                    (int64 Int32.MaxValue)
                    (int64 agent.MaxTurnAudioSamples24k * int64 (max 1 inputSampleRate) / 24000L)
                |> int
            lock syncRoot (fun () ->
                if recording then
                    let count = min (bytes.Length / sizeof<float32>) (max 0 (maxSamples - inputSamples.Count))
                    for index in 0 .. count - 1 do
                        inputSamples.Add(BitConverter.ToSingle(bytes, index * sizeof<float32>)))

    member _.RunAsync(cancellationToken: CancellationToken) =
        task {
            let status = agent.Status()
            do! sendJson
                    (box {| ``type`` = "session.ready"; id = session.Id; mode = session.Mode; transport = "websocket"; maxTurnAudioSamples24k = agent.MaxTurnAudioSamples24k; gemmaReady = status.Gemma.Ready; sttReady = status.Stt.Ready; ttsReady = status.Tts.Ready; ttsRuntime = status.Tts.Runtime; ttsVoiceCloning = status.Tts.SupportsVoiceCloning |})
                    cancellationToken

            let receiveBuffer = Array.zeroCreate<byte> (64 * 1024)
            while socket.State = WebSocketState.Open && not cancellationToken.IsCancellationRequested do
                use message = new MemoryStream()
                let mutable finished = false
                let mutable messageType = WebSocketMessageType.Text
                while not finished do
                    let! result = socket.ReceiveAsync(ArraySegment<byte>(receiveBuffer), cancellationToken)
                    messageType <- result.MessageType
                    if result.MessageType = WebSocketMessageType.Close then
                        finished <- true
                    else
                        message.Write(receiveBuffer, 0, result.Count)
                        finished <- result.EndOfMessage

                match messageType with
                | WebSocketMessageType.Text ->
                    try do! handleText (Encoding.UTF8.GetString(message.ToArray())) cancellationToken
                    with ex -> do! sendJson (box {| ``type`` = "error"; message = ex.Message |}) cancellationToken
                | WebSocketMessageType.Binary -> handleBinary (message.ToArray())
                | WebSocketMessageType.Close ->
                    if socket.State = WebSocketState.CloseReceived then
                        do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                | _ -> ()
        }

    interface IDisposable with
        member _.Dispose() =
            cancelActiveTurn()
            sendLock.Dispose()

module OpenSourceVoiceWebSocket =
    let acceptAsync
        (agent: IVoiceAgentRuntime)
        (logger: ILogger<OpenSourceVoiceWebSocketSession>)
        (ctx: HttpContext)
        (session: VoiceAgentSessionInfo) =
        task {
            if not ctx.WebSockets.IsWebSocketRequest then
                ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                do! ctx.Response.WriteAsync("A WebSocket upgrade request is required.")
            else
                use! socket = ctx.WebSockets.AcceptWebSocketAsync()
                use connection = new OpenSourceVoiceWebSocketSession(agent, session, socket, logger)
                do! connection.RunAsync(ctx.RequestAborted)
        }
