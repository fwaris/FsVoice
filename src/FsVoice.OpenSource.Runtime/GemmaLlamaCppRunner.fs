namespace FsVoice.OpenSource

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module private LlamaCppResponse =
    let tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | false, _ -> None

    let stringValue (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)
        |> Option.defaultValue ""

    let intValue (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            match value.TryGetInt32() with
            | true, number -> Some number
            | false, _ -> None)
        |> Option.defaultValue 0

    let floatValue (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            match value.TryGetDouble() with
            | true, number -> Some number
            | false, _ -> None)

    let tokenIds (element: JsonElement) =
        match tryProperty "tokens" element with
        | Some tokens when tokens.ValueKind = JsonValueKind.Array ->
            tokens.EnumerateArray()
            |> Seq.choose (fun token ->
                match token.TryGetInt32() with
                | true, value -> Some value
                | false, _ -> None)
            |> Seq.toArray
        | _ -> Array.empty

type GemmaLlamaCppRunner(options: GemmaRuntimeOptions, ?httpClient: HttpClient) =
    let endpoint =
        match Uri.TryCreate(options.LlamaCppEndpoint, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> uri
        | _ ->
            invalidArg
                (nameof options.LlamaCppEndpoint)
                $"LlamaCppEndpoint must be an absolute HTTP or HTTPS URL, not '{options.LlamaCppEndpoint}'."

    let ownsHttpClient = httpClient.IsNone

    let client =
        defaultArg httpClient (new HttpClient(Timeout = Timeout.InfiniteTimeSpan))

    let processor = GemmaProcessor()

    let requestTimeout =
        TimeSpan.FromSeconds(float (max 1 options.LlamaCppRequestTimeoutSeconds))

    let healthTimeout =
        TimeSpan.FromSeconds(float (max 1 options.LlamaCppHealthTimeoutSeconds))

    let completionUri = Uri(endpoint.ToString().TrimEnd('/') + "/completion")
    let healthUri = Uri(endpoint.ToString().TrimEnd('/') + "/health")

    let healthStatus () =
        try
            use timeout = new CancellationTokenSource(healthTimeout)
            use response = client.GetAsync(healthUri, timeout.Token).GetAwaiter().GetResult()

            if response.IsSuccessStatusCode then
                let body =
                    response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult()

                use document = JsonDocument.Parse body

                if
                    String.Equals(
                        LlamaCppResponse.stringValue "status" document.RootElement,
                        "ok",
                        StringComparison.OrdinalIgnoreCase
                    )
                then
                    Ok()
                else
                    Error $"llama.cpp health response was not ready: {body}"
            else
                Error $"llama.cpp health request returned HTTP {int response.StatusCode}."
        with ex ->
            Error $"llama.cpp is unavailable at {endpoint}: {ex.Message}"

    let promptForServer (request: GemmaGenerationRequest) =
        let prompt =
            processor.RenderChat(request.Messages, request.Tools, request.AddGenerationPrompt)

        if prompt.StartsWith("<bos>", StringComparison.Ordinal) then
            prompt, prompt.Substring("<bos>".Length)
        else
            prompt, prompt

    interface IGemmaRuntime with
        member _.Status() =
            match healthStatus () with
            | Ok() ->
                { Ready = true
                  ModelDir = endpoint.ToString()
                  Variant = options.LlamaCppModel
                  ExecutionProvider = "llama.cpp-server"
                  MissingFiles = Array.empty
                  LoadedSessions = [| "llama.cpp-http" |]
                  Message = $"Gemma GGUF is ready through llama.cpp at {endpoint}." }
            | Error message ->
                { Ready = false
                  ModelDir = endpoint.ToString()
                  Variant = options.LlamaCppModel
                  ExecutionProvider = "llama.cpp-server"
                  MissingFiles = Array.empty
                  LoadedSessions = Array.empty
                  Message = message }

        member _.GenerateAsync(request: GemmaGenerationRequest, cancellationToken: CancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                let originalPrompt, serverPrompt = promptForServer request

                let payload =
                    {| prompt = serverPrompt
                       n_predict = max 1 request.MaxNewTokens
                       temperature = max 0.0 request.Temperature
                       top_p = Math.Clamp(request.TopP, 0.0, 1.0)
                       top_k = max 0 request.TopK
                       stop = [| "<turn|>" |]
                       cache_prompt = false
                       return_tokens = true |}

                use message = new HttpRequestMessage(HttpMethod.Post, completionUri)

                message.Content <-
                    new StringContent(JsonSerializer.Serialize payload, Encoding.UTF8, "application/json")

                use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                timeout.CancelAfter requestTimeout
                let stopwatch = Stopwatch.StartNew()
                use! response = client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                let! body = response.Content.ReadAsStringAsync(timeout.Token)
                stopwatch.Stop()

                if not response.IsSuccessStatusCode then
                    let detail =
                        if body.Length <= 2000 then
                            body
                        else
                            body.Substring(0, 2000)

                    if detail.Contains("exceed_context_size_error", StringComparison.OrdinalIgnoreCase) then
                        invalidOp
                            $"llama.cpp completion returned HTTP {int response.StatusCode}: {detail} Restart llama.cpp with a larger context; the FsVoice launchers default to -ContextSize 16384."
                    else
                        invalidOp $"llama.cpp completion returned HTTP {int response.StatusCode}: {detail}"

                use document = JsonDocument.Parse body
                let root = document.RootElement
                let tokens = LlamaCppResponse.tokenIds root
                let predicted = LlamaCppResponse.intValue "tokens_predicted" root

                let outputTokenIds =
                    if tokens.Length > 0 then
                        tokens
                    else
                        Array.zeroCreate predicted

                let timings =
                    match LlamaCppResponse.tryProperty "timings" root with
                    | Some value -> value
                    | None -> Unchecked.defaultof<JsonElement>

                let timing name =
                    if timings.ValueKind = JsonValueKind.Object then
                        LlamaCppResponse.floatValue name timings
                    else
                        None

                let timingValues =
                    [ yield "totalMs", stopwatch.Elapsed.TotalMilliseconds

                      match timing "prompt_ms" with
                      | Some value -> yield "promptMs", value
                      | None -> ()

                      match timing "predicted_ms" with
                      | Some value -> yield "decodeMs", value
                      | None -> ()

                      match timing "prompt_per_second" with
                      | Some value -> yield "promptTokensPerSecond", value
                      | None -> ()

                      match timing "predicted_per_second" with
                      | Some value -> yield "decodeTokensPerSecond", value
                      | None -> () ]
                    |> Map.ofList

                let stopReason =
                    match LlamaCppResponse.stringValue "stop_type" root with
                    | "limit" -> "max_tokens"
                    | "eos" -> "eos"
                    | "word" -> "stop"
                    | value when String.IsNullOrWhiteSpace value -> "unknown"
                    | value -> value

                return
                    { Text = LlamaCppResponse.stringValue "content" root
                      Prompt = originalPrompt
                      InputTokenCount = LlamaCppResponse.intValue "tokens_evaluated" root
                      OutputTokenIds = outputTokenIds
                      StopReason = stopReason
                      TimingsMs = timingValues }
            }

    interface IDisposable with
        member _.Dispose() =
            if ownsHttpClient then
                client.Dispose()
