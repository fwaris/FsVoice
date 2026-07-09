namespace FsVoice.OpenSource.Server

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open FsVoice.OpenSource

module OpenSourceVoiceWebApp =
    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let bindOptions (configuration: IConfiguration) =
        let options = OpenSourceVoiceOptions()
        configuration.GetSection("OpenSourceVoice").Bind options
        options

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
    .buttons { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    button { border: 0; border-radius: 6px; padding: 10px 13px; background: #0f766e; color: white; font-weight: 750; cursor: pointer; }
    button.secondary { background: #334155; }
    button.warn { background: #b42318; }
    button:disabled { opacity: .55; cursor: default; }
    a { color: #0f766e; font-weight: 700; }
    .audioOutput { display: grid; gap: 8px; }
    .audioOutput a[hidden] { display: none; }
    .status { border: 1px solid #d7dee6; border-radius: 8px; padding: 12px; color: #5d6875; background: white; }
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
      <button id="connect" type="button">Connect</button>
      <div class="buttons">
        <button id="start" type="button" disabled>Start Turn</button>
        <button id="end" class="secondary" type="button" disabled>End Turn</button>
      </div>
      <button id="cancel" class="warn" type="button" disabled>Cancel</button>
      <audio id="remoteAudio" autoplay controls></audio>
      <div class="audioOutput">
        <audio id="generatedAudio" controls preload="metadata"></audio>
        <a id="generatedAudioLink" href="#" target="_blank" rel="noopener" hidden>Open WAV</a>
      </div>
    </aside>
    <section>
      <div id="message" class="status">Idle</div>
      <div id="transcript" class="transcript"></div>
      <div id="answer" class="answer"></div>
      <pre id="events">{}</pre>
    </section>
  </main>
  <script>
    const runtime = document.getElementById('runtime');
    const message = document.getElementById('message');
    const transcript = document.getElementById('transcript');
    const answer = document.getElementById('answer');
    const events = document.getElementById('events');
    const remoteAudio = document.getElementById('remoteAudio');
    const generatedAudio = document.getElementById('generatedAudio');
    const generatedAudioLink = document.getElementById('generatedAudioLink');
    const connectButton = document.getElementById('connect');
    const startButton = document.getElementById('start');
    const endButton = document.getElementById('end');
    const cancelButton = document.getElementById('cancel');
    let session = null;
    let pc = null;
    let dc = null;
    let localStream = null;

    function setGeneratedAudioUrl(url) {
      const cacheSafeUrl = `${url}${url.includes('?') ? '&' : '?'}t=${Date.now()}`;
      generatedAudio.src = cacheSafeUrl;
      generatedAudio.load();
      generatedAudioLink.href = url;
      generatedAudioLink.hidden = false;
    }

    function logEvent(payload) {
      events.textContent = JSON.stringify(payload, null, 2);
      if (payload.type === 'agent.transcription') transcript.textContent = payload.transcript;
      if (payload.type === 'agent.filler_text') message.textContent = payload.text;
      if (payload.type === 'agent.final_text') answer.textContent = payload.text;
      if (payload.type === 'tts.final.done' && payload.id && payload.turnIndex) {
        setGeneratedAudioUrl(`/api/open-source/sessions/${payload.id}/turns/${payload.turnIndex}/audio.wav`);
      }
      if (payload.type === 'agent.done') {
        message.textContent = `Turn ${payload.turnIndex} complete.`;
        if (payload.audioUrl) {
          setGeneratedAudioUrl(payload.audioUrl);
        }
      }
      if (payload.type === 'error') message.textContent = payload.message;
    }

    async function createSession() {
      const response = await fetch('/api/open-source/sessions', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ systemPrompt: document.getElementById('systemPrompt').value, mode: 'gemma-chatterbox' })
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

    async function connect() {
      connectButton.disabled = true;
      message.textContent = 'Connecting...';
      session = await createSession();
      localStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      pc = new RTCPeerConnection();
      pc.oniceconnectionstatechange = () => {
        message.textContent = `ICE: ${pc.iceConnectionState}`;
        if (pc.iceConnectionState === 'failed' || pc.iceConnectionState === 'disconnected') {
          message.textContent = 'WebRTC ICE failed. If this page is port-forwarded, forward the configured UDP ICE ports too, or use a TURN/TCP fallback.';
        }
      };
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'failed') {
          message.textContent = 'WebRTC connection failed before the data channel opened.';
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
        message.textContent = 'Connected. Press Start Turn, speak, then End Turn.';
        startButton.disabled = false;
        cancelButton.disabled = false;
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

    connectButton.onclick = () => connect().catch(error => {
      message.textContent = error.message;
      connectButton.disabled = false;
    });
    startButton.onclick = () => {
      transcript.textContent = '';
      answer.textContent = '';
      dc.send(JSON.stringify({ type: 'turn.start' }));
      startButton.disabled = true;
      endButton.disabled = false;
      message.textContent = 'Listening...';
    };
    endButton.onclick = () => {
      dc.send(JSON.stringify({ type: 'turn.end' }));
      endButton.disabled = true;
      startButton.disabled = false;
      message.textContent = 'Thinking...';
    };
    cancelButton.onclick = () => {
      dc.send(JSON.stringify({ type: 'turn.cancel' }));
      endButton.disabled = true;
      startButton.disabled = false;
    };

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

    let private statusPayload (agent: IVoiceAgentRuntime) =
        let status = agent.Status()
        {| ready = status.Ready
           serviceName = status.ServiceName
           mode = status.Mode
           workDir = status.WorkDir
           maxHistoryTurns = status.MaxHistoryTurns
           maxTurnAudioSeconds = status.MaxTurnAudioSeconds
           maxTurnAudioSamples24k = status.MaxTurnAudioSamples24k
           gemma = status.Gemma
           stt = status.Stt
           tts = status.Tts
           message = status.Message |}

    let private readSessionRequest (ctx: HttpContext) =
        task {
            if ctx.Request.ContentType <> null && ctx.Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) then
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

    let private acceptOffer (agent: IVoiceAgentRuntime) (webRtcStore: OpenSourceVoiceWebRtcSessionStore) (ctx: HttpContext) =
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

    let map (app: WebApplication) (agent: IVoiceAgentRuntime) (webRtcStore: OpenSourceVoiceWebRtcSessionStore) =
        app.MapGet("/", RequestDelegate(fun ctx -> task { do! writeText ctx "text/html; charset=utf-8" indexHtml })) |> ignore
        app.MapGet("/healthz", RequestDelegate(fun ctx -> writeJson ctx 200 {| ok = true |})) |> ignore
        app.MapGet("/api/status", RequestDelegate(fun ctx -> writeJson ctx 200 (statusPayload agent))) |> ignore
        app.MapPost("/api/open-source/sessions", RequestDelegate(fun ctx -> createSession agent ctx)) |> ignore
        app.MapPost("/api/open-source/sessions/{id}/webrtc/offer", RequestDelegate(fun ctx -> acceptOffer agent webRtcStore ctx)) |> ignore
        app.MapDelete("/api/open-source/sessions/{id}", RequestDelegate(fun ctx -> deleteSession webRtcStore ctx)) |> ignore
        app.MapMethods("/api/open-source/sessions/{id}/turns/{turnIndex}/details.json", [| "GET"; "HEAD" |], RequestDelegate(fun ctx -> serveTurnArtifact agent "details.json" ctx)) |> ignore
        app.MapMethods("/api/open-source/sessions/{id}/turns/{turnIndex}/audio.wav", [| "GET"; "HEAD" |], RequestDelegate(fun ctx -> serveTurnArtifact agent "audio.wav" ctx)) |> ignore
        app
