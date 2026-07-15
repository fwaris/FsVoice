namespace FsVoice.OpenSource

open System

type VadEndpointReason =
    | Silence
    | MaxDuration

type VadEndpointEvent =
    | SpeechStarted
    | SpeechStopped of samples24k: float32 array * durationMs: float * reason: VadEndpointReason

type private VadEndpointState =
    | Idle
    | Candidate of speechSamples16k: int
    | Speaking of silenceSamples16k: int * lastSpeechSample24k: int

type VoiceActivityEndpoint(vadSession: IVadSession, options: VadRuntimeOptions, maxSamples24k: int) =
    let sourceSampleRate = 24000
    let vadSampleRate = 16000
    let vadFrameSamples = 512
    let sourceBlockSamples = 768
    let preRollSamples = options.PreRollMs * sourceSampleRate / 1000
    let preRollBufferSamples = max sourceBlockSamples preRollSamples
    let speechPadSamples = options.SpeechPadMs * sourceSampleRate / 1000
    let minSpeechSamples16k = options.MinSpeechDurationMs * vadSampleRate / 1000
    let minSilenceSamples16k = options.MinSilenceDurationMs * vadSampleRate / 1000

    let vadResampler =
        AudioPcm.StreamingLinearResampler(sourceSampleRate, vadSampleRate)

    let pendingSource = ResizeArray<float32>()
    let pendingVad = ResizeArray<float32>()
    let preRoll = ResizeArray<float32>()
    let active = ResizeArray<float32>()
    let mutable state = Idle

    let appendLimited (target: ResizeArray<float32>) limit (values: float32 array) =
        if limit > 0 then
            for value in values do
                target.Add value

            if target.Count > limit then
                target.RemoveRange(0, target.Count - limit)

    let resetToIdleFrom (recent: float32 array) =
        preRoll.Clear()
        appendLimited preRoll preRollBufferSamples recent
        active.Clear()
        state <- Idle
        vadSession.Reset()
        vadResampler.Reset()
        pendingVad.Clear()

    let stop reason lastSpeechSample24k =
        let keepCount =
            match reason with
            | MaxDuration -> min active.Count maxSamples24k
            | Silence -> min active.Count (lastSpeechSample24k + speechPadSamples)

        let samples = active.GetRange(0, max 0 keepCount).ToArray()
        let recent = active.ToArray()
        resetToIdleFrom recent
        let durationMs = float samples.Length * 1000.0 / float sourceSampleRate
        SpeechStopped(samples, durationMs, reason)

    let processVadFrame (events: ResizeArray<VadEndpointEvent>) =
        let frame = pendingVad.GetRange(0, vadFrameSamples).ToArray()
        pendingVad.RemoveRange(0, vadFrameSamples)
        let probability = float (vadSession.SpeechProbability frame)

        match state with
        | Idle when probability >= options.Threshold ->
            active.Clear()
            active.AddRange preRoll
            state <- Candidate vadFrameSamples

            if vadFrameSamples >= minSpeechSamples16k then
                state <- Speaking(0, active.Count)
                events.Add SpeechStarted
        | Idle -> ()
        | Candidate _ when active.Count >= maxSamples24k ->
            let recent = active.ToArray()
            resetToIdleFrom recent
        | Candidate speechSamples when probability >= options.Threshold ->
            let confirmedSamples = speechSamples + vadFrameSamples

            if confirmedSamples >= minSpeechSamples16k then
                state <- Speaking(0, active.Count)
                events.Add SpeechStarted
            else
                state <- Candidate confirmedSamples
        | Candidate _ when probability < options.NegativeThreshold ->
            let recent = active.ToArray()
            resetToIdleFrom recent
        | Candidate speechSamples -> state <- Candidate speechSamples
        | Speaking(_, _) when active.Count >= maxSamples24k -> events.Add(stop MaxDuration active.Count)
        | Speaking(silenceSamples, lastSpeechSample) when probability < options.NegativeThreshold ->
            let updatedSilence = silenceSamples + vadFrameSamples

            if updatedSilence >= minSilenceSamples16k then
                events.Add(stop Silence lastSpeechSample)
            else
                state <- Speaking(updatedSilence, lastSpeechSample)
        | Speaking _ -> state <- Speaking(0, active.Count)

    let processSourceBlock (block: float32 array) (events: ResizeArray<VadEndpointEvent>) =
        match state with
        | Idle -> appendLimited preRoll preRollBufferSamples block
        | Candidate _
        | Speaking _ -> active.AddRange block

        let vadSamples = vadResampler.Append block
        pendingVad.AddRange vadSamples

        while pendingVad.Count >= vadFrameSamples do
            processVadFrame events

    member _.Append(samples24k: float32 array) =
        let events = ResizeArray<VadEndpointEvent>()
        pendingSource.AddRange samples24k

        while pendingSource.Count >= sourceBlockSamples do
            let block = pendingSource.GetRange(0, sourceBlockSamples).ToArray()
            pendingSource.RemoveRange(0, sourceBlockSamples)
            processSourceBlock block events

        events.ToArray()

    member _.Reset() =
        state <- Idle
        preRoll.Clear()
        active.Clear()
        pendingSource.Clear()
        pendingVad.Clear()
        vadResampler.Reset()
        vadSession.Reset()
