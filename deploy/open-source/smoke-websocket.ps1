param(
    [Parameter(Mandatory = $true)]
    [string]$AudioPath,
    [string]$Url = "http://127.0.0.1:5067",
    [ValidateRange(1, 30)]
    [int]$MaxAudioSeconds = 8,
    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"

function Read-WavAsMonoFloat32([string]$Path, [int]$MaxSeconds) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $bytes = [IO.File]::ReadAllBytes($resolved)

    if ($bytes.Length -lt 44 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne "RIFF") {
        throw "AudioPath must be a RIFF WAV file: $resolved"
    }

    $format = 0
    $channels = 0
    $sampleRate = 0
    $bitsPerSample = 0
    $dataOffset = -1
    $dataLength = 0
    $offset = 12

    while ($offset + 8 -le $bytes.Length) {
        $chunkName = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
        $chunkLength = [BitConverter]::ToInt32($bytes, $offset + 4)
        $chunkData = $offset + 8

        if ($chunkLength -lt 0 -or $chunkData + $chunkLength -gt $bytes.Length) {
            throw "WAV chunk '$chunkName' is invalid in $resolved"
        }

        if ($chunkName -eq "fmt ") {
            $format = [BitConverter]::ToUInt16($bytes, $chunkData)
            $channels = [BitConverter]::ToUInt16($bytes, $chunkData + 2)
            $sampleRate = [BitConverter]::ToInt32($bytes, $chunkData + 4)
            $bitsPerSample = [BitConverter]::ToUInt16($bytes, $chunkData + 14)
        } elseif ($chunkName -eq "data") {
            $dataOffset = $chunkData
            $dataLength = $chunkLength
        }

        $offset = $chunkData + $chunkLength + ($chunkLength % 2)
    }

    if ($dataOffset -lt 0 -or $channels -lt 1 -or $sampleRate -lt 1) {
        throw "WAV metadata or audio data is missing: $resolved"
    }

    $bytesPerSample = [int]($bitsPerSample / 8)

    if (-not (($format -eq 1 -and $bitsPerSample -eq 16) -or ($format -eq 3 -and $bitsPerSample -eq 32))) {
        throw "Smoke audio must be PCM16 or float32 WAV; format=$format bits=$bitsPerSample"
    }

    $frameSize = $channels * $bytesPerSample
    $availableFrames = [int]($dataLength / $frameSize)
    $frameCount = [Math]::Min($availableFrames, $sampleRate * $MaxSeconds)
    $samples = [float[]]::new($frameCount)

    for ($frame = 0; $frame -lt $frameCount; $frame++) {
        $sum = 0.0

        for ($channel = 0; $channel -lt $channels; $channel++) {
            $sampleOffset = $dataOffset + ($frame * $frameSize) + ($channel * $bytesPerSample)

            $value =
                if ($format -eq 1) {
                    [BitConverter]::ToInt16($bytes, $sampleOffset) / 32768.0
                } else {
                    [BitConverter]::ToSingle($bytes, $sampleOffset)
                }

            $sum += $value
        }

        $samples[$frame] = [float]($sum / $channels)
    }

    return [pscustomobject]@{
        Path = $resolved
        SampleRate = $sampleRate
        Samples = $samples
    }
}

function Send-WebSocketText(
    [Net.WebSockets.ClientWebSocket]$Socket,
    [object]$Payload,
    [Threading.CancellationToken]$CancellationToken
) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Payload | ConvertTo-Json -Compress -Depth 8))
    $segment = [ArraySegment[byte]]::new($bytes)
    $null = $Socket.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Text, $true, $CancellationToken).GetAwaiter().GetResult()
}

function Receive-WebSocketMessage(
    [Net.WebSockets.ClientWebSocket]$Socket,
    [Threading.CancellationToken]$CancellationToken
) {
    $buffer = [byte[]]::new(65536)
    $stream = [IO.MemoryStream]::new()

    try {
        do {
            $segment = [ArraySegment[byte]]::new($buffer)
            $result = $Socket.ReceiveAsync($segment, $CancellationToken).GetAwaiter().GetResult()

            if ($result.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) {
                throw "WebSocket closed before agent.done: $($Socket.CloseStatusDescription)"
            }

            $stream.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)

        return [pscustomobject]@{
            MessageType = $result.MessageType
            Bytes = $stream.ToArray()
        }
    } finally {
        $stream.Dispose()
    }
}

$baseUri = [Uri]$Url
$status = Invoke-RestMethod -Uri ([Uri]::new($baseUri, "/api/status")) -TimeoutSec 10

if (-not $status.ready) {
    throw "FsVoice is not ready: $($status.message)"
}

$audio = Read-WavAsMonoFloat32 $AudioPath $MaxAudioSeconds

$session = Invoke-RestMethod `
    -Uri ([Uri]::new($baseUri, "/api/open-source/sessions")) `
    -Method Post `
    -ContentType "application/json" `
    -Body (@{ systemPrompt = "You are concise."; mode = "gemma-pocket-tts" } | ConvertTo-Json)

$scheme = if ($baseUri.Scheme -eq "https") { "wss" } else { "ws" }
$socketUri = [Uri]::new("${scheme}://$($baseUri.Authority)/api/open-source/sessions/$($session.id)/ws")
$socket = [Net.WebSockets.ClientWebSocket]::new()
$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))

try {
    $null = $socket.ConnectAsync($socketUri, $timeout.Token).GetAwaiter().GetResult()
    $ready = Receive-WebSocketMessage $socket $timeout.Token

    if ($ready.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) {
        throw "Expected session.ready text message."
    }

    Send-WebSocketText $socket @{ type = "audio.config"; sampleRate = $audio.SampleRate; format = "float32le"; channels = 1 } $timeout.Token

    $leadingSamples = [int][Math]::Round($audio.SampleRate * 0.3)
    $trailingSamples = [int][Math]::Round($audio.SampleRate * 0.9)
    $streamSamples = [float[]]::new($leadingSamples + $audio.Samples.Length + $trailingSamples)
    [Array]::Copy($audio.Samples, 0, $streamSamples, $leadingSamples, $audio.Samples.Length)
    $chunkSamples = [Math]::Max(1, [int][Math]::Round($audio.SampleRate * 0.032))

    for ($offset = 0; $offset -lt $streamSamples.Length; $offset += $chunkSamples) {
        $count = [Math]::Min($chunkSamples, $streamSamples.Length - $offset)
        $chunk = [float[]]::new($count)
        [Array]::Copy($streamSamples, $offset, $chunk, 0, $count)
        $chunkBytes = [byte[]]::new($count * 4)
        [Buffer]::BlockCopy($chunk, 0, $chunkBytes, 0, $chunkBytes.Length)
        $segment = [ArraySegment[byte]]::new($chunkBytes)
        $null = $socket.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Binary, $true, $timeout.Token).GetAwaiter().GetResult()
        Start-Sleep -Milliseconds ([Math]::Max(1, [int][Math]::Round(1000.0 * $count / $audio.SampleRate)))
    }

    $audioPackets = 0
    $transcript = ""
    $finalText = ""
    $firstAnswerAudioMs = $null
    $speechStarted = $false
    $speechStopped = $false
    $done = $false

    while (-not $done) {
        $message = Receive-WebSocketMessage $socket $timeout.Token

        if ($message.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Binary) {
            $audioPackets++
        } else {
            $event = [Text.Encoding]::UTF8.GetString($message.Bytes) | ConvertFrom-Json

            switch ($event.type) {
                "agent.transcription" { $transcript = $event.transcript }
                "agent.final_text" { $finalText = $event.text }
                "metrics.response_to_first_answer_audio" { $firstAnswerAudioMs = $event.durationMs }
                "vad.speech_started" { $speechStarted = $true }
                "vad.speech_stopped" { $speechStopped = $true }
                "agent.done" {
                    $transcript = $event.transcript
                    $finalText = $event.finalText
                    $done = $true
                }
                "error" { throw "FsVoice turn failed: $($event.message)" }
            }
        }
    }

    if ($audioPackets -lt 1) {
        throw "The turn completed without streamed answer audio."
    }
    if (-not $speechStarted -or -not $speechStopped) {
        throw "Silero VAD did not emit both speech-started and speech-stopped events."
    }

    [pscustomobject]@{
        Ready = $status.ready
        SessionId = $session.id
        InputPath = $audio.Path
        InputSampleRate = $audio.SampleRate
        InputSamples = $audio.Samples.Length
        VadSpeechStarted = $speechStarted
        VadSpeechStopped = $speechStopped
        Transcript = $transcript
        FinalText = $finalText
        AudioPackets = $audioPackets
        ResponseToFirstAnswerAudioMs = $firstAnswerAudioMs
    } | ConvertTo-Json -Depth 5
} finally {
    $timeout.Cancel()
    $socket.Dispose()
    $timeout.Dispose()
}
