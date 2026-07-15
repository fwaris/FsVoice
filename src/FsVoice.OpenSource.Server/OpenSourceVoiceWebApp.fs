namespace FsVoice.OpenSource.Server

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FsVoice.OpenSource

[<CLIMutable>]
type AssetBootstrapStatus =
    { Ready: bool
      Mode: string
      Provider: string
      ReleaseId: string
      ManifestSha256: string
      CacheRoot: string
      CacheHit: bool
      OfflineManifestUsed: bool
      DownloadedBytes: int64
      DurationMs: float
      Message: string }

module OpenSourceVoiceWebApp =
    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let bindOptions (configuration: IConfiguration) =
        let options = OpenSourceVoiceOptions()
        configuration.GetSection("OpenSourceVoice").Bind options
        options

    let localAssetStatus =
        { Ready = true
          Mode = "local"
          Provider = "local"
          ReleaseId = "local"
          ManifestSha256 = ""
          CacheRoot = ""
          CacheHit = true
          OfflineManifestUsed = false
          DownloadedBytes = 0L
          DurationMs = 0.0
          Message = "Pre-provisioned local assets are in use." }

    let tryReadAssetStatus (path: string) =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then
            None
        else
            try
                JsonSerializer.Deserialize<AssetBootstrapStatus>(File.ReadAllBytes path, jsonOptions)
                |> Option.ofObj
            with _ ->
                None

    let private indexHtml =
        """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>FsVoice Open Source Voice</title>
  <style>
    :root { color-scheme: light; }
    body { margin: 0; font: 15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; color: #17212b; background: #fff; }
    header { min-height: 56px; padding: 12px 22px; border-bottom: 1px solid #d7dee6; display: flex; align-items: center; justify-content: space-between; gap: 16px; }
    main { display: grid; grid-template-columns: minmax(330px, 430px) 1fr; min-height: calc(100vh - 57px); }
    aside { padding: 18px 22px; background: #f5f7f9; border-right: 1px solid #d7dee6; display: grid; gap: 13px; align-content: start; }
    section { padding: 18px 24px; display: grid; gap: 14px; align-content: start; }
    label { display: grid; gap: 6px; font-weight: 650; }
    textarea, button { font: inherit; }
    textarea { width: 100%; min-height: 122px; border: 1px solid #cbd4dd; border-radius: 6px; padding: 9px 10px; resize: vertical; background: white; box-sizing: border-box; }
    button { border: 0; border-radius: 6px; padding: 10px 13px; background: #0f766e; color: white; font-weight: 750; cursor: pointer; }
    button.secondary { background: #334155; }
    button.warn { background: #b42318; }
    button:disabled { opacity: .55; cursor: default; }
    .micControl { display: flex; align-items: center; gap: 12px; }
    .micButton { width: 54px; height: 54px; padding: 13px; border-radius: 50%; display: inline-grid; place-items: center; background: #64748b; transition: background .15s ease, transform .15s ease; }
    .micButton:hover:not(:disabled) { transform: scale(1.04); }
    .micButton.connected { background: #d500f9; }
    .micButton svg { width: 28px; height: 28px; fill: currentColor; }
    .micButton [hidden] { display: none; }
    .micLabel { color: #475569; font-weight: 700; }
    a { color: #0f766e; font-weight: 700; }
    .audioOutput { display: grid; gap: 8px; }
    .audioOutput a[hidden] { display: none; }
    .status { border: 1px solid #d7dee6; border-radius: 8px; padding: 12px; color: #5d6875; background: white; }
    .metric { border: 1px solid #b8d8d3; border-radius: 8px; padding: 12px; background: #effaf8; display: flex; justify-content: space-between; gap: 16px; align-items: baseline; }
    .metric span { color: #315c57; font-weight: 650; }
    .metric strong { color: #0f766e; font-size: 20px; white-space: nowrap; }
    .transcript, .answer { border: 1px solid #d7dee6; border-radius: 8px; padding: 12px; background: white; min-height: 48px; }
    pre { margin: 0; max-height: 54vh; overflow: auto; background: #101923; color: #e6edf3; border-radius: 6px; padding: 12px; font-size: 12px; }
    audio { width: min(720px, 100%); }
    @media (max-width: 840px) { main { grid-template-columns: 1fr; } aside { border-right: 0; border-bottom: 1px solid #d7dee6; } }
  </style>
</head>
<body>
  <header><strong>FsVoice Open Source</strong><span id="runtime">Checking runtime...</span></header>
  <main>
    <aside>
      <label>System prompt<textarea id="systemPrompt">You are a concise voice assistant. Use tools when useful. Reply with one or two short spoken sentences unless the user explicitly asks for detail.</textarea></label>
      <div class="micControl">
        <button id="mic" class="micButton" type="button" aria-label="Connect microphone" aria-pressed="false" title="Connect microphone">
          <svg id="micOnIcon" viewBox="0 0 24 24" aria-hidden="true" hidden><path d="M12 14a3 3 0 0 0 3-3V5a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3Zm5.3-3a5.3 5.3 0 0 1-10.6 0H5a7 7 0 0 0 6 6.92V21H8v2h8v-2h-3v-3.08A7 7 0 0 0 19 11h-1.7Z"/></svg>
          <svg id="micOffIcon" viewBox="0 0 24 24" aria-hidden="true"><path d="m19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23A6.9 6.9 0 0 0 19 11ZM4.27 3 3 4.27l6.01 6.01V11A3 3 0 0 0 12 14c.22 0 .44-.03.65-.08l1.66 1.66A5.3 5.3 0 0 1 6.7 11H5a7 7 0 0 0 6 6.92V21H8v2h8v-2h-3v-3.08a6.92 6.92 0 0 0 2.54-1.11L19.73 21 21 19.73 4.27 3ZM15 10.73 9 4.73V5a3 3 0 0 1 6 0v5.73Z"/></svg>
        </button>
        <span id="micLabel" class="micLabel">Disconnected</span>
      </div>
      <button id="cancel" class="warn" type="button" disabled>Cancel response</button>
      <audio id="remoteAudio" autoplay controls></audio>
      <div class="audioOutput">
        <audio id="generatedAudio" controls preload="metadata"></audio>
        <a id="generatedAudioLink" href="#" target="_blank" rel="noopener" hidden>Open WAV</a>
      </div>
    </aside>
    <section>
      <div id="message" class="status">Disconnected</div>
      <div class="metric"><span>Response → first answer audio</span><strong id="firstAnswerAudioMetric" aria-live="polite">—</strong></div>
      <div id="transcript" class="transcript"></div>
      <div id="answer" class="answer"></div>
      <pre id="events">{}</pre>
    </section>
  </main>
  <script>
    const runtime = document.getElementById('runtime');
    const message = document.getElementById('message');
    const firstAnswerAudioMetric = document.getElementById('firstAnswerAudioMetric');
    const transcript = document.getElementById('transcript');
    const answer = document.getElementById('answer');
    const events = document.getElementById('events');
    const remoteAudio = document.getElementById('remoteAudio');
    const generatedAudio = document.getElementById('generatedAudio');
    const generatedAudioLink = document.getElementById('generatedAudioLink');
    const micButton = document.getElementById('mic');
    const micOnIcon = document.getElementById('micOnIcon');
    const micOffIcon = document.getElementById('micOffIcon');
    const micLabel = document.getElementById('micLabel');
    const cancelButton = document.getElementById('cancel');
    let session = null;
    let pc = null;
    let dc = null;
    let ws = null;
    let localStream = null;
    let audioContext = null;
    let captureNode = null;
    let transport = null;
    let fallbackStarted = false;
    let connected = false;
    let connecting = false;
    let shuttingDown = false;
    let bargeInEnabled = true;
    let playbackTime = 0;
    const preferWebSocket = new URLSearchParams(location.search).get('transport') === 'websocket';
    const playbackSources = new Set();

    function setGeneratedAudioUrl(url) {
      const cacheSafeUrl = `${url}${url.includes('?') ? '&' : '?'}t=${Date.now()}`;
      generatedAudio.src = cacheSafeUrl;
      generatedAudio.load();
      generatedAudioLink.href = url;
      generatedAudioLink.hidden = false;
    }

    function logEvent(payload) {
      events.textContent = JSON.stringify(payload, null, 2);
      if (payload.type === 'session.ready') bargeInEnabled = payload.bargeInEnabled !== false;
      if (payload.type === 'agent.transcription') transcript.textContent = payload.transcript;
      if (payload.type === 'vad.speech_started') {
        transcript.textContent = '';
        answer.textContent = '';
        firstAnswerAudioMetric.textContent = '—';
        stopPlayback();
        message.textContent = 'Speech detected';
      }
      if (payload.type === 'vad.speech_stopped') {
        message.textContent = 'Thinking';
        cancelButton.disabled = false;
      }
      if (payload.type === 'agent.filler_text') {
        message.textContent = payload.text;
        cancelButton.disabled = false;
      }
      if (payload.type === 'agent.final_text') answer.textContent = payload.text;
      if (payload.type?.startsWith('tts.') && payload.type.endsWith('.chunk')) {
        message.textContent = bargeInEnabled ? 'Speaking — you can interrupt' : 'Speaking';
        cancelButton.disabled = false;
        if (remoteAudio.srcObject) remoteAudio.play().catch(() => {});
      }
      if (payload.type === 'metrics.response_to_first_answer_audio') {
        firstAnswerAudioMetric.textContent = `${payload.durationMs.toFixed(1)} ms`;
      }
      if (payload.type === 'tts.final.done' && payload.id && payload.turnIndex) {
        setGeneratedAudioUrl(`/api/open-source/sessions/${payload.id}/turns/${payload.turnIndex}/audio.wav`);
      }
      if (payload.type === 'agent.done') {
        message.textContent = 'Listening';
        cancelButton.disabled = true;
        if (payload.audioUrl) {
          setGeneratedAudioUrl(payload.audioUrl);
        }
      }
      if (payload.type === 'generation.canceled') {
        stopPlayback();
        message.textContent = connected ? 'Listening' : 'Disconnected';
        cancelButton.disabled = true;
      }
      if (payload.type === 'error') message.textContent = payload.message;
    }

    function setMicState(state) {
      connecting = state === 'connecting';
      connected = state === 'connected';
      micButton.disabled = connecting;
      micButton.classList.toggle('connected', connected);
      micButton.setAttribute('aria-pressed', connected ? 'true' : 'false');
      const action = connected ? 'Disconnect microphone' : 'Connect microphone';
      micButton.setAttribute('aria-label', action);
      micButton.title = action;
      micOnIcon.hidden = !connected;
      micOffIcon.hidden = connected;
      micLabel.textContent = connecting ? 'Connecting' : connected ? 'Connected' : 'Disconnected';
    }

    function stopPlayback() {
      for (const source of playbackSources) {
        try { source.stop(); } catch (_) {}
      }
      playbackSources.clear();
      playbackTime = audioContext ? audioContext.currentTime : 0;
      try { remoteAudio.pause(); } catch (_) {}
      try { generatedAudio.pause(); generatedAudio.currentTime = 0; } catch (_) {}
    }

    function playPcmPacket(buffer) {
      if (!audioContext || buffer.byteLength < 12) return;
      const header = new Uint8Array(buffer, 0, 4);
      if (header[0] !== 70 || header[1] !== 83 || header[2] !== 65 || header[3] !== 49) return;
      const view = new DataView(buffer);
      const sampleRate = view.getInt32(4, true);
      const samples = new Float32Array(buffer, 12);
      const audioBuffer = audioContext.createBuffer(1, samples.length, sampleRate);
      audioBuffer.copyToChannel(samples, 0);
      const source = audioContext.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(audioContext.destination);
      playbackTime = Math.max(playbackTime, audioContext.currentTime + 0.03);
      source.start(playbackTime);
      playbackTime += audioBuffer.duration;
      playbackSources.add(source);
      source.onended = () => playbackSources.delete(source);
    }

    function sendControl(payload) {
      const json = JSON.stringify(payload);
      if (transport === 'webrtc' && dc?.readyState === 'open') dc.send(json);
      else if (transport === 'websocket' && ws?.readyState === WebSocket.OPEN) ws.send(json);
    }

    async function setupWebSocketCapture() {
      audioContext ||= new AudioContext();
      await audioContext.resume();
      if (captureNode) return;
      const workletSource = `
        class FsVoiceCapture extends AudioWorkletProcessor {
          process(inputs) {
            const channel = inputs[0] && inputs[0][0];
            if (channel) {
              const copy = channel.slice();
              this.port.postMessage(copy.buffer, [copy.buffer]);
            }
            return true;
          }
        }
        registerProcessor('fsvoice-capture', FsVoiceCapture);`;
      const moduleUrl = URL.createObjectURL(new Blob([workletSource], { type: 'text/javascript' }));
      try { await audioContext.audioWorklet.addModule(moduleUrl); }
      finally { URL.revokeObjectURL(moduleUrl); }
      const source = audioContext.createMediaStreamSource(localStream);
      captureNode = new AudioWorkletNode(audioContext, 'fsvoice-capture');
      const silent = audioContext.createGain();
      silent.gain.value = 0;
      source.connect(captureNode).connect(silent).connect(audioContext.destination);
      captureNode.port.onmessage = event => {
        if (connected && ws?.readyState === WebSocket.OPEN) ws.send(event.data);
      };
      sendControl({ type: 'audio.config', sampleRate: audioContext.sampleRate, format: 'float32le', channels: 1 });
    }

    async function createSession() {
      const response = await fetch('/api/open-source/sessions', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ systemPrompt: document.getElementById('systemPrompt').value, mode: 'gemma-pocket-tts' })
      });
      if (!response.ok) throw new Error(await response.text());
      return await response.json();
    }

    async function waitForIceGathering(peer) {
      if (peer.iceGatheringState === 'complete') return;
      await new Promise(resolve => {
        const done = () => {
          if (peer.iceGatheringState === 'complete') {
            peer.removeEventListener('icegatheringstatechange', done);
            resolve();
          }
        };
        peer.addEventListener('icegatheringstatechange', done);
        setTimeout(resolve, 1600);
      });
    }

    async function connectWebSocket(reason) {
      if (fallbackStarted) return;
      fallbackStarted = true;
      try { dc?.close(); } catch (_) {}
      try { pc?.close(); } catch (_) {}
      dc = null;
      pc = null;
      message.textContent = `${reason} Using WebSocket fallback...`;
      const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
      ws = new WebSocket(`${scheme}://${location.host}/api/open-source/sessions/${session.id}/ws`);
      ws.binaryType = 'arraybuffer';
      ws.onmessage = event => {
        if (typeof event.data === 'string') logEvent(JSON.parse(event.data));
        else playPcmPacket(event.data);
      };
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('Timed out opening WebSocket fallback.')), 10000);
        ws.onopen = () => { clearTimeout(timer); resolve(); };
        ws.onerror = () => { clearTimeout(timer); reject(new Error('WebSocket fallback failed to connect.')); };
      });
      transport = 'websocket';
      await setupWebSocketCapture();
      setMicState('connected');
      message.textContent = 'Listening';
      cancelButton.disabled = true;
    }

    async function connectWebRtc() {
      pc = new RTCPeerConnection();
      pc.oniceconnectionstatechange = () => {
        if (shuttingDown) return;
        message.textContent = `ICE: ${pc.iceConnectionState}`;
        if (pc.iceConnectionState === 'failed' || pc.iceConnectionState === 'disconnected') {
          connectWebSocket('WebRTC ICE failed.').catch(error => message.textContent = error.message);
        }
      };
      pc.onconnectionstatechange = () => {
        if (shuttingDown) return;
        if (pc.connectionState === 'failed') {
          connectWebSocket('WebRTC connection failed.').catch(error => message.textContent = error.message);
        }
      };
      pc.onicecandidateerror = event => {
        message.textContent = `ICE candidate error ${event.errorCode || ''} ${event.errorText || ''}`.trim();
      };
      pc.ontrack = event => {
        remoteAudio.srcObject = event.streams[0];
        remoteAudio.play().catch(() => {});
      };
      for (const track of localStream.getAudioTracks()) pc.addTrack(track, localStream);
      dc = pc.createDataChannel('fsvoice-events');
      dc.onopen = () => {
        transport = 'webrtc';
        setMicState('connected');
        message.textContent = 'Listening';
        cancelButton.disabled = true;
      };
      dc.onmessage = event => logEvent(JSON.parse(event.data));
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      await waitForIceGathering(pc);
      const answerResponse = await fetch(session.webRtcOfferUrl, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ sdp: pc.localDescription.sdp, type: pc.localDescription.type })
      });
      if (!answerResponse.ok) throw new Error(await answerResponse.text());
      const answerSdp = await answerResponse.json();
      await pc.setRemoteDescription(answerSdp);
      await new Promise((resolve, reject) => {
        if (dc.readyState === 'open') {
          resolve();
          return;
        }
        const timer = setTimeout(() => reject(new Error('Timed out waiting for WebRTC data channel. Check UDP ICE port forwarding or TURN configuration.')), 12000);
        dc.addEventListener('open', () => {
          clearTimeout(timer);
          resolve();
        }, { once: true });
        dc.addEventListener('error', () => {
          clearTimeout(timer);
          reject(new Error('WebRTC data channel failed to open.'));
        }, { once: true });
      });
    }

    async function connect() {
      if (connecting || connected) return;
      shuttingDown = false;
      fallbackStarted = false;
      setMicState('connecting');
      message.textContent = 'Connecting';
      session = await createSession();
      localStream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
      });
      if (preferWebSocket) {
        await connectWebSocket('WebSocket transport requested.');
        return;
      }
      try {
        await connectWebRtc();
      } catch (error) {
        await connectWebSocket(`WebRTC unavailable: ${error.message}`);
      }
    }

    async function disconnect() {
      if (shuttingDown) return;
      shuttingDown = true;
      const sessionToDelete = session;
      try { sendControl({ type: 'turn.cancel' }); } catch (_) {}
      stopPlayback();
      try { captureNode?.disconnect(); } catch (_) {}
      captureNode = null;
      for (const track of localStream?.getTracks() || []) track.stop();
      localStream = null;
      try { dc?.close(); } catch (_) {}
      try { pc?.close(); } catch (_) {}
      try { ws?.close(1000, 'microphone disconnected'); } catch (_) {}
      dc = null;
      pc = null;
      ws = null;
      remoteAudio.srcObject = null;
      if (audioContext) {
        try { await audioContext.close(); } catch (_) {}
        audioContext = null;
      }
      transport = null;
      session = null;
      fallbackStarted = false;
      cancelButton.disabled = true;
      setMicState('disconnected');
      message.textContent = 'Disconnected';
      if (sessionToDelete?.id) {
        fetch(`/api/open-source/sessions/${sessionToDelete.id}`, { method: 'DELETE' }).catch(() => {});
      }
      shuttingDown = false;
    }

    micButton.onclick = () => {
      if (connected) {
        disconnect().catch(error => message.textContent = error.message);
      } else {
        connect().catch(async error => {
          await disconnect();
          message.textContent = `Connection failed: ${error.message}`;
        });
      }
    };
    cancelButton.onclick = () => {
      stopPlayback();
      sendControl({ type: 'turn.cancel' });
      cancelButton.disabled = true;
      message.textContent = 'Listening';
    };

    window.addEventListener('beforeunload', () => {
      try { sendControl({ type: 'turn.cancel' }); } catch (_) {}
      for (const track of localStream?.getTracks() || []) track.stop();
    });

    fetch('/api/status')
      .then(response => response.json())
      .then(status => runtime.textContent = status.message || 'Ready')
      .catch(error => runtime.textContent = error.message);
  </script>
</body>
</html>
"""

    let private writeJson (ctx: HttpContext) status payload =
        task {
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json; charset=utf-8"
            do! JsonSerializer.SerializeAsync(ctx.Response.Body, payload, jsonOptions)
        }

    let private writeText (ctx: HttpContext) contentType (text: string) =
        task {
            ctx.Response.ContentType <- contentType
            do! ctx.Response.WriteAsync text
        }

    let private error status message =
        {| error = {| code = status; message = message |} |}

    let private routeValue (ctx: HttpContext) name =
        match ctx.Request.RouteValues.TryGetValue name with
        | true, value when not (isNull value) -> value.ToString()
        | _ -> ""

    let private safeId (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value |> Seq.forall (fun ch -> Char.IsLetterOrDigit ch || ch = '_' || ch = '-')

    let private statusPayload (agent: IVoiceAgentRuntime) (vad: IVadRuntime) (assets: AssetBootstrapStatus) =
        let status = agent.Status()
        let vadStatus = vad.Status()

        {| ready = status.Ready && vadStatus.Ready && assets.Ready
           serviceName = status.ServiceName
           mode = status.Mode
           workDir = status.WorkDir
           maxHistoryTurns = status.MaxHistoryTurns
           maxTurnAudioSeconds = status.MaxTurnAudioSeconds
           maxTurnAudioSamples24k = status.MaxTurnAudioSamples24k
           gemma = status.Gemma
           stt = status.Stt
           tts = status.Tts
           vad = vadStatus
           index = status.Index
           assets = assets
           message = $"{status.Message} {vadStatus.Message} {assets.Message}" |}

    let private readiness
        (agent: IVoiceAgentRuntime)
        (vad: IVadRuntime)
        (assets: AssetBootstrapStatus)
        (ctx: HttpContext)
        =
        let status = agent.Status()
        let vadStatus = vad.Status()
        let ready = status.Ready && vadStatus.Ready && assets.Ready

        let statusCode =
            if ready then
                StatusCodes.Status200OK
            else
                StatusCodes.Status503ServiceUnavailable

        writeJson
            ctx
            statusCode
            {| ready = ready
               gemmaReady = status.Gemma.Ready
               sttReady = status.Stt.Ready
               ttsReady = status.Tts.Ready
               vadReady = vadStatus.Ready
               indexReady = status.Index.Ready
               assetReady = assets.Ready
               message = $"{status.Message} {vadStatus.Message} {assets.Message}" |}

    let private readSessionRequest (ctx: HttpContext) =
        task {
            if
                ctx.Request.ContentType <> null
                && ctx.Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            then
                use! doc = JsonDocument.ParseAsync(ctx.Request.Body)

                let stringProperty (name: string) =
                    match doc.RootElement.TryGetProperty name with
                    | true, value -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
                    | _ -> ""

                return
                    { SystemPrompt = stringProperty "systemPrompt"
                      Mode = stringProperty "mode" }
            else
                return { SystemPrompt = ""; Mode = "" }
        }

    let private createSession (agent: IVoiceAgentRuntime) (ctx: HttpContext) =
        task {
            try
                let! request = readSessionRequest ctx
                let session = agent.CreateSession request
                do! writeJson ctx 200 session
            with
            | :? ArgumentException as ex -> do! writeJson ctx 400 (error 400 ex.Message)
            | ex -> do! writeJson ctx 500 (error 500 ex.Message)
        }

    let private acceptOffer
        (agent: IVoiceAgentRuntime)
        (webRtcStore: OpenSourceVoiceWebRtcSessionStore)
        (ctx: HttpContext)
        =
        task {
            let sessionId = routeValue ctx "id"

            if not (safeId sessionId) then
                do! writeJson ctx 400 (error 400 "Invalid session id.")
            else
                match agent.TryGetSession sessionId with
                | None -> do! writeJson ctx 404 (error 404 "Open-source voice session was not found.")
                | Some session ->
                    try
                        let! offer = JsonSerializer.DeserializeAsync<SdpPayload>(ctx.Request.Body, jsonOptions)

                        if isNull (box offer) then
                            do! writeJson ctx 400 (error 400 "WebRTC offer payload is required.")
                        else
                            let webRtc = webRtcStore.CreateOrReplace session
                            let! answer = webRtc.AcceptOfferAsync(offer, ctx.RequestAborted)
                            do! writeJson ctx 200 answer
                    with
                    | :? ArgumentException as ex -> do! writeJson ctx 400 (error 400 ex.Message)
                    | ex -> do! writeJson ctx 500 (error 500 ex.Message)
        }

    let private acceptWebSocket
        (agent: IVoiceAgentRuntime)
        (vad: IVadRuntime)
        (options: OpenSourceVoiceOptions)
        (ctx: HttpContext)
        =
        task {
            let sessionId = routeValue ctx "id"

            if not (safeId sessionId) then
                do! writeJson ctx 400 (error 400 "Invalid session id.")
            else
                match agent.TryGetSession sessionId with
                | None -> do! writeJson ctx 404 (error 404 "Open-source voice session was not found.")
                | Some session ->
                    let logger =
                        ctx.RequestServices.GetRequiredService<ILogger<OpenSourceVoiceWebSocketSession>>()

                    do! OpenSourceVoiceWebSocket.acceptAsync agent vad options logger ctx session
        }

    let private deleteSession (webRtcStore: OpenSourceVoiceWebRtcSessionStore) (ctx: HttpContext) =
        task {
            let sessionId = routeValue ctx "id"

            if not (safeId sessionId) then
                do! writeJson ctx 400 (error 400 "Invalid session id.")
            else
                webRtcStore.Remove sessionId |> ignore
                do! writeJson ctx 200 {| deleted = true; id = sessionId |}
        }

    let private serveTurnArtifact (agent: IVoiceAgentRuntime) fileName (ctx: HttpContext) =
        task {
            let sessionId = routeValue ctx "id"
            let turnIndexText = routeValue ctx "turnIndex"

            match Int32.TryParse turnIndexText with
            | false, _ -> do! writeJson ctx 400 (error 400 "Invalid turn index.")
            | true, turnIndex ->
                if not (safeId sessionId) || turnIndex < 1 then
                    do! writeJson ctx 400 (error 400 "Invalid session id or turn index.")
                else
                    match agent.TryGetTurnArtifact(sessionId, turnIndex, fileName) with
                    | None -> do! writeJson ctx 404 (error 404 $"Open-source turn artifact {fileName} was not found.")
                    | Some artifact ->
                        let fileInfo = FileInfo artifact.Path
                        ctx.Response.ContentType <- artifact.ContentType
                        ctx.Response.ContentLength <- Nullable<int64> fileInfo.Length
                        ctx.Response.Headers["Cache-Control"] <- "no-store"

                        if not (HttpMethods.IsHead ctx.Request.Method) then
                            do! ctx.Response.SendFileAsync artifact.Path
        }

    let mapWithAssets
        (app: WebApplication)
        (agent: IVoiceAgentRuntime)
        (vad: IVadRuntime)
        (assets: AssetBootstrapStatus)
        (options: OpenSourceVoiceOptions)
        (webRtcStore: OpenSourceVoiceWebRtcSessionStore)
        =
        app.UseWebSockets() |> ignore

        app.MapGet("/", RequestDelegate(fun ctx -> task { do! writeText ctx "text/html; charset=utf-8" indexHtml }))
        |> ignore

        app.MapGet("/healthz", RequestDelegate(fun ctx -> writeJson ctx 200 {| ok = true |}))
        |> ignore

        app.MapGet("/healthz/ready", RequestDelegate(fun ctx -> readiness agent vad assets ctx))
        |> ignore

        app.MapGet("/api/status", RequestDelegate(fun ctx -> writeJson ctx 200 (statusPayload agent vad assets)))
        |> ignore

        app.MapPost("/api/open-source/sessions", RequestDelegate(fun ctx -> createSession agent ctx))
        |> ignore

        app.MapPost(
            "/api/open-source/sessions/{id}/webrtc/offer",
            RequestDelegate(fun ctx -> acceptOffer agent webRtcStore ctx)
        )
        |> ignore

        app.MapGet(
            "/api/open-source/sessions/{id}/ws",
            RequestDelegate(fun ctx -> acceptWebSocket agent vad options ctx)
        )
        |> ignore

        app.MapDelete("/api/open-source/sessions/{id}", RequestDelegate(fun ctx -> deleteSession webRtcStore ctx))
        |> ignore

        app.MapMethods(
            "/api/open-source/sessions/{id}/turns/{turnIndex}/details.json",
            [| "GET"; "HEAD" |],
            RequestDelegate(fun ctx -> serveTurnArtifact agent "details.json" ctx)
        )
        |> ignore

        app.MapMethods(
            "/api/open-source/sessions/{id}/turns/{turnIndex}/audio.wav",
            [| "GET"; "HEAD" |],
            RequestDelegate(fun ctx -> serveTurnArtifact agent "audio.wav" ctx)
        )
        |> ignore

        app

    let map
        (app: WebApplication)
        (agent: IVoiceAgentRuntime)
        (vad: IVadRuntime)
        (options: OpenSourceVoiceOptions)
        (webRtcStore: OpenSourceVoiceWebRtcSessionStore)
        =
        mapWithAssets app agent vad localAssetStatus options webRtcStore
