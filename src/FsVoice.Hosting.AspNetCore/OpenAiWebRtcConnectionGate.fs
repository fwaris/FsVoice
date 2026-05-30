namespace FsVoice.Hosting.AspNetCore

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type OpenAiWebRtcConnectAttemptContext =
    { attempt: int
      queueWaitMs: int64
      cancellationToken: CancellationToken }

type OpenAiWebRtcConnectResult<'T> =
    { value: 'T
      queueWaitMs: int64
      attemptCount: int }

exception OpenAiWebRtcConnectFailedException of string * int * int64 * exn

type IOpenAiWebRtcConnectionGate =
    abstract RunWithRetriesAsync<'T> :
        sessionId: string *
        purpose: string *
        connect: (OpenAiWebRtcConnectAttemptContext -> Task<'T>) *
        failureStage: (unit -> string option) *
        cancellationToken: CancellationToken ->
            Task<OpenAiWebRtcConnectResult<'T>>

type OpenAiWebRtcConnectionGate(options: IOptions<OpenAiRealtimeOptions>, logger: ILogger<OpenAiWebRtcConnectionGate>) =
    let maxConcurrent = OpenAiRealtimeOptions.webRtcMaxConcurrentConnects options.Value

    let semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent)

    let timeout () =
        OpenAiRealtimeOptions.webRtcConnectTimeout options.Value

    let maxAttempts () =
        OpenAiRealtimeOptions.webRtcConnectMaxAttempts options.Value

    let failureStageOrDefault failureStage =
        failureStage ()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "unknown"

    let retryDelay attempt =
        let jitterMs = Random.Shared.Next(25, 175)
        TimeSpan.FromMilliseconds(float (100 + (attempt * 100) + jitterMs))

    interface IOpenAiWebRtcConnectionGate with
        member _.RunWithRetriesAsync(sessionId, purpose, connect, failureStage, cancellationToken) =
            let rec run attempt totalQueueWaitMs =
                task {
                    let queueStopwatch = Stopwatch.StartNew()
                    do! semaphore.WaitAsync(cancellationToken)
                    queueStopwatch.Stop()

                    let queueWaitMs = queueStopwatch.ElapsedMilliseconds
                    let totalQueueWaitMs = totalQueueWaitMs + queueWaitMs

                    let! outcome =
                        task {
                            try
                                let! result =
                                    task {
                                        try
                                            use attemptTimeout =
                                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                                            attemptTimeout.CancelAfter(timeout ())

                                            let! value =
                                                connect
                                                    { attempt = attempt
                                                      queueWaitMs = queueWaitMs
                                                      cancellationToken = attemptTimeout.Token }

                                            return Ok value
                                        with ex ->
                                            if cancellationToken.IsCancellationRequested then
                                                return raise (OperationCanceledException(cancellationToken))
                                            else
                                                return Error(failureStageOrDefault failureStage, ex)
                                    }

                                return result
                            finally
                                semaphore.Release() |> ignore
                        }

                    match outcome with
                    | Ok value ->
                        return
                            { value = value
                              queueWaitMs = totalQueueWaitMs
                              attemptCount = attempt }
                    | Error(stage, ex) when attempt < maxAttempts () ->
                        logger.LogWarning(
                            ex,
                            "OpenAI {Purpose} WebRTC setup for SIP session {SessionId} failed at {Stage} on attempt {Attempt}; retrying.",
                            purpose,
                            sessionId,
                            stage,
                            attempt
                        )

                        do! Task.Delay(retryDelay attempt, cancellationToken)
                        return! run (attempt + 1) totalQueueWaitMs
                    | Error(stage, ex) ->
                        return raise (OpenAiWebRtcConnectFailedException(stage, attempt, totalQueueWaitMs, ex))
                }

            run 1 0L

    interface IDisposable with
        member _.Dispose() = semaphore.Dispose()
