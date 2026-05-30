namespace FsVoice.Hosting.AspNetCore

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type OpenAiRealtimeClientSecret =
    { value: string
      expiresAt: int64 option }

type IOpenAiRealtimeRestClient =
    abstract CreateClientSecretAsync:
        session: JsonElement * cancellationToken: CancellationToken -> Task<OpenAiRealtimeClientSecret>

    abstract GetSdpAnswerAsync:
        clientSecret: string * offerSdp: string * cancellationToken: CancellationToken -> Task<string>

[<RequireQualifiedAccess>]
module OpenAiRealtimePayload =
    let sessionClientSecretJson (session: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)

        writer.WriteStartObject()
        writer.WritePropertyName("session")
        session.WriteTo writer
        writer.WriteEndObject()
        writer.Flush()

        Encoding.UTF8.GetString(stream.ToArray())

    let parseClientSecretResponse (json: string) =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement

        let tryReadSecretValue (element: JsonElement) =
            match element.TryGetProperty("value") with
            | true, property when property.ValueKind = JsonValueKind.String -> property.GetString()
            | _ -> null

        let value =
            match root.TryGetProperty("value") with
            | true, property when property.ValueKind = JsonValueKind.String -> property.GetString()
            | _ ->
                match root.TryGetProperty("client_secret") with
                | true, clientSecret when clientSecret.ValueKind = JsonValueKind.Object ->
                    tryReadSecretValue clientSecret
                | _ -> null

        if String.IsNullOrWhiteSpace value then
            invalidOp "OpenAI realtime client secret response did not include value."

        let expiresAt =
            match root.TryGetProperty("expires_at") with
            | true, property when property.ValueKind = JsonValueKind.Number ->
                match property.TryGetInt64() with
                | true, value -> Some value
                | _ -> None
            | _ ->
                match root.TryGetProperty("client_secret") with
                | true, clientSecret when clientSecret.ValueKind = JsonValueKind.Object ->
                    match clientSecret.TryGetProperty("expires_at") with
                    | true, property when property.ValueKind = JsonValueKind.Number ->
                        match property.TryGetInt64() with
                        | true, value -> Some value
                        | _ -> None
                    | _ -> None
                | _ -> None

        { value = value; expiresAt = expiresAt }

type OpenAiRealtimeRestClient
    (
        options: IOptions<OpenAiRealtimeOptions>,
        httpClientFactory: IHttpClientFactory,
        logger: ILogger<OpenAiRealtimeRestClient>
    ) =
    let createRequest (bearer: string) (uri: Uri) (content: HttpContent) =
        let configured = options.Value
        let request = new HttpRequestMessage(HttpMethod.Post, uri)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer)

        if not (String.IsNullOrWhiteSpace configured.SafetyIdentifier) then
            request.Headers.Add("OpenAI-Safety-Identifier", configured.SafetyIdentifier)

        request.Content <- content
        request

    let endpoint (path: string) =
        Uri($"{OpenAiRealtimeOptions.realtimeBaseUrl options.Value}/{path.TrimStart('/')}")

    let sendAsync (bearer: string) (uri: Uri) (content: HttpContent) (cancellationToken: CancellationToken) =
        task {
            let client = httpClientFactory.CreateClient("OpenAI")
            use request = createRequest bearer uri content
            return! client.SendAsync(request, cancellationToken)
        }

    interface IOpenAiRealtimeRestClient with
        member _.CreateClientSecretAsync(session: JsonElement, cancellationToken: CancellationToken) =
            task {
                let apiKey = OpenAiRealtimeOptions.requireApiKey options.Value
                let payload = OpenAiRealtimePayload.sessionClientSecretJson session
                use content = new StringContent(payload, Encoding.UTF8, "application/json")

                logger.LogInformation("Creating OpenAI realtime client secret for SIP call.")

                use! response = sendAsync apiKey (endpoint "client_secrets") content cancellationToken
                let! body = response.Content.ReadAsStringAsync(cancellationToken)

                if not response.IsSuccessStatusCode then
                    invalidOp $"OpenAI realtime client secret failed {(int response.StatusCode)}: {body}"

                return OpenAiRealtimePayload.parseClientSecretResponse body
            }

        member _.GetSdpAnswerAsync(clientSecret: string, offerSdp: string, cancellationToken: CancellationToken) =
            task {
                if String.IsNullOrWhiteSpace clientSecret then
                    invalidArg (nameof clientSecret) "An OpenAI realtime client secret is required."

                if String.IsNullOrWhiteSpace offerSdp then
                    invalidArg (nameof offerSdp) "An SDP offer is required."

                use content = new StringContent(offerSdp, Encoding.UTF8)
                content.Headers.ContentType <- MediaTypeHeaderValue("application/sdp")

                use! response = sendAsync clientSecret (endpoint "calls") content cancellationToken
                let! body = response.Content.ReadAsStringAsync(cancellationToken)

                if not response.IsSuccessStatusCode then
                    invalidOp $"OpenAI realtime SDP answer failed {(int response.StatusCode)}: {body}"

                return body
            }
