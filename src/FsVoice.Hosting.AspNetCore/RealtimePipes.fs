namespace FsVoice.Hosting.AspNetCore

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type RealtimeAudioPayload =
    { durationRtpUnits: uint32
      payload: byte array }

[<AllowNullLiteral>]
type RealtimeMediaPipe
    (name: string, capacity: int, send: RealtimeAudioPayload -> unit, logger: ILogger, onDrop: int64 -> unit) =
    let queueCapacity = Math.Max(1, capacity)

    let options =
        BoundedChannelOptions(
            queueCapacity,
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        )

    let channel = Channel.CreateBounded<RealtimeAudioPayload>(options)
    let cancellation = new CancellationTokenSource()
    let mutable queueLength = 0
    let mutable droppedFrames = 0L
    let mutable enqueuedFrames = 0L
    let mutable sentFrames = 0L
    let mutable isDisposed = 0

    let noteDrop count =
        Interlocked.Add(&droppedFrames, count) |> ignore
        onDrop count

    let noteQueuedWrite () =
        let length = Interlocked.Increment(&queueLength)

        if length > queueCapacity then
            Interlocked.Exchange(&queueLength, queueCapacity) |> ignore
            noteDrop 1L

        Interlocked.Increment(&enqueuedFrames) |> ignore

    let worker =
        Task.Run(
            Func<Task>(fun () ->
                task {
                    try
                        while not cancellation.IsCancellationRequested do
                            let! canRead = channel.Reader.WaitToReadAsync(cancellation.Token)

                            if canRead then
                                let mutable frame = Unchecked.defaultof<RealtimeAudioPayload>

                                while channel.Reader.TryRead(&frame) do
                                    Interlocked.Decrement(&queueLength) |> ignore

                                    try
                                        send frame
                                        Interlocked.Increment(&sentFrames) |> ignore
                                    with ex ->
                                        logger.LogWarning(
                                            ex,
                                            "Realtime media pipe {PipeName} failed to send an audio frame.",
                                            name
                                        )
                    with
                    | :? OperationCanceledException -> ()
                    | ex -> logger.LogError(ex, "Realtime media pipe {PipeName} stopped unexpectedly.", name)
                })
        )

    member _.Name = name

    member _.DroppedFrames = Interlocked.Read(&droppedFrames)

    member _.TryEnqueue(durationRtpUnits: uint32, payload: byte array) =
        if Interlocked.CompareExchange(&isDisposed, 0, 0) <> 0 || isNull payload then
            false
        else
            let copiedPayload = Array.copy payload
            noteQueuedWrite ()

            if
                channel.Writer.TryWrite(
                    { durationRtpUnits = durationRtpUnits
                      payload = copiedPayload }
                )
            then
                true
            else
                Interlocked.Decrement(&queueLength) |> ignore
                noteDrop 1L
                false

    member _.Dispose() =
        if Interlocked.Exchange(&isDisposed, 1) = 0 then
            channel.Writer.TryComplete() |> ignore
            cancellation.Cancel()
            worker.ContinueWith(Action<Task>(fun _ -> cancellation.Dispose())) |> ignore

    interface IDisposable with
        member this.Dispose() = this.Dispose()
