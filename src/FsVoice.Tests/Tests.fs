module FsVoice.Tests

open Xunit
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.AI
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FSharp.Control
open FsVoice.Hosting.AspNetCore
open FsVoice.Ctx
open FsVoice.Retrieval
open FsVoice.Core
open FsVoice.Platform
open RTOpenAI.Events

type MockChatClient() =
    interface IChatClient with
        member this.Dispose() = ()
        member this.GetService(serviceType: Type, serviceKey: obj) = null

        member this.GetResponseAsync(chatMessages, options, cancellationToken) =
            let response =
                ChatResponse(
                    ChatMessage(
                        ChatRole.Assistant,
                        """{"terms":["synonym1","technicalTerm1"],"rewrittenQueries":["technical query"],"sectionName":null,"queryType":"Question"}"""
                    )
                )

            Task.FromResult(response)

        member this.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type InvalidJsonSchemaChatClient() =
    interface IChatClient with
        member this.Dispose() = ()
        member this.GetService(serviceType: Type, serviceKey: obj) = null

        member this.GetResponseAsync(chatMessages, options, cancellationToken) =
            InvalidOperationException("HTTP 400 invalid_request_error invalid_json_schema: schema type error")
            |> Task.FromException<ChatResponse>

        member this.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type SchemaFallbackChatClient(responseText: string, ?rejectJsonMode: bool) =
    let mutable strictAttempts = 0
    let mutable jsonModeAttempts = 0
    let mutable plainAttempts = 0

    member _.StrictAttempts = strictAttempts
    member _.JsonModeAttempts = jsonModeAttempts
    member _.PlainAttempts = plainAttempts

    interface IChatClient with
        member _.Dispose() = ()
        member _.GetService(serviceType: Type, serviceKey: obj) = null

        member _.GetResponseAsync(chatMessages, options, cancellationToken) =
            let hasJsonResponseFormat =
                not (isNull options)
                && match options.ResponseFormat with
                   | :? ChatResponseFormatJson -> true
                   | _ -> false

            let strict =
                if isNull options || isNull options.AdditionalProperties then
                    false
                else
                    let mutable value = Unchecked.defaultof<obj>
                    let hasStrict = options.AdditionalProperties.TryGetValue("strict", &value)

                    if hasStrict then
                        match value with
                        | :? bool as strict -> strict
                        | _ -> false
                    else
                        false

            if strict then
                strictAttempts <- strictAttempts + 1

                InvalidOperationException("HTTP 400 invalid_request_error invalid_json_schema: schema type error")
                |> Task.FromException<ChatResponse>
            elif defaultArg rejectJsonMode false && hasJsonResponseFormat then
                jsonModeAttempts <- jsonModeAttempts + 1

                InvalidOperationException(
                    "HTTP 400 invalid_request_error invalid_json_schema: response_format rejected"
                )
                |> Task.FromException<ChatResponse>
            else
                if hasJsonResponseFormat then
                    jsonModeAttempts <- jsonModeAttempts + 1
                else
                    plainAttempts <- plainAttempts + 1

                ChatResponse(ChatMessage(ChatRole.Assistant, responseText)) |> Task.FromResult

        member _.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type CountingChatClient(responseText: string) =
    let mutable count = 0

    member _.Count = count

    interface IChatClient with
        member _.Dispose() = ()
        member _.GetService(serviceType: Type, serviceKey: obj) = null

        member _.GetResponseAsync(chatMessages, options, cancellationToken) =
            count <- count + 1
            ChatResponse(ChatMessage(ChatRole.Assistant, responseText)) |> Task.FromResult

        member _.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type RecordingChatClient(responseText: string) =
    let mutable messages: ChatMessage list = []
    let mutable maxOutputTokens = Nullable<int>()
    let mutable temperature = Nullable<float32>()

    member _.Messages = messages
    member _.MaxOutputTokens = maxOutputTokens
    member _.Temperature = temperature

    interface IChatClient with
        member _.Dispose() = ()
        member _.GetService(serviceType: Type, serviceKey: obj) = null

        member _.GetResponseAsync(chatMessages, options, cancellationToken) =
            messages <- chatMessages |> Seq.toList

            if not (isNull options) then
                maxOutputTokens <- options.MaxOutputTokens
                temperature <- options.Temperature

            ChatResponse(ChatMessage(ChatRole.Assistant, responseText)) |> Task.FromResult

        member _.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type SequencedChatClient(responseTexts: string list) =
    let mutable calls: Nullable<int> list = []

    member _.Calls = calls

    interface IChatClient with
        member _.Dispose() = ()
        member _.GetService(serviceType: Type, serviceKey: obj) = null

        member _.GetResponseAsync(chatMessages, options, cancellationToken) =
            let maxOutputTokens =
                if isNull options then
                    Nullable<int>()
                else
                    options.MaxOutputTokens

            calls <- calls @ [ maxOutputTokens ]

            let responseText =
                responseTexts |> List.tryItem (calls.Length - 1) |> Option.defaultValue ""

            ChatResponse(ChatMessage(ChatRole.Assistant, responseText)) |> Task.FromResult

        member _.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type LengthFinishChatClient(responseText: string) =
    let mutable maxOutputTokens = Nullable<int>()

    member _.MaxOutputTokens = maxOutputTokens

    interface IChatClient with
        member _.Dispose() = ()
        member _.GetService(serviceType: Type, serviceKey: obj) = null

        member _.GetResponseAsync(chatMessages, options, cancellationToken) =
            if not (isNull options) then
                maxOutputTokens <- options.MaxOutputTokens

            let response = ChatResponse(ChatMessage(ChatRole.Assistant, responseText))
            response.FinishReason <- Nullable(ChatFinishReason.Length)
            Task.FromResult(response)

        member _.GetStreamingResponseAsync(chatMessages, options, cancellationToken) =
            asyncSeq { yield ChatResponseUpdate() }

type FakeQaToolHost() =
    interface IQaToolHost with
        member _.Report _ = ()

        member _.SearchKnowledgeAsync(_, _, _) = Task.FromResult("No source context.")

        member _.SourceInventoryAsync _ = Task.FromResult("No selected sources.")

        member _.SearchMemoryAsync(_, _, _) = Task.FromResult("No memory context.")

        member _.SearchBlackboardAsync(_, _) =
            Task.FromResult("No blackboard context.")

type FakeContextProvider(source: KnowledgeSource) =
    interface IQaContextProvider with
        member _.ProviderId = "fake.context"
        member _.DisplayName = "Fake Context"
        member _.Sources = [ source ]

        member _.LoadAsync _ = Task.FromResult([])

        member _.RetrieveAsync(request, _) =
            [ { source = source
                index = 0
                sectionPath = []
                text = $"Fake context for {request.query}."
                score = 1.0f } ]
            |> Task.FromResult

        member _.InventoryAsync _ =
            Task.FromResult("Fake Context inventory.")

        member _.DisposeAsync() = ValueTask()

let private responsesResponse id status output : FsResponses.Response =
    { id = id
      ``object`` = Some "response"
      created_at = None
      status = status
      error = None
      incomplete_details = None
      instructions = None
      max_output_tokens = None
      model = FsResponses.Models.gpt_5
      metadata = None
      output = output
      parallel_tool_calls = Some false
      previous_response_id = None
      reasoning = None
      store = Some false
      temperature = 1.0f
      text = None
      tool_choice = None
      tools = []
      top_p = 1.0f
      truncation = Some "auto"
      usage = None
      user = None }

let private responsesCreatedEvent id =
    FsResponses.ResponseStreamEvent.ResponseCreated
        { event_id = None
          sequence_number = None
          response = responsesResponse id "created" [] }

let private responsesCompletedEventWithOutput id output =
    FsResponses.ResponseStreamEvent.ResponseCompleted
        { event_id = None
          sequence_number = None
          response = responsesResponse id "completed" output }

let private responsesCompletedEvent id text =
    responsesCompletedEventWithOutput
        id
        [ FsResponses.IOitem.Message
              { FsResponses.Message.Default with
                  id = Some $"msg_{id}"
                  status = Some "completed"
                  role = "assistant"
                  content = [ FsResponses.Content.Output_text { text = text; annotations = None } ] } ]

let private responsesCompletedEventWithCompaction id text compactionId encryptedContent =
    responsesCompletedEventWithOutput
        id
        [ FsResponses.IOitem.Message
              { FsResponses.Message.Default with
                  id = Some $"msg_{id}"
                  status = Some "completed"
                  role = "assistant"
                  content = [ FsResponses.Content.Output_text { text = text; annotations = None } ] }
          FsResponses.IOitem.Compaction
              { id = Some compactionId
                encrypted_content = encryptedContent } ]

let private responseUsage inputTokens outputTokens : FsResponses.Usage =
    { input_tokens = inputTokens
      output_tokens = outputTokens
      total_tokens = inputTokens + outputTokens
      input_tokens_details = None
      output_tokens_details = None }

let private withResponseUsage inputTokens event =
    match event with
    | FsResponses.ResponseStreamEvent.ResponseCompleted lifecycle ->
        FsResponses.ResponseStreamEvent.ResponseCompleted
            { lifecycle with
                response =
                    { lifecycle.response with
                        usage = Some(responseUsage inputTokens 10) } }
    | _ -> event

let private responsesCompletedEventWithUsage id text inputTokens =
    responsesCompletedEvent id text |> withResponseUsage inputTokens

let private responsesCompletedEmptyEvent id = responsesCompletedEventWithOutput id []

let private responsesTokenLimitEvent id text =
    let response =
        { (responsesResponse
              id
              "incomplete"
              [ FsResponses.IOitem.Message
                    { FsResponses.Message.Default with
                        id = Some $"msg_{id}"
                        status = Some "incomplete"
                        role = "assistant"
                        content = [ FsResponses.Content.Output_text { text = text; annotations = None } ] } ]) with
            incomplete_details = Some { reason = "max_output_tokens" } }

    FsResponses.ResponseStreamEvent.ResponseIncomplete
        { event_id = None
          sequence_number = None
          response = response }

let private responsesFunctionCallEvent id callId name arguments =
    responsesCompletedEventWithOutput
        id
        [ FsResponses.IOitem.Function_call
              { id = $"fc_{callId}"
                call_id = callId
                name = name
                arguments = arguments } ]

let private responsesFunctionCallEventWithText id text callId name arguments =
    responsesCompletedEventWithOutput
        id
        [ FsResponses.IOitem.Message
              { FsResponses.Message.Default with
                  id = Some $"msg_{id}"
                  status = Some "completed"
                  role = "assistant"
                  content = [ FsResponses.Content.Output_text { text = text; annotations = None } ] }
          FsResponses.IOitem.Function_call
              { id = $"fc_{callId}"
                call_id = callId
                name = name
                arguments = arguments } ]

let private responsesFunctionCallEvents id calls =
    responsesCompletedEventWithOutput
        id
        (calls
         |> List.map (fun (callId, name, arguments) ->
             FsResponses.IOitem.Function_call
                 { id = $"fc_{callId}"
                   call_id = callId
                   name = name
                   arguments = arguments }))

let private previousResponseNotFoundEvent =
    FsResponses.ResponseStreamEvent.Error
        { event_id = None
          sequence_number = None
          status = Some 404
          response_id = None
          error =
            { code = "previous_response_not_found"
              message = "Previous response not found."
              ``type`` = Some "invalid_request_error"
              param = Some "previous_response_id" } }

let private responsesErrorEvent code message =
    FsResponses.ResponseStreamEvent.Error
        { event_id = None
          sequence_number = None
          status = Some 500
          response_id = None
          error =
            { code = code
              message = message
              ``type`` = Some "server_error"
              param = None } }

type private ResponseRequestPath = | PersistentAnswer

type private TestResponsesTransport
    (
        prepare: CancellationToken -> Task<unit>,
        runRequest:
            FsResponses.WebSocketCreateRequest -> CancellationToken -> Task<FsResponses.ResponseStreamEvent list>
    ) =
    interface FsResponses.IResponsesTransport with
        member _.PrepareAsync cancellationToken = prepare cancellationToken

        member _.CreateAndCollectAsync(request, cancellationToken) = runRequest request cancellationToken

        member _.Dispose() = ()

let private testResponseWebSocketConfig =
    FsResponses.ResponseWebSocketConfig.create "test-api-key"

let private testQaSessionOptions storageRoot =
    QaSessionOptions.create storageRoot testResponseWebSocketConfig

let private responsesTransportOverrideAsync handler =
    new TestResponsesTransport(
        (fun _ -> task { return () }),
        (fun request cancellationToken -> handler PersistentAnswer request cancellationToken)
    )
    :> FsResponses.IResponsesTransport

let private responsesTransportOverrideWithPrepareAsync prepare handler =
    new TestResponsesTransport(
        prepare,
        (fun request cancellationToken -> handler PersistentAnswer request cancellationToken)
    )
    :> FsResponses.IResponsesTransport

let private responsesTransportOverride handler =
    responsesTransportOverrideAsync (fun path request cancellationToken ->
        handler path request cancellationToken |> Task.FromResult)

let private websocketAnswerTransport handler =
    responsesTransportOverride (fun _ request cancellationToken -> handler request cancellationToken)

let private inputTextFromResponseRequest (request: FsResponses.WebSocketCreateRequest) =
    request.input
    |> List.choose (function
        | FsResponses.IOitem.Message message ->
            message.content
            |> List.choose (function
                | FsResponses.Content.Input_text text -> Some text.text
                | _ -> None)
            |> String.concat "\n"
            |> Some
        | _ -> None)
    |> String.concat "\n"

let private responseToolNames (request: FsResponses.WebSocketCreateRequest) =
    request.tools
    |> Option.defaultValue []
    |> List.choose (function
        | FsResponses.Tool.Function fn -> Some fn.name
        | _ -> None)

let private responseContextManagementThresholds (request: FsResponses.WebSocketCreateRequest) =
    request.context_management
    |> Option.defaultValue []
    |> List.choose (function
        | FsResponses.ResponseContextManagement.Compaction value -> Some value.compact_threshold
        | _ -> None)

let private responseFunctionTools (request: FsResponses.WebSocketCreateRequest) =
    request.tools
    |> Option.defaultValue []
    |> List.choose (function
        | FsResponses.Tool.Function fn -> Some fn
        | _ -> None)

let private functionOutputsFromResponseRequest (request: FsResponses.WebSocketCreateRequest) =
    request.input
    |> List.choose (function
        | FsResponses.IOitem.Function_call_output output -> Some output
        | _ -> None)

let private waitForAsync description predicate =
    task {
        let deadline = DateTimeOffset.UtcNow.AddSeconds 5.

        while not (predicate ()) && DateTimeOffset.UtcNow < deadline do
            do! Task.Delay 20

        Assert.True(predicate (), description)
    }

type FakeDoclingRasterizer(result: Result<FsColbert.DoclingRasterPage list, string>) =
    interface FsColbert.IDoclingPageRasterizer with
        member _.RasterizeAsync _ = async { return result }

type CountingDoclingOcr(cells: FsColbert.DoclingOcrCell list) =
    let mutable calls = 0

    member _.Calls = calls

    interface FsColbert.IDoclingOcrProvider with
        member _.RecognizeAsync _ =
            async {
                calls <- calls + 1
                return Ok cells
            }

type FakeDoclingLayout(predictions: FsColbert.DoclingLayoutPrediction list) =
    interface FsColbert.IDoclingLayoutPredictor with
        member _.PredictLayoutAsync pages =
            async {
                let requested = pages |> List.map _.pageNo |> Set.ofList

                return
                    predictions
                    |> List.filter (fun prediction -> Set.contains prediction.pageNo requested)
                    |> Ok
            }

type FailingDoclingLayout() =
    interface FsColbert.IDoclingLayoutPredictor with
        member _.PredictLayoutAsync _ =
            async { return Error "layout should not run" }

type FakeDoclingFigureClassifier(classes: FsColbert.DoclingFigureClass list) =
    interface FsColbert.IDoclingFigureClassifier with
        member _.ClassifyAsync _ = async { return Ok classes }

let private keywordPassage (source: KnowledgeSource) index text : FsColbert.PassageRef =
    { sourceId = source.location
      sourceDisplayName = source.DisplayName
      sourceLocation = source.location
      index = index
      sectionPath = []
      text = text
      keywords = [] }

let private keywordOptions schemaVersion client =
    { KnowledgeSources.KeywordGenerationOptions.defaults with
        client = Some client
        schemaVersion = schemaVersion
        batchSize = 1
        parallelism = 2 }

let private tempStorageRoot () =
    Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))

let private testTranscript turnId text : TranscriptSnapshot =
    { turnId = turnId
      itemId = turnId
      revision = 1
      text = text
      isFinal = true
      receivedAt = DateTimeOffset.UtcNow }

let private testQaAnswer turnId text : QaAnswer =
    { turnId = turnId
      answer = text
      model = "test"
      context = []
      sourceRetrievalElapsedMs = 0.0
      inventory = []
      toolObservations = []
      timedOut = false
      createdAt = DateTimeOffset.UtcNow }

let private boardFromRecords records =
    records
    |> List.fold (fun board record -> Blackboard.add record board) (Blackboard.empty 200)

let private invokeSessionTool (session: QaSession) toolName args =
    task {
        let tool =
            session.ToolCatalog.tools
            |> List.find (fun tool ->
                tool.PluginName = "FsVoiceTools"
                && String.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))

        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        for name, value in args do
            dict[name] <- value

        let! result = tool.InvokeAsync(dict, CancellationToken.None)
        return result.content
    }

let private seedDurableMemory path (snapshot: TranscriptSnapshot) answer =
    let store, updates, logs =
        DurableMemory.commitProposals
            (DurableMemory.empty (Some path))
            (DurableMemory.proposalsFromExchange snapshot answer)

    Assert.NotEmpty updates
    store, updates, logs

let private hashText (value: string) =
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes value

    sha.ComputeHash bytes
    |> Convert.ToHexString
    |> fun hash -> hash.ToLowerInvariant()

let private testSourceKindId sourceKind =
    match sourceKind with
    | Pdf -> "pdf"
    | Markdown -> "markdown"
    | Json -> "json"

let private testSourceFingerprint (source: KnowledgeSource) =
    let filePart =
        let info = FileInfo source.location

        if info.Exists then
            $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
        else
            source.location

    $"{testSourceKindId source.kind}:{filePart}"

let private doclingNativeCell text l bottom r top : FsColbert.DoclingOcrCell =
    { text = text
      bbox = FsColbert.DoclingGeometry.bottomLeftBox l bottom r top
      confidence = None }

let private doclingOcrCell text l t r b : FsColbert.DoclingOcrCell =
    { text = text
      bbox = FsColbert.DoclingGeometry.topLeftBox l t r b
      confidence = Some 0.9 }

let private doclingCluster id label l t r b : FsColbert.DoclingLayoutCluster =
    { id = id
      label = label
      confidence = 0.95f
      bbox = FsColbert.DoclingGeometry.topLeftBox l t r b
      cells = [] }

let private visualDescriptionOptions storageRoot client =
    { PdfVisualDescriptionOptions.defaults with
        enabled = true
        client = client
        modelId = $"test-visual-model-{Guid.NewGuid():N}"
        schemaVersion = $"test-visual-schema-{Guid.NewGuid():N}"
        cacheStorageRoot = Some storageRoot
        parallelism = 1 }

let private readHybridVisualPassages options report path =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 400.0 }
                      cells = [ doclingNativeCell "Native paragraph describing nearby content" 20.0 285.0 220.0 310.0 ] }

                return Ok [ nativePage ]
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters =
                      [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 80.0 260.0 130.0
                        doclingCluster 1 FsColbert.DoclingLabel.Picture 250.0 190.0 360.0 300.0 ] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource = FsColbert.PassageSource.create path "Visual Doc" path

        return!
            DoclingHybrid.readPdfPassagesWithProviders
                options
                report
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                path
                rasterizer
                nativeProvider
                None
                layout
                None
    }

let private testKeywordCachePath storageRoot sourceFingerprint (options: KnowledgeSources.KeywordGenerationOptions) =
    let folder = Path.Combine(storageRoot, "FsVoice", "FsColbert", "KeywordCache")
    Directory.CreateDirectory folder |> ignore

    let fileName =
        [ yield sourceFingerprint
          yield options.modelId
          yield options.schemaVersion
          yield QaPlugInProfile.fingerprint options.plugInProfile

          if not (String.IsNullOrWhiteSpace options.plugInFingerprint) then
              yield options.plugInFingerprint ]
        |> String.concat "\n"
        |> hashText

    Path.Combine(folder, $"{fileName}.jsonl")

let private seedKeywordCache
    storageRoot
    (source: KnowledgeSource)
    (options: KnowledgeSources.KeywordGenerationOptions)
    (passage: FsColbert.PassageRef)
    keywords
    =
    let sourceFingerprint = testSourceFingerprint source
    let textHash = hashText passage.text

    let key =
        [ yield sourceFingerprint
          yield string passage.index
          yield textHash
          yield options.modelId
          yield options.schemaVersion
          yield QaPlugInProfile.fingerprint options.plugInProfile

          if not (String.IsNullOrWhiteSpace options.plugInFingerprint) then
              yield options.plugInFingerprint ]
        |> String.concat "\n"
        |> hashText

    let cachePath = testKeywordCachePath storageRoot sourceFingerprint options

    let line =
        JsonSerializer.Serialize(
            {| key = key
               sourceFingerprint = sourceFingerprint
               passageIndex = passage.index
               textHash = textHash
               modelId = options.modelId
               schemaVersion = options.schemaVersion
               profileFingerprint = QaPlugInProfile.fingerprint options.plugInProfile
               keywords = keywords |}
        )

    File.AppendAllText(cachePath, line + Environment.NewLine, Encoding.UTF8)

let private fakeEmbedding () : FsColbert.MultiVector =
    let dim = FsColbert.EncoderConfig.mxbaiEdgeColbert.embeddingDim
    let values = Array.zeroCreate<float32> dim
    values[0] <- 1.0f

    { tokenIds = [| 1 |]
      vectors = values
      tokenCount = 1
      embeddingDim = dim }

let private fakeIndexedPassage (source: KnowledgeSource) index text keywords : FsColbert.IndexedPassage =
    let reference: FsColbert.PassageRef =
        { sourceId = source.location
          sourceDisplayName = source.DisplayName
          sourceLocation = source.location
          index = index
          sectionPath = []
          text = text
          keywords = keywords }

    { reference = reference
      embedding = fakeEmbedding ()
      terms = Set.empty }

let private fakeColbertIndex passages : FsColbert.ColbertIndex =
    { config = FsColbert.EncoderConfig.mxbaiEdgeColbert
      chunkOptions = FsColbert.ChunkOptions.fsKameDefaults
      tfidfOptions = FsColbert.TfidfOptions.defaults
      passages = passages
      tfidf = FsColbert.Tfidf.buildWithOptions FsColbert.TfidfOptions.defaults passages
      createdAt = DateTimeOffset.UtcNow }

let private prebuiltFolder storageRoot =
    let folder = Path.Combine(storageRoot, "FsVoice", "FsColbert", "Prebuilt")
    Directory.CreateDirectory folder |> ignore
    folder

let private speak2DocsPrebuiltFolder storageRoot =
    let folder = Path.Combine(storageRoot, "Speak2Docs", "FsColbert", "Prebuilt")
    Directory.CreateDirectory folder |> ignore
    folder

let private persistedIndexFolder storageRoot =
    let folder = Path.Combine(storageRoot, "FsVoice", "FsColbert", "Indexes")
    Directory.CreateDirectory folder |> ignore
    folder

[<Fact>]
let ``picked source files classify supported extensions`` () =
    Assert.Equal(Speak2Docs.PickedSourceFileKind.PickedDocument, Speak2Docs.PickedSourceFiles.kind "guide.pdf")
    Assert.Equal(Speak2Docs.PickedSourceFileKind.PickedDocument, Speak2Docs.PickedSourceFiles.kind "guide.md")
    Assert.Equal(Speak2Docs.PickedSourceFileKind.PickedIndexBundle, Speak2Docs.PickedSourceFiles.kind "bundle.zip")
    Assert.True(Speak2Docs.PickedSourceFiles.isDocument "guide.pdf")
    Assert.True(Speak2Docs.PickedSourceFiles.isDocument "guide.md")
    Assert.True(Speak2Docs.PickedSourceFiles.isIndexBundle "bundle.zip")

[<Fact>]
let ``picked source files reject unsupported extensions`` () =
    Assert.Equal(
        Speak2Docs.PickedSourceFileKind.UnsupportedPickedSourceFile,
        Speak2Docs.PickedSourceFiles.kind "guide.markdown"
    )

    Assert.Equal(
        Speak2Docs.PickedSourceFileKind.UnsupportedPickedSourceFile,
        Speak2Docs.PickedSourceFiles.kind "notes.txt"
    )

    Assert.Equal(
        Speak2Docs.PickedSourceFileKind.UnsupportedPickedSourceFile,
        Speak2Docs.PickedSourceFiles.kind "index.fsci"
    )

    Assert.False(Speak2Docs.PickedSourceFiles.isDocument "guide.markdown")
    Assert.False(Speak2Docs.PickedSourceFiles.isIndexBundle "index.fsci")

[<Fact>]
let ``ready built-in documents can be selected`` () =
    let builtInDoc: Speak2Docs.PdfDocumentSource =
        { id = "prebuilt-example"
          kind = Speak2Docs.MarkdownFile
          displayName = "Built-in"
          storedPath = "/tmp/built-in.md"
          originalPath = "app://FsColbertIndexes/documents/built-in.md"
          selected = true
          status = Speak2Docs.Ready
          chunkCount = 3
          error = None }

    let failedBuiltInDoc =
        { builtInDoc with
            status = Speak2Docs.Failed
            selected = false
            error = Some "Failed" }

    Assert.True(Speak2Docs.PdfDocuments.isBuiltIn builtInDoc)
    Assert.True(Speak2Docs.PdfDocuments.canSelect builtInDoc)
    Assert.False(Speak2Docs.PdfDocuments.canSelect failedBuiltInDoc)

[<Theory>]
[<InlineData("gpt-5.5")>]
[<InlineData("gpt-5.5-mini")>]
[<InlineData(" GPT-5.5 ")>]
let ``model capability omits temperature for models that reject it`` modelId =
    Assert.False(ModelCapabilities.supportsTemperature modelId)

[<Theory>]
[<InlineData("gpt-5.1")>]
[<InlineData("gpt-4.1-mini")>]
let ``model capability keeps temperature for supported models`` modelId =
    Assert.True(ModelCapabilities.supportsTemperature modelId)

[<Fact>]
let ``async priority queue dequeues lower priority first and preserves FIFO`` () =
    task {
        let queue = AsyncPriorityQueue<string>()
        queue.Enqueue("low", 10)
        queue.Enqueue("first", 1)
        queue.Enqueue("second", 1)

        let! first = queue.DequeueTask()
        let! second = queue.DequeueTask()
        let! low = queue.DequeueTask()

        Assert.Equal("first", first)
        Assert.Equal("second", second)
        Assert.Equal("low", low)
    }

[<Fact>]
let ``async priority queue completion releases waiters`` () =
    task {
        let queue = AsyncPriorityQueue<string>()
        let pending = queue.DequeueTask()

        queue.Complete()

        do! Assert.ThrowsAsync<InvalidOperationException>(fun () -> pending) :> Task
    }

[<Fact>]
let ``generic PlugIn supplies model roles and runtime defaults`` () =
    let plugin = new GenericQaPlugIn() :> IQaPlugIn
    let definition = plugin.Definition |> PlugInDefinition.sanitize

    Assert.Equal(PlugInDefinition.currentContractVersion, plugin.ContractVersion)
    Assert.Equal("generic", definition.id)
    Assert.Equal("gpt-realtime-2", (PlugInDefinition.model Realtime definition).modelId)
    Assert.Equal("gpt-5.5", (PlugInDefinition.model Answer definition).modelId)
    Assert.Equal("gpt-5-mini", (PlugInDefinition.model Keyword definition).modelId)
    Assert.False(definition.runtime.enableQueryExpansion)

[<Fact>]
let ``default realtime prompt is source first for document requests`` () =
    let instructions = DefaultPlugInPrompts.realtimeInstructions

    Assert.Contains("selected sources", instructions)
    Assert.Contains("source-like requests", instructions)
    Assert.Contains("abstract", instructions)
    Assert.Contains("section", instructions)
    Assert.Contains("summarize the abstract of the paper", instructions)
    Assert.Contains("QUERY_ORACLE", instructions)
    Assert.Contains("needs_external_context = true", instructions)
    Assert.Contains("never repeat the startup selected-document-count greeting", instructions)

[<Fact>]
let ``qa session applies custom prompts and answer role options`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                [ responsesCompletedEvent "resp_custom_answer" "custom response" ])

        let answerConfig =
            { ModelRoleConfig.create "gpt-4.1-mini" with
                maxOutputTokens = Some 123
                temperature = Some 0.1f }

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                prompts =
                    { PromptSet.empty with
                        answerSystem = Some "CUSTOM SYSTEM"
                        answerUserTemplate = Some "Q={{question}}\nCTX={{sourceContext}}\nINV={{sourceInventory}}" } }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("custom response", answer.answer)
        Assert.Single(captured) |> ignore
        Assert.Equal(Some "CUSTOM SYSTEM", captured[0].instructions)
        Assert.Equal(Some 123, captured[0].max_output_tokens)
        Assert.Equal(Some 0.1f, captured[0].temperature)
        Assert.Contains("Q=What does the fake context say?", inputTextFromResponseRequest captured[0])
        Assert.Contains("Fake context for What does the fake context say?", inputTextFromResponseRequest captured[0])
    }

[<Fact>]
let ``qa session answers through responses websocket transport`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                [ responsesCompletedEvent "resp_socket_1" "socket response" ])

        let answerConfig =
            { ModelRoleConfig.create "gpt-4.1-mini" with
                maxOutputTokens = Some 123
                temperature = Some 0.1f }

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerRequireToolCall = true
                answerPromptCacheKey = Some "test-cache-key"
                answerPromptCacheRetention = Some "24h"
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                prompts =
                    { PromptSet.empty with
                        answerSystem = Some "CUSTOM SYSTEM"
                        answerUserTemplate = Some "Q={{question}}\nCTX={{sourceContext}}\nINV={{sourceInventory}}" } }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("socket response", answer.answer)
        Assert.Single(captured) |> ignore
        Assert.Equal("gpt-4.1-mini", captured[0].model)
        Assert.Equal(Some 123, captured[0].max_output_tokens)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Equal(Some 0.1f, captured[0].temperature)
        Assert.Equal(Some true, captured[0].store)
        Assert.Equal(None, captured[0].previous_response_id)
        Assert.Equal(Some "test-cache-key", captured[0].prompt_cache_key)
        Assert.Equal(Some "24h", captured[0].prompt_cache_retention)
        Assert.Equal(Some "CUSTOM SYSTEM", captured[0].instructions)
        Assert.Equal(Some FsResponses.ToolChoice.Required, captured[0].tool_choice)
        Assert.Contains("selected_source_search", responseToolNames captured[0])
        Assert.Contains("source_inventory", responseToolNames captured[0])
        Assert.Contains("Q=What does the fake context say?", inputTextFromResponseRequest captured[0])
        Assert.Contains("Fake context for What does the fake context say?", inputTextFromResponseRequest captured[0])
    }

[<Fact>]
let ``qa session prepares responses websocket before first answer`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-prepared"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable prepareCount = 0

        let transport =
            responsesTransportOverrideWithPrepareAsync
                (fun _ ->
                    task {
                        prepareCount <- prepareCount + 1
                        return ()
                    })
                (fun _ request _ ->
                    task {
                        captured.Add request

                        return [ responsesCompletedEvent "resp_answer" "prepared socket response" ]
                    })

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)

        let preparer = session :> IQaAnswerTransportPreparer
        do! preparer.PrepareAnswerTransportAsync(CancellationToken.None)

        Assert.Equal(1, prepareCount)
        Assert.Empty(captured)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("prepared socket response", answer.answer)
        Assert.Single(captured) |> ignore
        Assert.Equal(None, captured[0].previous_response_id)
        Assert.Equal(Some true, captured[0].generate)
        Assert.True(Option.isSome captured[0].instructions)
        Assert.Contains("selected_source_search", responseToolNames captured[0])
        Assert.Contains("source_inventory", responseToolNames captured[0])
    }

[<Fact>]
let ``qa session options default to persistent answer websocket`` () =
    let options = testQaSessionOptions (tempStorageRoot ())

    Assert.Equal(QaAnswerTransportMode.PersistentWebSocket, options.answerTransportMode)

[<Fact>]
let ``qa session websocket answer uses previous response id after first turn`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-append"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable answerNumber = 0

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                answerNumber <- answerNumber + 1
                [ responsesCompletedEvent $"resp_{answerNumber}" $"socket answer {answerNumber}" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)

        let request question =
            { turnId = Guid.NewGuid().ToString("N")
              question = question
              realtimeJudgement = None
              deadline = None }

        let! first = session.AnswerAsync(request "first question", CancellationToken.None)
        let! second = session.AnswerAsync(request "second question", CancellationToken.None)

        Assert.Equal("socket answer 1", first.answer)
        Assert.Equal("socket answer 2", second.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(None, captured[0].previous_response_id)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Single(captured[0].input) |> ignore
        Assert.Contains("source_inventory", responseToolNames captured[0])
        Assert.Contains("first question", inputTextFromResponseRequest captured[0])
        Assert.Equal(Some "resp_1", captured[1].previous_response_id)
        Assert.Equal(Some true, captured[1].generate)
        Assert.Single(captured[1].input) |> ignore
        Assert.Contains("selected_source_search", responseToolNames captured[1])
        Assert.Contains("source_inventory", responseToolNames captured[1])
        Assert.Contains("second question", inputTextFromResponseRequest captured[1])
    }

[<Fact>]
let ``qa session canceled answer releases serialized answer gate`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-answer-cancel"
              enabled = true }

        let firstRequestEntered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let captured = ResizeArray<string>()
        let gate = obj ()
        let mutable requestNumber = 0

        let transport =
            responsesTransportOverrideAsync (fun _ _ cancellationToken ->
                task {
                    let current =
                        lock gate (fun () ->
                            requestNumber <- requestNumber + 1
                            requestNumber)

                    if current = 1 then
                        firstRequestEntered.TrySetResult() |> ignore
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return [ responsesCompletedEvent "resp_never" "never" ]
                    else
                        lock gate (fun () -> captured.Add $"request-{current}")
                        return [ responsesCompletedEvent $"resp_{current}" $"answer {current}" ]
                })

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)
        use firstCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)

        let request question =
            { turnId = Guid.NewGuid().ToString("N")
              question = question
              realtimeJudgement = None
              deadline = None }

        let firstTask =
            session.AnswerAsync(request "first question", firstCancellation.Token)

        do! firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> firstTask) :> Task

        let! second = session.AnswerAsync(request "second question", CancellationToken.None)

        Assert.Equal("answer 2", second.answer)
        Assert.Equal<string list>([ "request-2" ], lock gate (fun () -> captured |> Seq.toList))
    }

[<Fact>]
let ``qa session serializes concurrent responses websocket answers`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-serialized"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let gate = obj ()
        let mutable activeRequests = 0
        let mutable maxActiveRequests = 0

        let firstRequestEntered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let releaseFirstRequest =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let capturedSnapshot () =
            lock gate (fun () -> captured |> Seq.toList)

        let transport =
            responsesTransportOverrideAsync (fun _ request cancellationToken ->
                task {
                    let requestNumber =
                        lock gate (fun () ->
                            activeRequests <- activeRequests + 1
                            maxActiveRequests <- max maxActiveRequests activeRequests
                            captured.Add request
                            captured.Count)

                    try
                        if requestNumber = 1 then
                            firstRequestEntered.TrySetResult() |> ignore
                            do! releaseFirstRequest.Task.WaitAsync(cancellationToken)

                        return [ responsesCompletedEvent $"resp_{requestNumber}" $"socket answer {requestNumber}" ]
                    finally
                        lock gate (fun () -> activeRequests <- activeRequests - 1)
                })

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)

        let request question =
            { turnId = Guid.NewGuid().ToString("N")
              question = question
              realtimeJudgement = None
              deadline = None }

        let firstTask =
            session.AnswerAsync(request "first question", CancellationToken.None)

        do! firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

        let secondTask =
            session.AnswerAsync(request "second question", CancellationToken.None)

        do! Task.Delay 100

        Assert.Single(capturedSnapshot ()) |> ignore

        releaseFirstRequest.TrySetResult() |> ignore

        let! first = firstTask
        let! second = secondTask
        let requests = capturedSnapshot ()

        Assert.Equal("socket answer 1", first.answer)
        Assert.Equal("socket answer 2", second.answer)
        Assert.Equal(2, requests.Length)
        Assert.Equal(1, maxActiveRequests)
        Assert.Equal(None, requests[0].previous_response_id)
        Assert.Equal(Some "resp_1", requests[1].previous_response_id)
        Assert.Contains("first question", inputTextFromResponseRequest requests[0])
        Assert.Contains("second question", inputTextFromResponseRequest requests[1])
    }

[<Fact>]
let ``qa session websocket sends OpenAI compaction config without internal maintenance`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-compact"
              enabled = true }

        let captured =
            ResizeArray<ResponseRequestPath * FsResponses.WebSocketCreateRequest>()

        let capturedGate = obj ()

        let capturedSnapshot () =
            lock capturedGate (fun () -> captured |> Seq.toList)

        let mutable answerNumber = 0

        let transport =
            responsesTransportOverride (fun path request _ ->
                lock capturedGate (fun () -> captured.Add((path, request)))

                answerNumber <- answerNumber + 1
                [ responsesCompletedEvent $"resp_answer_{answerNumber}" $"socket answer {answerNumber}" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)

        let request question =
            { turnId = Guid.NewGuid().ToString("N")
              question = question
              realtimeJudgement = None
              deadline = None }

        let! first = session.AnswerAsync(request "first question", CancellationToken.None)
        let! second = session.AnswerAsync(request "second question", CancellationToken.None)

        Assert.Equal("socket answer 1", first.answer)
        Assert.Equal("socket answer 2", second.answer)

        let requests = capturedSnapshot ()

        Assert.Equal(2, requests.Length)

        for path, request in requests do
            Assert.Equal(PersistentAnswer, path)
            Assert.True([ 200000 ] = responseContextManagementThresholds request)
            Assert.Equal(Some true, request.generate)
            Assert.DoesNotContain("Compact Speak2Docs", inputTextFromResponseRequest request)

        let _, firstRequest = requests[0]
        let _, secondRequest = requests[1]

        Assert.Equal(None, firstRequest.previous_response_id)
        Assert.Equal(Some "resp_answer_1", secondRequest.previous_response_id)
        Assert.DoesNotContain("blackboard_search", responseToolNames firstRequest)
        Assert.Contains("selected_source_search", responseToolNames secondRequest)
        Assert.Contains("source_inventory", responseToolNames secondRequest)
    }

[<Fact>]
let ``qa session websocket omits OpenAI compaction config when disabled`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-no-compact"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                [ responsesCompletedEvent "resp_answer" "socket answer" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerOpenAiCompactionThresholdTokens = None }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "first question"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("socket answer", answer.answer)
        let capturedRequest = Assert.Single(captured)
        Assert.Empty(responseContextManagementThresholds capturedRequest)
    }

[<Fact>]
let ``qa session websocket answer replays append only history when previous response is missing`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-replay"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable callNumber = 0

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                callNumber <- callNumber + 1

                match callNumber with
                | 1 -> [ responsesCompletedEvent "resp_1" "first socket answer" ]
                | 2 -> [ previousResponseNotFoundEvent ]
                | _ -> [ responsesCompletedEvent "resp_replayed" "replayed socket answer" ])

        let logs = ResizeArray<string>()

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request question =
            { turnId = Guid.NewGuid().ToString("N")
              question = question
              realtimeJudgement = None
              deadline = None }

        let! first = session.AnswerAsync(request "first question", CancellationToken.None)
        let! second = session.AnswerAsync(request "second question", CancellationToken.None)

        Assert.Equal("first socket answer", first.answer)
        Assert.Equal("replayed socket answer", second.answer)
        Assert.Equal(3, captured.Count)
        Assert.Equal(None, captured[0].previous_response_id)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Equal(Some "resp_1", captured[1].previous_response_id)
        Assert.Equal(Some true, captured[1].generate)
        Assert.Equal(None, captured[2].previous_response_id)
        Assert.Equal(Some true, captured[2].generate)
        Assert.Equal(Some true, captured[2].store)

        Assert.True(
            captured[2].input.Length >= 3,
            "Replay should include prior user, prior assistant, and current user items."
        )

        Assert.Contains("first question", inputTextFromResponseRequest captured[2])
        Assert.Contains("second question", inputTextFromResponseRequest captured[2])
        Assert.Contains(logs, fun log -> log.Contains("previous_response_id was not found"))
    }

[<Fact>]
let ``qa session websocket dispatches model requested tools`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-tools"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                match captured.Count with
                | 1 -> [ responsesFunctionCallEvent "resp_tool_call" "call_inventory" "source_inventory" "{}" ]
                | _ -> [ responsesCompletedEvent "resp_tool_final" "final answer from tool" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ] }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Which sources are selected?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("final answer from tool", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(None, captured[0].previous_response_id)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Contains("source_inventory", responseToolNames captured[0])
        Assert.Contains("selected_source_search", responseToolNames captured[0])

        for tool in responseFunctionTools captured[0] do
            Assert.True(tool.strict)

            let expectedRequired =
                tool.parameters.properties |> Map.keys |> Seq.sort |> Seq.toList

            Assert.True((expectedRequired = (tool.parameters.required |> List.sort)))

        Assert.Equal(Some "resp_tool_call", captured[1].previous_response_id)
        Assert.Equal(Some true, captured[1].generate)
        Assert.Contains("source_inventory", responseToolNames captured[1])
        Assert.Contains("selected_source_search", responseToolNames captured[1])

        let output = Assert.Single(functionOutputsFromResponseRequest captured[1])
        Assert.Equal("call_inventory", output.call_id)
        Assert.Contains("Fake Context inventory.", output.output)

        let observation = Assert.Single(answer.toolObservations)
        Assert.Equal("source_inventory", observation.toolName)
        Assert.Contains("Fake Context inventory.", observation.content)
    }

[<Fact>]
let ``qa session websocket sends outputs for overflow tool calls`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-many-tools"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let logs = ResizeArray<string>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                match captured.Count with
                | 1 ->
                    [ responsesFunctionCallEvents
                          "resp_many_tools_call"
                          [ for index in 1..10 -> $"call_inventory_{index}", "source_inventory", "{}" ] ]
                | _ -> [ responsesCompletedEvent "resp_many_tools_final" "final answer after many tools" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Use many tools."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("final answer after many tools", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(8, answer.toolObservations.Length)

        let outputs = functionOutputsFromResponseRequest captured[1]
        Assert.Equal(10, outputs.Length)
        Assert.Equal<string list>([ for index in 1..10 -> $"call_inventory_{index}" ], outputs |> List.map _.call_id)

        let skippedOutputs = outputs |> List.skip 8
        Assert.All(skippedOutputs, fun output -> Assert.Contains("more than 8 tool calls", output.output))
        Assert.Contains(logs, fun log -> log.Contains("more than 8 tool calls"))
    }

[<Fact>]
let ``qa session websocket forces no-tool final answer after tool round budget`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-tool-budget"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let logs = ResizeArray<string>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                let inputText = inputTextFromResponseRequest request

                if inputText.Contains("second question", StringComparison.OrdinalIgnoreCase) then
                    [ responsesCompletedEvent "resp_after_budget" "second answer after tool budget" ]
                elif request.tool_choice = Some FsResponses.ToolChoice.None then
                    [ responsesCompletedEvent "resp_tool_budget_final" "final answer after tool budget" ]
                else
                    [ responsesFunctionCallEvent
                          $"resp_tool_budget_{captured.Count}"
                          $"call_inventory_{captured.Count}"
                          "source_inventory"
                          "{}" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Keep checking tools forever?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("final answer after tool budget", answer.answer)
        Assert.DoesNotContain("stopping extra searches", answer.answer)
        Assert.Equal(4, captured.Count)
        Assert.Equal(3, answer.toolObservations.Length)

        Assert.Equal(Some "resp_tool_budget_2", captured[2].previous_response_id)
        Assert.Equal(Some true, captured[2].generate)

        Assert.Equal(None, captured[3].previous_response_id)
        Assert.Equal(Some true, captured[3].generate)
        Assert.Equal(Some FsResponses.ToolChoice.None, captured[3].tool_choice)
        Assert.Empty(responseToolNames captured[3])
        Assert.Empty(functionOutputsFromResponseRequest captured[3])

        let finalizerInput = inputTextFromResponseRequest captured[3]
        Assert.Contains("Keep checking tools forever?", finalizerInput)
        Assert.Contains("Fake Context inventory.", finalizerInput)
        Assert.Contains(logs, fun log -> log.Contains("evidence-only finalizer"))

        let secondRequest =
            { turnId = Guid.NewGuid().ToString("N")
              question = "second question"
              realtimeJudgement = None
              deadline = None }

        let! secondAnswer = session.AnswerAsync(secondRequest, CancellationToken.None)

        Assert.Equal("second answer after tool budget", secondAnswer.answer)
        Assert.Equal(5, captured.Count)
        Assert.Equal(Some "resp_tool_budget_final", captured[4].previous_response_id)
        Assert.DoesNotContain("Keep checking tools forever?", inputTextFromResponseRequest captured[4])
        Assert.Contains("second question", inputTextFromResponseRequest captured[4])
    }

[<Fact>]
let ``qa session websocket uses configured tool call loop limit`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-tool-loop-limit"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                if request.tool_choice = Some FsResponses.ToolChoice.None then
                    [ responsesCompletedEvent "resp_tool_loop_final" "final answer after one tool round" ]
                else
                    [ responsesFunctionCallEvent "resp_tool_loop_call" "call_inventory" "source_inventory" "{}" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerToolCallLoopLimit = 1 }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Use the tool once."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("final answer after one tool round", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Equal(None, captured[1].previous_response_id)
        Assert.Equal(Some FsResponses.ToolChoice.None, captured[1].tool_choice)
        Assert.Contains("Final answer synthesis mode", captured[1].instructions.Value)
        Assert.Empty(responseToolNames captured[1])
        Assert.Empty(functionOutputsFromResponseRequest captured[1])

        let finalizerInput = inputTextFromResponseRequest captured[1]
        Assert.Contains("Use the tool once.", finalizerInput)
        Assert.Contains("Fake Context inventory.", finalizerInput)
    }

[<Fact>]
let ``qa session evidence finalizer replaces tool-limit musing with clean answer`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-tool-limit-musing"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                match captured.Count with
                | 1 ->
                    [ responsesFunctionCallEventWithText
                          "resp_musing_tool_call"
                          "I should inspect another source before answering, and I can use the inventory tool."
                          "call_inventory"
                          "source_inventory"
                          "{}" ]
                | _ -> [ responsesCompletedEvent "resp_musing_final" "clean final answer from evidence" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerToolCallLoopLimit = 1 }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Summarize the selected source."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("clean final answer from evidence", answer.answer)
        Assert.DoesNotContain("I should", answer.answer)
        Assert.DoesNotContain("I can use", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(None, captured[1].previous_response_id)
        Assert.Equal(Some FsResponses.ToolChoice.None, captured[1].tool_choice)
        Assert.Contains("Final answer synthesis mode", captured[1].instructions.Value)
        Assert.Empty(responseToolNames captured[1])
        Assert.Empty(functionOutputsFromResponseRequest captured[1])

        let finalizerInput = inputTextFromResponseRequest captured[1]
        Assert.Contains("Summarize the selected source.", finalizerInput)
        Assert.Contains("Fake Context inventory.", finalizerInput)
    }

[<Theory>]
[<InlineData("empty")>]
[<InlineData("error")>]
[<InlineData("token_limit")>]
[<InlineData("tool_call")>]
let ``qa session evidence finalizer failure returns controlled fallback`` mode =
    task {
        let source =
            { kind = Json
              location = $"memory://fake-websocket-finalizer-failure-{mode}"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let logs = ResizeArray<string>()

        let finalizerFailureEvents () =
            match mode with
            | "empty" -> [ responsesCompletedEmptyEvent "resp_finalizer_empty" ]
            | "error" -> [ responsesErrorEvent "finalizer_failed" "Finalizer failed." ]
            | "token_limit" -> [ responsesTokenLimitEvent "resp_finalizer_token_limit" "partial finalizer text" ]
            | "tool_call" ->
                [ responsesFunctionCallEvent
                      "resp_finalizer_tool_call"
                      "call_inventory_finalizer"
                      "source_inventory"
                      "{}" ]
            | _ -> failwith $"Unexpected finalizer failure mode: {mode}"

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                match captured.Count with
                | 1 ->
                    [ responsesFunctionCallEventWithText
                          "resp_finalizer_failure_call"
                          "I should keep searching before I answer."
                          "call_inventory"
                          "source_inventory"
                          "{}" ]
                | _ -> finalizerFailureEvents ())

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerToolCallLoopLimit = 1
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Answer from the selected source."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal(QaAnswerModel.reliableAnswerFallback, answer.answer)
        Assert.DoesNotContain("I should keep searching", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(None, captured[1].previous_response_id)
        Assert.Equal(Some FsResponses.ToolChoice.None, captured[1].tool_choice)
        Assert.Empty(responseToolNames captured[1])
        Assert.Contains(logs, fun log -> log.Contains("did not produce a clean answer"))
    }

[<Fact>]
let ``qa session websocket logs OpenAI compaction output items`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-heavy-chain"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let logs = ResizeArray<string>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                [ responsesCompletedEventWithCompaction
                      "resp_heavy_answer"
                      "short socket answer"
                      "cmp_123"
                      "encrypted-compaction" ])

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("short socket answer", answer.answer)

        let answerRequest = Assert.Single(captured)
        Assert.Equal(Some true, answerRequest.generate)
        Assert.True([ 200000 ] = responseContextManagementThresholds answerRequest)

        Assert.Contains(
            logs,
            fun log ->
                log.Contains("OpenAI server-side answer compaction applied", StringComparison.OrdinalIgnoreCase)
                && log.Contains("cmp_123", StringComparison.OrdinalIgnoreCase)
        )
    }

[<Fact>]
let ``qa session websocket hides transient empty response diagnostics when retry succeeds`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake-websocket-empty"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable callNumber = 0

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                callNumber <- callNumber + 1

                match callNumber with
                | 1 -> [ responsesCompletedEmptyEvent "resp_empty" ]
                | _ -> [ responsesCompletedEvent "resp_retry" "retry socket answer" ])

        let logs = ResizeArray<string>()

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("retry socket answer", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(Some true, captured[0].generate)
        Assert.Equal(Some true, captured[1].generate)
        Assert.Equal(Some 5000, captured[0].max_output_tokens)
        Assert.Equal(Some 10000, captured[1].max_output_tokens)

        Assert.False(logs |> Seq.exists (fun log -> log.Contains("returned empty text")))
        Assert.False(logs |> Seq.exists (fun log -> log.Contains("retry succeeded")))
    }

[<Fact>]
let ``qa session retries empty answer response with larger output budget`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable callNumber = 0

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                callNumber <- callNumber + 1

                match callNumber with
                | 1 -> [ responsesCompletedEmptyEvent "resp_retry_empty" ]
                | _ -> [ responsesCompletedEvent "resp_retry_answer" "retry answer" ])

        let logs = ResizeArray<string>()

        let answerConfig =
            { ModelRoleConfig.create "gpt-5.5" with
                maxOutputTokens = Some 50 }

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("retry answer", answer.answer)
        Assert.Equal(2, captured.Count)
        Assert.Equal(Some 50, captured[0].max_output_tokens)
        Assert.True(captured[1].max_output_tokens.Value >= 1200)
        Assert.False(logs |> Seq.exists (fun log -> log.Contains("returned empty text")))
    }

[<Fact>]
let ``qa session reports token limit when answer response is length finished`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()
        let mutable callNumber = 0

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request
                callNumber <- callNumber + 1

                match callNumber with
                | _ -> [ responsesTokenLimitEvent "resp_token_limit" "partial answer" ])

        let logs = ResizeArray<string>()

        let answerConfig =
            { ModelRoleConfig.create "gpt-5.5" with
                maxOutputTokens = Some 321 }

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options, transport)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Give me a long answer."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Contains("max answer token limit of 321", answer.answer)
        Assert.Contains("Disconnect, open Settings, increase Max Answer Tokens", answer.answer)
        Assert.Equal(Some 321, captured[0].max_output_tokens)
        Assert.Contains(logs, fun log -> log.Contains("hit output token limit"))
    }

[<Fact>]
let ``getSynonyms parses comma-separated keywords correctly`` () =
    async {
        let client = new MockChatClient()
        let mutable reportedMsg = ""
        let report msg = reportedMsg <- msg
        let! expansion = KnowledgeSources.getSynonyms client (Some report) "query"

        match expansion with
        | Some expansion ->
            Assert.Equal<int>(2, expansion.terms.Length)
            Assert.Contains("synonym1", expansion.terms)
            Assert.Contains("technicalTerm1", expansion.terms)
            Assert.Equal<KnowledgeSources.QueryType>(KnowledgeSources.QueryType.Question, expansion.queryType)
            Assert.Contains("Retrieval query", reportedMsg)
        | None -> failwith "Expected query expansion."
    }
    |> Async.RunSynchronously

[<Fact>]
let ``getSynonyms handles empty query`` () =
    async {
        let client = new MockChatClient()
        let! expansion = KnowledgeSources.getSynonyms client None ""
        Assert.True(Option.isNone expansion)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``query post-processing keeps domain expansion in supplied plug-in profile`` () =
    let profile =
        { QaPlugInProfile.generic with
            id = "test-domain"
            displayName = "Test Domain"
            voiceReplacements =
                [ { pattern = @"\bmedicare\s+gap\b"
                    replacement = "Medigap" } ]
            queryExpansionRules =
                [ { triggers = [ "medigap" ]
                    terms = [ "medicare"; "supplement"; "plan" ] } ] }

    let generic = QueryPostProcessing.forVoiceLikeRetrieval "medicare gap"

    let profiled =
        QueryPostProcessing.forVoiceLikeRetrievalWithProfile profile "medicare gap"

    Assert.DoesNotContain("supplement", generic.searchTerms)
    Assert.Contains("supplement", profiled.searchTerms)
    Assert.Contains("Medigap", profiled.normalizedQuery)

[<Fact>]
let ``json knowledge source can be loaded as generic QA content`` () =
    async {
        let path =
            Path.Combine(Path.GetTempPath(), $"fsvoice-json-source-{Guid.NewGuid():N}.json")

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                {| id = "test-source"
                   title = "Test Source"
                   documents =
                    [ {| id = "1"
                         title = "Answer label: 1"
                         text = "Use the scheduling guide for emergency dental appointments."
                         keywords = [ "appointment lookup" ] |} ] |}
            )
        )

        let source =
            { kind = Json
              location = path
              enabled = true }

        let! retrieval, errors = KnowledgeSources.loadInternalIndex [ source ]

        Assert.Empty errors
        let chunk = Assert.Single retrieval.chunks
        Assert.Contains("Answer label: 1", chunk.text)
        Assert.Contains("emergency dental appointments", chunk.text)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling json knowledge source loads passages with keywords`` () =
    async {
        let path =
            Path.Combine(Path.GetTempPath(), $"fsvoice-docling-source-{Guid.NewGuid():N}.json")

        let page: FsColbert.DoclingPageItem =
            { pageNo = 1
              size = { width = 100.0; height = 100.0 } }

        let item: FsColbert.DoclingTextItem =
            { selfRef = "#/texts/0"
              parent = "#/body"
              label = FsColbert.DoclingLabels.ofJsonValue "text"
              text = "Orthodontia benefits require a waiting period."
              orig = "Orthodontia benefits require a waiting period."
              contentLayer = FsColbert.DoclingContentLayer.Body
              prov = []
              keywords = [ "dental braces"; "orthodontia" ]
              sourceId = Some "benefits"
              sourceDisplayName = Some "Benefits" }

        let document: FsColbert.DoclingDocument =
            { name = "benefits"
              originFileName = Some "benefits.pdf"
              originMimeType = Some "application/pdf"
              pages = [ 1, page ] |> Map.ofList
              texts = [ item ]
              tables = []
              pictures = []
              bodyChildren = [ "#/texts/0" ]
              furnitureChildren = [] }

        File.WriteAllText(path, FsColbert.DoclingJson.serialize document)

        let source =
            { kind = Json
              location = path
              enabled = true }

        let! passages = KnowledgeSources.loadPassages source

        match passages with
        | Error err -> failwith err
        | Ok passages ->
            let passage = Assert.Single passages
            Assert.Contains("Orthodontia benefits", passage.text)
            Assert.Equal<string list>([ "dental braces"; "orthodontia" ], passage.keywords)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid page input builder skips ocr when native pdf text is sufficient`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 200.0; height = 200.0 }
                      cells =
                        [ doclingNativeCell "Native" 10.0 145.0 60.0 160.0
                          doclingNativeCell "PDF" 65.0 145.0 95.0 160.0
                          doclingNativeCell "text" 100.0 145.0 135.0 160.0 ] }

                return Ok [ nativePage ]
            }

        let ocr = CountingDoclingOcr [ doclingOcrCell "OCR fallback" 20.0 80.0 120.0 100.0 ]

        let! result =
            DoclingHybrid.buildPageInputs
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                ignore
                "/tmp/native.pdf"
                rasterizer
                nativeProvider
                (Some(ocr :> FsColbert.IDoclingOcrProvider))

        match result with
        | Error err -> failwith err
        | Ok inputs ->
            let input = Assert.Single inputs
            Assert.Equal(0, ocr.Calls)
            Assert.Equal<string list>([ "Native"; "PDF"; "text" ], input.ocrCells |> List.map _.text)
            Assert.Equal(FsColbert.DoclingCoordinateOrigin.BottomLeft, input.ocrCells.Head.bbox.coordOrigin)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid built-in layout providers resolve pp and heron`` () =
    let pp = DoclingHybrid.tryBuiltInLayoutProvider "pp-doclayout-m"
    let heron = DoclingHybrid.tryBuiltInLayoutProvider "heron"

    Assert.True(pp.IsSome)
    Assert.Equal("pp-doclayout-m", pp.Value.Id)
    Assert.True(heron.IsSome)
    Assert.Equal("heron", heron.Value.Id)

[<Fact>]
let ``pdf parser index fingerprint includes layout quality revision`` () =
    let hybrid =
        KnowledgeSources.PdfParsingModes.indexFingerprint KnowledgeSources.PdfParsingMode.Hybrid

    let withoutLayout =
        KnowledgeSources.PdfParsingModes.indexFingerprint KnowledgeSources.PdfParsingMode.HybridWithoutLayout

    Assert.Contains("pdfParsingMode=hybrid", hybrid)
    Assert.Contains("pdfParserQuality=layout-sparse-native-headings-v2", hybrid)
    Assert.False(String.Equals(hybrid, withoutLayout, StringComparison.Ordinal))

[<Fact>]
let ``pdf source parsing fingerprint includes docling rasterizer and layout model`` () =
    let storageRoot = tempStorageRoot ()
    let path = Path.Combine(storageRoot, "fingerprint.pdf")

    let source =
        { kind = Pdf
          location = path
          enabled = true }

    let hybridOptions =
        KnowledgeSources.PdfIngestionOptions.create KnowledgeSources.PdfParsingMode.Hybrid

    try
        DoclingHybrid.clearRasterizerFactory ()

        let withoutRasterizer =
            KnowledgeSources.sourceParsingFingerprint hybridOptions source

        Assert.Contains("pdfLayoutAnalysis=true", withoutRasterizer)
        Assert.Contains("pdfLayoutModel=pp-doclayout-m", withoutRasterizer)
        Assert.Contains("pdfRasterizer=none", withoutRasterizer)

        DoclingHybrid.setRasterizerFactoryWithInfo "test-rasterizer-v1" "Test rasterizer" (fun _ ->
            FakeDoclingRasterizer(Error "unused") :> FsColbert.IDoclingPageRasterizer)

        let withRasterizer = KnowledgeSources.sourceParsingFingerprint hybridOptions source
        Assert.Contains("pdfRasterizer=test-rasterizer-v1", withRasterizer)
        Assert.False(String.Equals(withoutRasterizer, withRasterizer, StringComparison.Ordinal))

        let withoutLayout =
            KnowledgeSources.sourceParsingFingerprint
                (KnowledgeSources.PdfIngestionOptions.create KnowledgeSources.PdfParsingMode.HybridWithoutLayout)
                source

        Assert.Contains("pdfLayoutAnalysis=false", withoutLayout)
        Assert.Contains("pdfRasterizer=test-rasterizer-v1", withoutLayout)
    finally
        DoclingHybrid.clearRasterizerFactory ()

[<Fact>]
let ``docling hybrid layout disabled bypasses supplied layout predictor`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 200.0; height = 200.0 }
                      cells = [ doclingNativeCell "Native layout bypass text" 10.0 145.0 180.0 160.0 ] }

                return Ok [ nativePage ]
            }

        let source =
            FsColbert.PassageSource.create "/tmp/no-layout.pdf" "No Layout" "/tmp/no-layout.pdf"

        let! result =
            DoclingHybrid.readPdfPassagesWithProvidersAndCancellation
                { DoclingHybrid.defaults with
                    enableLayoutAnalysis = false
                    minNativeCharsPerPage = 8 }
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                source
                "/tmp/no-layout.pdf"
                rasterizer
                nativeProvider
                None
                (FailingDoclingLayout() :> FsColbert.IDoclingLayoutPredictor)
                None
                CancellationToken.None

        match result with
        | Error err -> failwith err
        | Ok passages -> Assert.NotEmpty passages
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid provider path emits native text table picture metadata and keywords`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 400.0 }
                      cells =
                        [ doclingNativeCell "Native paragraph" 20.0 285.0 180.0 310.0
                          doclingNativeCell "Table row" 30.0 155.0 120.0 175.0 ] }

                return Ok [ nativePage ]
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters =
                      [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 80.0 220.0 130.0
                        doclingCluster 1 FsColbert.DoclingLabel.Table 20.0 210.0 160.0 260.0
                        doclingCluster 2 FsColbert.DoclingLabel.Picture 250.0 190.0 360.0 300.0 ] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let classifier =
            let figureClass: FsColbert.DoclingFigureClass =
                { className = "diagram"
                  confidence = 0.91f }

            FakeDoclingFigureClassifier [ figureClass ] :> FsColbert.IDoclingFigureClassifier

        let passageSource =
            FsColbert.PassageSource.create "/tmp/doc.pdf" "Doc" "/tmp/doc.pdf"

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/doc.pdf"
                rasterizer
                nativeProvider
                None
                layout
                (Some classifier)

        match result with
        | Error err -> failwith err
        | Ok passages ->
            let passage = Assert.Single passages
            Assert.Contains("Native paragraph", passage.text)
            Assert.Contains("Table row", passage.text)
            Assert.Contains("[Picture: diagram", passage.text)
            Assert.Contains("table", passage.keywords)
            Assert.Contains("picture", passage.keywords)
            Assert.Contains("diagram", passage.keywords)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual descriptions disabled preserves hybrid passage output without model calls`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-disabled.pdf")

        let response =
            """{"description":"Revenue chart rises across quarters.","keywords":["revenue trend","quarterly sales"]}"""

        let client = new CountingChatClient(response)

        let! baseline =
            readHybridVisualPassages
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                ignore
                path

        let! disabled =
            readHybridVisualPassages
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8
                    visualDescriptions =
                        { PdfVisualDescriptionOptions.defaults with
                            enabled = false
                            client = Some(client :> IChatClient)
                            cacheStorageRoot = Some storageRoot } }
                ignore
                path

        match baseline, disabled with
        | Ok baseline, Ok disabled ->
            Assert.Equal(0, client.Count)
            Assert.Equal<string list>(baseline |> List.map _.text, disabled |> List.map _.text)

            Assert.DoesNotContain(
                disabled,
                fun passage -> passage.text.Contains("[Visual description:", StringComparison.Ordinal)
            )
        | Error err, _
        | _, Error err -> failwith err
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual descriptions enabled adds synthetic visual text and keywords`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-enabled.pdf")

        let response =
            """{"description":"Revenue chart rises from Q1 to Q4 with the strongest increase near Q4.","keywords":["revenue trend","q4","quarterly sales"]}"""

        let client = new CountingChatClient(response)

        let options =
            { DoclingHybrid.defaults with
                minNativeCharsPerPage = 8
                visualDescriptions = visualDescriptionOptions storageRoot (Some(client :> IChatClient)) }

        let reports = ResizeArray<string>()
        let! result = readHybridVisualPassages options reports.Add path

        match result with
        | Error err -> failwith err
        | Ok passages ->
            let passage = Assert.Single passages
            Assert.Equal(1, client.Count)
            Assert.Contains("[Visual description: picture] Revenue chart rises from Q1 to Q4", passage.text)
            Assert.Contains("revenue trend", passage.keywords)
            Assert.Contains("quarterly sales", passage.keywords)
            Assert.Contains(reports, fun message -> message.Contains("added 1 visual description"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual descriptions survive native-text fallback`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-fallback.pdf")

        let response =
            """{"description":"Block diagram shows sensor inputs flowing into an anomaly detector.","keywords":["block diagram","sensor inputs","anomaly detector"]}"""

        let client = new CountingChatClient(response)
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativeText =
                    String.replicate
                        80
                        "Native text from the paper explains wearable sensor streams, contextual anomaly detection, and clinician-facing monitoring. "

                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 400.0 }
                      cells = [ doclingNativeCell nativeText 20.0 285.0 360.0 310.0 ] }

                return Ok [ nativePage ]
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters = [ doclingCluster 1 FsColbert.DoclingLabel.Picture 250.0 190.0 360.0 300.0 ] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource = FsColbert.PassageSource.create path "Visual Fallback Doc" path

        let options =
            { DoclingHybrid.defaults with
                minNativeCharsPerPage = 8
                visualDescriptions = visualDescriptionOptions storageRoot (Some(client :> IChatClient)) }

        let reports = ResizeArray<string>()

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                options
                reports.Add
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                path
                rasterizer
                nativeProvider
                None
                layout
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.Equal(1, client.Count)
            Assert.Contains(reports, fun message -> message.Contains("Using document structure native-text conversion"))
            Assert.Contains(passages, fun passage -> passage.text.Contains("Native text from the paper"))

            Assert.Contains(
                passages,
                fun passage ->
                    passage.text.Contains(
                        "[Visual description: picture] Block diagram shows sensor inputs",
                        StringComparison.Ordinal
                    )
            )
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual descriptions enabled without client logs and skips`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-no-client.pdf")

        let options =
            { DoclingHybrid.defaults with
                minNativeCharsPerPage = 8
                visualDescriptions = visualDescriptionOptions storageRoot None }

        let reports = ResizeArray<string>()
        let! result = readHybridVisualPassages options reports.Add path

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.NotEmpty passages

            Assert.DoesNotContain(
                passages,
                fun passage -> passage.text.Contains("[Visual description:", StringComparison.Ordinal)
            )

            Assert.Contains(
                reports,
                fun message -> message.Contains("no visual-description model client is configured")
            )
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual description model failure logs and keeps indexing`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-failure.pdf")

        let options =
            { DoclingHybrid.defaults with
                minNativeCharsPerPage = 8
                visualDescriptions =
                    visualDescriptionOptions storageRoot (Some(new InvalidJsonSchemaChatClient() :> IChatClient)) }

        let reports = ResizeArray<string>()
        let! result = readHybridVisualPassages options reports.Add path

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.NotEmpty passages

            Assert.DoesNotContain(
                passages,
                fun passage -> passage.text.Contains("[Visual description:", StringComparison.Ordinal)
            )

            Assert.Contains(reports, fun message -> message.Contains("PDF visual description failed"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual description cache hit avoids second model call`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-cache.pdf")

        let response =
            """{"description":"A labeled process diagram connects intake to approval.","keywords":["process diagram","approval"]}"""

        let firstClient = new CountingChatClient(response)
        let secondClient = new CountingChatClient(response)

        let firstVisualOptions =
            visualDescriptionOptions storageRoot (Some(firstClient :> IChatClient))

        let firstOptions =
            { DoclingHybrid.defaults with
                minNativeCharsPerPage = 8
                visualDescriptions = firstVisualOptions }

        let secondOptions =
            { firstOptions with
                visualDescriptions =
                    { firstVisualOptions with
                        client = Some(secondClient :> IChatClient) } }

        let! first = readHybridVisualPassages firstOptions ignore path
        let! second = readHybridVisualPassages secondOptions ignore path

        match first, second with
        | Ok first, Ok second ->
            Assert.Equal(1, firstClient.Count)
            Assert.Equal(0, secondClient.Count)
            Assert.Contains("[Visual description: picture] A labeled process diagram", (Assert.Single first).text)
            Assert.Contains("[Visual description: picture] A labeled process diagram", (Assert.Single second).text)
        | Error err, _
        | _, Error err -> failwith err
    }
    |> Async.RunSynchronously

[<Fact>]
let ``pdf visual description fingerprint changes with enablement model and schema`` () =
    let storageRoot = tempStorageRoot ()
    let path = Path.Combine(storageRoot, "visual-fingerprint.pdf")

    let source =
        { kind = Pdf
          location = path
          enabled = true }

    let disabled =
        { KnowledgeSources.PdfIngestionOptions.defaults with
            visualDescriptions = PdfVisualDescriptionOptions.disabled }

    let enabled =
        { disabled with
            visualDescriptions =
                { PdfVisualDescriptionOptions.defaults with
                    enabled = true
                    modelId = "visual-model-a"
                    schemaVersion = "visual-schema-a" } }

    let modelChanged =
        { enabled with
            visualDescriptions =
                { enabled.visualDescriptions with
                    modelId = "visual-model-b" } }

    let schemaChanged =
        { enabled with
            visualDescriptions =
                { enabled.visualDescriptions with
                    schemaVersion = "visual-schema-b" } }

    let fingerprint options =
        KnowledgeSources.sourceParsingFingerprint options source

    Assert.False(String.Equals(fingerprint disabled, fingerprint enabled, StringComparison.Ordinal))
    Assert.False(String.Equals(fingerprint enabled, fingerprint modelChanged, StringComparison.Ordinal))
    Assert.False(String.Equals(fingerprint enabled, fingerprint schemaChanged, StringComparison.Ordinal))

[<Fact>]
let ``hybrid pdf without layout does not attempt visual descriptions`` () =
    async {
        let storageRoot = tempStorageRoot ()
        let path = Path.Combine(storageRoot, "visual-without-layout.pdf")
        let response = """{"description":"Should not be called.","keywords":["unused"]}"""
        let client = new CountingChatClient(response)
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 400.0 }
                      cells = [ doclingNativeCell "Native only hybrid without layout text" 20.0 285.0 260.0 310.0 ] }

                return Ok [ nativePage ]
            }

        let passageSource = FsColbert.PassageSource.create path "No Layout Visual Doc" path

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    enableLayoutAnalysis = false
                    minNativeCharsPerPage = 8
                    visualDescriptions = visualDescriptionOptions storageRoot (Some(client :> IChatClient)) }
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                path
                rasterizer
                nativeProvider
                None
                (FailingDoclingLayout() :> FsColbert.IDoclingLayoutPredictor)
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.Equal(0, client.Count)
            Assert.Contains("Native only hybrid without layout text", (Assert.Single passages).text)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid provider path falls back to native text when layout collapses`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy

        let rasterizer =
            FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image }; { pageNo = 2; image = image } ])

        let repeatedText prefix =
            [ 1..170 ] |> List.map (fun index -> $"{prefix}{index}") |> String.concat " "

        let nativeProvider _ =
            async {
                let pages: FsColbert.DoclingNativePageText list =
                    [ { pageNo = 1
                        size = { width = 400.0; height = 400.0 }
                        cells = [ doclingNativeCell (repeatedText "alpha") 20.0 350.0 380.0 380.0 ] }
                      { pageNo = 2
                        size = { width = 400.0; height = 400.0 }
                        cells = [ doclingNativeCell (repeatedText "beta") 20.0 350.0 380.0 380.0 ] } ]

                return Ok pages
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters = [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 15.0 390.0 70.0 ] }
                  { pageNo = 2; clusters = [] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource =
            FsColbert.PassageSource.create "/tmp/collapsed.pdf" "Collapsed" "/tmp/collapsed.pdf"

        let reports = ResizeArray<string>()

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                reports.Add
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/collapsed.pdf"
                rasterizer
                nativeProvider
                None
                layout
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.True(passages.Length > 1)
            Assert.Contains(passages, fun passage -> passage.text.Contains("beta"))
            Assert.Contains(reports, fun message -> message.Contains("layout conversion produced only 1 passage"))
            Assert.Contains(reports, fun message -> message.Contains("Using document structure native-text conversion"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling native text fallback preserves line heading section paths`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let repeatedText prefix =
            [ 1..120 ] |> List.map (fun index -> $"{prefix}{index}") |> String.concat " "

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 400.0 }
                      cells =
                        [ doclingNativeCell "AI on the Pulse: Real-Time Health Anomaly Detection" 20.0 360.0 380.0 380.0
                          doclingNativeCell "1 INTRODUCTION" 20.0 330.0 180.0 345.0
                          doclingNativeCell "opposite column text should not join heading" 260.0 330.0 380.0 345.0
                          doclingNativeCell (repeatedText "patient") 20.0 300.0 380.0 315.0
                          doclingNativeCell "4.3 Experimental Setup" 20.0 260.0 220.0 275.0
                          doclingNativeCell (repeatedText "units") 20.0 230.0 380.0 245.0 ] }

                return Ok [ nativePage ]
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters = [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 15.0 40.0 30.0 ] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource =
            FsColbert.PassageSource.create "/tmp/native-sections.pdf" "Native Sections" "/tmp/native-sections.pdf"

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/native-sections.pdf"
                rasterizer
                nativeProvider
                None
                layout
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            let introductionPath =
                [ "AI on the Pulse: Real-Time Health Anomaly Detection"; "1 INTRODUCTION" ]

            let setupPath =
                [ "AI on the Pulse: Real-Time Health Anomaly Detection"
                  "4.3 Experimental Setup" ]

            Assert.Contains(passages, fun passage -> passage.sectionPath = introductionPath)
            Assert.Contains(passages, fun passage -> passage.sectionPath = setupPath)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling native text fallback ignores table and plot rows as section headings`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 500 255uy 255uy 255uy
        let rasterizer = FakeDoclingRasterizer(Ok [ { pageNo = 1; image = image } ])

        let repeatedText prefix =
            [ 1..120 ] |> List.map (fun index -> $"{prefix}{index}") |> String.concat " "

        let title = "A-Mem: Agentic Memory for LLM Agents"

        let nativeProvider _ =
            async {
                let nativePage: FsColbert.DoclingNativePageText =
                    { pageNo = 1
                      size = { width = 400.0; height = 500.0 }
                      cells =
                        [ doclingNativeCell title 20.0 460.0 380.0 480.0
                          doclingNativeCell "4 Experiment" 20.0 430.0 150.0 445.0
                          doclingNativeCell (repeatedText "experiment") 20.0 400.0 380.0 415.0
                          doclingNativeCell "Method" 20.0 360.0 100.0 375.0
                          doclingNativeCell "3.2 A-MEM" 20.0 330.0 120.0 345.0
                          doclingNativeCell "30 A-mem" 20.0 300.0 120.0 315.0
                          doclingNativeCell "4.3 Empirical Results" 20.0 260.0 220.0 275.0
                          doclingNativeCell (repeatedText "result") 20.0 230.0 380.0 245.0 ] }

                return Ok [ nativePage ]
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters = [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 15.0 40.0 30.0 ] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource =
            FsColbert.PassageSource.create "/tmp/native-table-rows.pdf" "Native Table Rows" "/tmp/native-table-rows.pdf"

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/native-table-rows.pdf"
                rasterizer
                nativeProvider
                None
                layout
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.Contains(passages, fun passage -> passage.sectionPath = [ title; "4 Experiment" ])

            Assert.Contains(
                passages,
                fun passage -> passage.sectionPath = [ title; "4 Experiment"; "4.3 Empirical Results" ]
            )

            Assert.False(
                passages
                |> List.exists (fun passage ->
                    passage.sectionPath
                    |> List.exists (fun section -> section = "Method" || section = "3.2 A-MEM" || section = "30 A-mem")),
                "Table and plot labels should remain body text, not section path entries."
            )
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid provider path falls back to native text when layout is sparse`` () =
    async {
        let image = FsColbert.DoclingRgbImage.solid 400 400 255uy 255uy 255uy

        let rasterizer =
            FakeDoclingRasterizer(
                Ok
                    [ { pageNo = 1; image = image }
                      { pageNo = 2; image = image }
                      { pageNo = 3; image = image }
                      { pageNo = 4; image = image } ]
            )

        let repeatedText prefix =
            [ 1..220 ] |> List.map (fun index -> $"{prefix}{index}") |> String.concat " "

        let nativeProvider _ =
            async {
                let pageText pageNo prefix : FsColbert.DoclingNativePageText =
                    { pageNo = pageNo
                      size = { width = 400.0; height = 400.0 }
                      cells = [ doclingNativeCell (repeatedText prefix) 20.0 350.0 380.0 380.0 ] }

                return
                    [ pageText 1 "alpha"
                      pageText 2 "beta"
                      pageText 3 "gamma"
                      pageText 4 "delta" ]
                    |> Ok
            }

        let layout =
            FakeDoclingLayout
                [ { pageNo = 1
                    clusters = [ doclingCluster 0 FsColbert.DoclingLabel.Text 15.0 15.0 390.0 70.0 ] }
                  { pageNo = 2
                    clusters = [ doclingCluster 1 FsColbert.DoclingLabel.Text 15.0 15.0 390.0 70.0 ] }
                  { pageNo = 3; clusters = [] }
                  { pageNo = 4; clusters = [] } ]
            :> FsColbert.IDoclingLayoutPredictor

        let passageSource =
            FsColbert.PassageSource.create "/tmp/sparse.pdf" "Sparse" "/tmp/sparse.pdf"

        let reports = ResizeArray<string>()

        let! result =
            DoclingHybrid.readPdfPassagesWithProviders
                { DoclingHybrid.defaults with
                    minNativeCharsPerPage = 8 }
                reports.Add
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/sparse.pdf"
                rasterizer
                nativeProvider
                None
                layout
                None

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.True(passages.Length > 2)
            Assert.Contains(passages, fun passage -> passage.text.Contains("gamma"))
            Assert.Contains(passages, fun passage -> passage.text.Contains("delta"))
            Assert.Contains(reports, fun message -> message.Contains("layout conversion looked incomplete"))
            Assert.Contains(reports, fun message -> message.Contains("Using document structure native-text conversion"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid cancellation does not fall back to legacy parser`` () =
    let storageRoot =
        Path.Combine(Path.GetTempPath(), $"fsvoice-docling-cancel-{Guid.NewGuid():N}")

    Directory.CreateDirectory storageRoot |> ignore

    try
        let passageSource =
            FsColbert.PassageSource.create "/tmp/cancel.pdf" "Cancel" "/tmp/cancel.pdf"

        let mutable legacyCalled = false

        use cts = new CancellationTokenSource()
        cts.Cancel()

        Assert.Throws<OperationCanceledException>(fun () ->
            DoclingHybrid.readPdfPassagesWithFallbackAndCancellation
                storageRoot
                ignore
                FsColbert.ChunkOptions.fsKameDefaults
                passageSource
                "/tmp/cancel.pdf"
                (fun () ->
                    async {
                        legacyCalled <- true
                        return Ok []
                    })
                cts.Token
            |> Async.RunSynchronously
            |> ignore)
        |> ignore

        Assert.False legacyCalled
    finally
        if Directory.Exists storageRoot then
            Directory.Delete(storageRoot, true)

[<Fact>]
let ``indexing cancellation is rethrown instead of wrapped as an error`` () =
    let storageRoot =
        Path.Combine(Path.GetTempPath(), $"fsvoice-index-cancel-{Guid.NewGuid():N}")

    Directory.CreateDirectory storageRoot |> ignore

    try
        let source: KnowledgeSource =
            { kind = Pdf
              location = "/tmp/cancel.pdf"
              enabled = true }

        let passage: FsColbert.PassageRef =
            { sourceId = source.location
              sourceDisplayName = source.DisplayName
              sourceLocation = source.location
              index = 0
              sectionPath = []
              text = "cancel me"
              keywords = [] }

        use cts = new CancellationTokenSource()
        cts.Cancel()

        Assert.Throws<OperationCanceledException>(fun () ->
            KnowledgeSources.InindexPassagesWithCancellation
                storageRoot
                ignore
                KnowledgeSources.KeywordGenerationOptions.disabled
                KnowledgeSources.PdfParsingMode.Legacy
                source
                [ passage ]
                cts.Token
            |> Async.RunSynchronously
            |> ignore)
        |> ignore
    finally
        if Directory.Exists storageRoot then
            Directory.Delete(storageRoot, true)

[<Fact>]
let ``docling hybrid fallback uses legacy pdf reader when provider path fails`` () =
    async {
        let source =
            FsColbert.PassageSource.create "fallback" "Fallback" "/tmp/fallback.pdf"

        let reports = ResizeArray<string>()

        let legacyReader () =
            async {
                let passage: FsColbert.PassageRef =
                    { sourceId = source.id
                      sourceDisplayName = source.displayName
                      sourceLocation = source.location
                      index = 0
                      sectionPath = []
                      text = "Legacy PdfPig text"
                      keywords = [] }

                return Ok [ passage ]
            }

        let! result =
            DoclingHybrid.fallbackToLegacy
                reports.Add
                source.location
                (async { return Error "layout model missing" })
                legacyReader

        match result with
        | Error err -> failwith err
        | Ok passages ->
            let passage = Assert.Single passages
            Assert.Equal("Legacy PdfPig text", passage.text)
            Assert.Contains(reports, fun message -> message.Contains("Falling back to PdfPig text extraction"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``docling hybrid fallback uses legacy pdf reader when hybrid returns one passage`` () =
    async {
        let source =
            FsColbert.PassageSource.create "fallback" "Fallback" "/tmp/fallback.pdf"

        let reports = ResizeArray<string>()

        let passage index text : FsColbert.PassageRef =
            { sourceId = source.id
              sourceDisplayName = source.displayName
              sourceLocation = source.location
              index = index
              sectionPath = []
              text = text
              keywords = [] }

        let legacyReader () =
            async { return Ok [ passage 0 "Legacy PdfPig text 1"; passage 1 "Legacy PdfPig text 2" ] }

        let! result =
            DoclingHybrid.fallbackToLegacy
                reports.Add
                source.location
                (async { return Ok [ passage 0 "Docling single blob" ] })
                legacyReader

        match result with
        | Error err -> failwith err
        | Ok passages ->
            Assert.Equal(2, passages.Length)
            Assert.Equal("Legacy PdfPig text 1", passages.Head.text)
            Assert.Contains(reports, fun message -> message.Contains("produced only 1 passage"))
            Assert.Contains(reports, fun message -> message.Contains("Using PdfPig fallback"))
    }
    |> Async.RunSynchronously

[<Fact>]
let ``legacy prebuilt manifest still loads persisted index keywords`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "legacy.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Legacy markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "legacy.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Policy waiting period text." [ "orthodontia" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    let manifest =
        [| {| id = "legacy"
              kind = "markdown"
              displayName = "Legacy"
              storedPath = sourcePath
              indexPath = indexPath |} |]

    File.WriteAllText(Path.Combine(folder, "prebuilt-indexes.installed.json"), JsonSerializer.Serialize manifest)

    match KnowledgeSources.tryLoadPrebuiltIndex storageRoot source with
    | Error err -> failwith err
    | Ok None -> failwith "Expected prebuilt index."
    | Ok(Some index) ->
        Assert.Equal(1, index.passages.Length)
        Assert.Equal<string list>([ "orthodontia" ], index.passages.Head.reference.keywords)

[<Fact>]
let ``bundle manifest loads prebuilt index and keeps keyword tfidf ranking`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "docling.json")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "{}")

    let source =
        { kind = Json
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "docling.fsci")

    fakeColbertIndex
        [ fakeIndexedPassage source 0 "Benefits waiting period details." [ "orthodontia"; "dental braces" ]
          fakeIndexedPassage source 1 "Kitchen recipe notes." [] ]
    |> FsColbert.IndexPersistence.save indexPath

    let bundleSource: FsColbert.IndexBundleSource =
        { sourceId = sourcePath
          sourceDisplayName = "Docling"
          sourceLocation = Some sourcePath
          sourceKind = Some "docling-json"
          indexFile = "docling.fsci" }

    FsColbert.IndexBundle.create
        "test-bundle"
        "1.0.0"
        FsColbert.ModelCatalog.mxbaiEdgeColbertInt8.id
        FsColbert.ChunkOptions.fsKameDefaults
        FsColbert.TfidfOptions.defaults
        [ bundleSource ]
    |> FsColbert.IndexBundle.writeManifest (Path.Combine(folder, "index-bundle.json"))

    match KnowledgeSources.tryLoadPrebuiltIndex storageRoot source with
    | Error err -> failwith err
    | Ok None -> failwith "Expected bundled index."
    | Ok(Some index) ->
        let keywordCount =
            index.passages |> List.sumBy (fun passage -> passage.reference.keywords.Length)

        let candidates =
            FsColbert.Tfidf.scoreQuery index.tfidf "orthodontia"
            |> FsColbert.Tfidf.topCandidates 10

        Assert.True(keywordCount > 0)
        Assert.Equal(0, candidates[0] |> fst)

[<Fact>]
let ``bundle manifest mismatch reports compatibility reason`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "docling.json")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "{}")

    let source =
        { kind = Json
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "docling.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Benefits waiting period details." [ "orthodontia" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    FsColbert.IndexBundle.create
        "test-bundle"
        "1.0.0"
        "wrong/model"
        FsColbert.ChunkOptions.fsKameDefaults
        FsColbert.TfidfOptions.defaults
        [ { sourceId = sourcePath
            sourceDisplayName = "Docling"
            sourceLocation = Some sourcePath
            sourceKind = Some "docling-json"
            indexFile = "docling.fsci" } ]
    |> FsColbert.IndexBundle.writeManifest (Path.Combine(folder, "index-bundle.json"))

    match KnowledgeSources.tryLoadPrebuiltIndex storageRoot source with
    | Ok _ -> failwith "Expected incompatible bundle."
    | Error err -> Assert.Contains("model_id", err)

[<Fact>]
let ``index preview loads persisted index keywords terms and vector summary`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let indexPath = Path.Combine(persistedIndexFolder storageRoot, "preview.fsci")

    { (fakeIndexedPassage source 0 "Policy waiting period text." [ "orthodontia" ]) with
        terms = Set.ofList [ "period"; "waiting" ] }
    |> List.singleton
    |> fakeColbertIndex
    |> FsColbert.IndexPersistence.save indexPath

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Error err -> failwith err
    | Ok preview ->
        let record = preview.records.Head

        Assert.Equal(1, preview.totalChunks)
        Assert.Equal(1, preview.sampledCount)
        Assert.Equal<string list>([ "orthodontia" ], record.keywords)
        Assert.Equal<string list>([ "period"; "waiting" ], record.terms)
        Assert.Equal(1, record.vector.tokenCount)
        Assert.True(record.vector.embeddingDim > 0)
        Assert.Equal(1.0f, record.vector.valueSample.Head)

[<Fact>]
let ``index preview loads bundled prebuilt index`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview-bundle.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview bundle markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "preview-bundle.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Bundled waiting period text." [ "bundle keyword" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    FsColbert.IndexBundle.create
        "preview-bundle"
        "1.0.0"
        FsColbert.ModelCatalog.mxbaiEdgeColbertInt8.id
        FsColbert.ChunkOptions.fsKameDefaults
        FsColbert.TfidfOptions.defaults
        [ { sourceId = sourcePath
            sourceDisplayName = "Preview Bundle"
            sourceLocation = Some sourcePath
            sourceKind = Some "markdown"
            indexFile = "preview-bundle.fsci" } ]
    |> FsColbert.IndexBundle.writeManifest (Path.Combine(folder, "index-bundle.json"))

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(1, preview.records.Length)
        Assert.Equal<string list>([ "bundle keyword" ], preview.records.Head.keywords)

[<Fact>]
let ``visual descriptions setting does not block bundled prebuilt pdf index`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview-bundle.pdf")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview bundle PDF source.")

    let source =
        { kind = Pdf
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "preview-bundle-pdf.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Bundled PDF visual and text passage." [ "bundle pdf" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    FsColbert.IndexBundle.create
        "preview-bundle-pdf"
        "1.0.0"
        FsColbert.ModelCatalog.mxbaiEdgeColbertInt8.id
        FsColbert.ChunkOptions.fsKameDefaults
        FsColbert.TfidfOptions.defaults
        [ { sourceId = sourcePath
            sourceDisplayName = "Preview Bundle PDF"
            sourceLocation = Some sourcePath
            sourceKind = Some "pdf"
            indexFile = "preview-bundle-pdf.fsci" } ]
    |> FsColbert.IndexBundle.writeManifest (Path.Combine(folder, "index-bundle.json"))

    let pdfOptions =
        { KnowledgeSources.PdfIngestionOptions.create KnowledgeSources.PdfParsingMode.Hybrid with
            visualDescriptions =
                { PdfVisualDescriptionOptions.defaults with
                    enabled = true
                    modelId = "test-visual-model"
                    schemaVersion = "test-visual-schema" } }

    match KnowledgeSources.loadIndexPreviewWithOptions storageRoot ignore pdfOptions 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(1, preview.records.Length)
        Assert.Contains("Bundled PDF visual and text passage.", preview.records.Head.text)

[<Fact>]
let ``visual descriptions setting falls back to existing persisted pdf index`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview-existing.pdf")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview existing PDF source.")

    let source =
        { kind = Pdf
          location = sourcePath
          enabled = true }

    let indexPath =
        Path.Combine(persistedIndexFolder storageRoot, "preview-existing.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Existing PDF passage should remain searchable." [] ]
    |> FsColbert.IndexPersistence.save indexPath

    let reports = ResizeArray<string>()

    let pdfOptions =
        { KnowledgeSources.PdfIngestionOptions.create KnowledgeSources.PdfParsingMode.Hybrid with
            visualDescriptions =
                { PdfVisualDescriptionOptions.defaults with
                    enabled = true
                    modelId = "test-visual-model"
                    schemaVersion = "test-visual-schema" } }

    match KnowledgeSources.loadIndexPreviewWithOptions storageRoot reports.Add pdfOptions 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(1, preview.records.Length)
        Assert.Contains("Existing PDF passage should remain searchable.", preview.records.Head.text)

        Assert.Contains(
            reports,
            fun message -> message.Contains("reprocess this source to refresh", StringComparison.OrdinalIgnoreCase)
        )

[<Fact>]
let ``visual descriptions fallback does not load an index for a different pdf`` () =
    let storageRoot = tempStorageRoot ()
    let selectedPath = Path.Combine(storageRoot, "selected.pdf")
    let unselectedPath = Path.Combine(storageRoot, "unselected.pdf")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(selectedPath, "Selected PDF has no index.")
    File.WriteAllText(unselectedPath, "Unselected PDF has an index.")

    let selected =
        { kind = Pdf
          location = selectedPath
          enabled = true }

    let unselected =
        { kind = Pdf
          location = unselectedPath
          enabled = true }

    let indexPath = Path.Combine(persistedIndexFolder storageRoot, "unselected.fsci")

    fakeColbertIndex [ fakeIndexedPassage unselected 0 "Unselected PDF passage must not be returned." [] ]
    |> FsColbert.IndexPersistence.save indexPath

    let pdfOptions =
        { KnowledgeSources.PdfIngestionOptions.create KnowledgeSources.PdfParsingMode.Hybrid with
            visualDescriptions =
                { PdfVisualDescriptionOptions.defaults with
                    enabled = true
                    modelId = "test-visual-model"
                    schemaVersion = "test-visual-schema" } }

    match KnowledgeSources.loadIndexPreviewWithOptions storageRoot ignore pdfOptions 20 selected with
    | Ok preview -> failwith $"Expected no index for selected PDF, but loaded {preview.totalChunks} chunk(s)."
    | Error err -> Assert.Contains("No FsColbert index is available", err)

[<Fact>]
let ``index preview loads Speak2Docs installed prebuilt index`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "us-constitution-knowledge-pack.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "U.S. Constitution markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let folder = speak2DocsPrebuiltFolder storageRoot

    let indexPath =
        Path.Combine(folder, "prebuilt-us-constitution-knowledge-pack.md.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Article I establishes Congress." [ "article i"; "congress" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    let installed: KnowledgeSources.InstalledPrebuiltIndex array =
        [| { id = "prebuilt-us-constitution-knowledge-pack.md"
             kind = "markdown"
             displayName = "U.S. Constitution Knowledge Pack"
             storedPath = sourcePath
             indexPath = indexPath } |]

    File.WriteAllText(Path.Combine(folder, "prebuilt-indexes.installed.json"), JsonSerializer.Serialize installed)

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(1, preview.totalChunks)
        Assert.Equal(1, preview.records.Length)
        Assert.Contains("Article I", preview.records.Head.text)
        Assert.Equal<string list>([ "article i"; "congress" ], preview.records.Head.keywords)

[<Fact>]
let ``index preview uses installed prebuilt entry before incompatible bundle manifest`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview-installed.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview installed markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let folder = prebuiltFolder storageRoot
    let indexPath = Path.Combine(folder, "preview-installed.fsci")

    fakeColbertIndex [ fakeIndexedPassage source 0 "Installed prebuilt waiting period text." [ "installed keyword" ] ]
    |> FsColbert.IndexPersistence.save indexPath

    let installed: KnowledgeSources.InstalledPrebuiltIndex array =
        [| { id = "preview-installed"
             kind = "markdown"
             displayName = "Preview Installed"
             storedPath = sourcePath
             indexPath = indexPath } |]

    File.WriteAllText(Path.Combine(folder, "prebuilt-indexes.installed.json"), JsonSerializer.Serialize installed)

    FsColbert.IndexBundle.create
        "preview-installed"
        "1.0.0"
        "wrong/model"
        FsColbert.ChunkOptions.fsKameDefaults
        FsColbert.TfidfOptions.defaults
        [ { sourceId = sourcePath
            sourceDisplayName = "Preview Installed"
            sourceLocation = Some sourcePath
            sourceKind = Some "markdown"
            indexFile = "preview-installed.fsci" } ]
    |> FsColbert.IndexBundle.writeManifest (Path.Combine(folder, "index-bundle.json"))

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(1, preview.records.Length)
        Assert.Contains("Installed prebuilt waiting period text.", preview.records.Head.text)
        Assert.Equal<string list>([ "installed keyword" ], preview.records.Head.keywords)

[<Fact>]
let ``index preview returns at most requested random records`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "preview-limit.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Preview limit markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    let indexPath = Path.Combine(persistedIndexFolder storageRoot, "preview-limit.fsci")

    [ 0..24 ]
    |> List.map (fun index -> fakeIndexedPassage source index $"Passage {index}." [])
    |> fakeColbertIndex
    |> FsColbert.IndexPersistence.save indexPath

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Error err -> failwith err
    | Ok preview ->
        Assert.Equal(25, preview.totalChunks)
        Assert.Equal(20, preview.records.Length)
        Assert.True(preview.records.Length <= 20)

[<Fact>]
let ``index preview reports missing index`` () =
    let storageRoot = tempStorageRoot ()
    let sourcePath = Path.Combine(storageRoot, "missing-preview.md")
    Directory.CreateDirectory storageRoot |> ignore
    File.WriteAllText(sourcePath, "Missing preview markdown source.")

    let source =
        { kind = Markdown
          location = sourcePath
          enabled = true }

    match KnowledgeSources.loadIndexPreview storageRoot ignore KnowledgeSources.PdfParsingMode.Hybrid 20 source with
    | Ok _ -> failwith "Expected missing preview index."
    | Error err -> Assert.Contains("No FsColbert index is available", err)

[<Fact>]
let ``qa session composes injected context providers`` () =
    task {
        let source =
            { kind = Json
              location = "fake://source"
              enabled = true }

        let provider = FakeContextProvider source :> IQaContextProvider
        let storageRoot = tempStorageRoot ()

        let options =
            { testQaSessionOptions storageRoot with
                autoWriteback = false }

        let transport =
            websocketAnswerTransport (fun request _ ->
                [ responsesCompletedEvent "resp_compose_answer" "composed answer" ])

        let session = new QaSession(options, transport)
        let! errors = (session :> IQaOrchestrator).ConfigureAsync([ provider ], CancellationToken.None)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "composition"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Empty errors
        Assert.Single answer.context |> ignore
        Assert.Contains("composition", answer.context.Head.text)
        Assert.Equal<KnowledgeSource list>([ source ], answer.inventory)

        do! (session :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``qa session does not call llm query expansion by default`` () =
    task {
        let path =
            Path.Combine(Path.GetTempPath(), $"fsvoice-no-query-expansion-{Guid.NewGuid():N}.json")

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                {| documents =
                    [ {| id = "1"
                         title = "Policy"
                         text = "The debug answer is BLUE-42."
                         keywords = [] |} ] |}
            )
        )

        let expansion =
            new CountingChatClient(
                """{"terms":["BLUE-42"],"rewrittenQueries":["BLUE-42"],"sectionName":null,"queryType":"Question"}"""
            )

        let options =
            { testQaSessionOptions (tempStorageRoot ()) with
                autoWriteback = false
                clients =
                    { QaModelClients.none with
                        queryExpansion = Some expansion } }

        let transport =
            websocketAnswerTransport (fun request _ -> [ responsesCompletedEvent "resp_no_expansion_answer" "BLUE-42" ])

        use session = new QaSession(options, transport)

        let source =
            { kind = Json
              location = path
              enabled = true }

        let! _ = session.LoadSourcesAsync(InternalDocumentIndex, [ source ], CancellationToken.None)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What is the debug answer?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal(0, expansion.Count)
        Assert.Contains("BLUE-42", answer.context.Head.text)
    }

[<Fact>]
let ``qa session sends internal document index context to responses answer prompt`` () =
    task {
        let storageRoot = tempStorageRoot ()
        Directory.CreateDirectory storageRoot |> ignore

        let sourcePath = Path.Combine(storageRoot, "indexed-policy.json")

        File.WriteAllText(
            sourcePath,
            JsonSerializer.Serialize(
                {| documents =
                    [ {| id = "warranty"
                         title = "Cobalt Sensor Warranty"
                         text =
                          "The indexed guide says the cobalt sensor warranty lasts ninety days and requires a serial number for replacement claims."
                         keywords = [] |}
                      {| id = "snacks"
                         title = "Office Snacks"
                         text = "The office snack policy lists apples, tea, and weekly cleaning."
                         keywords = [] |} ] |}
            )
        )

        let source =
            { kind = Json
              location = sourcePath
              enabled = true }

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                [ responsesCompletedEvent "resp_index_answer" "The warranty lasts ninety days." ])

        let options =
            { testQaSessionOptions storageRoot with
                autoWriteback = false }

        use session = new QaSession(options, transport)

        let! errors = session.LoadSourcesAsync(InternalDocumentIndex, [ source ], CancellationToken.None)
        Assert.Empty errors

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "How long is the cobalt sensor warranty?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("The warranty lasts ninety days.", answer.answer)
        Assert.NotEmpty answer.context
        Assert.Contains("cobalt sensor warranty lasts ninety days", answer.context.Head.text)
        Assert.DoesNotContain(answer.context, fun chunk -> chunk.text.Contains("office snack policy"))
        Assert.Equal<KnowledgeSource list>([ source ], answer.inventory)

        Assert.Single(captured) |> ignore
        Assert.Equal(Some true, captured[0].generate)
        Assert.Equal(Some true, captured[0].store)
        Assert.Equal(None, captured[0].previous_response_id)

        let answerPrompt = inputTextFromResponseRequest captured[0]
        Assert.Contains("User question:", answerPrompt)
        Assert.Contains("How long is the cobalt sensor warranty?", answerPrompt)
        Assert.Contains("Selected source inventory:", answerPrompt)
        Assert.Contains("Matched source context:", answerPrompt)
        Assert.Contains("Cobalt Sensor Warranty", answerPrompt)
        Assert.Contains("cobalt sensor warranty lasts ninety days", answerPrompt)
        Assert.DoesNotContain("office snack policy", answerPrompt)
    }

[<Fact>]
let ``keyword schema rejection does not block keyword attachment`` () =
    async {
        let source =
            { kind = Markdown
              location = $"/tmp/schema-reject-{Guid.NewGuid():N}.md"
              enabled = true }

        let passages =
            [ keywordPassage source 0 "The guide covers emergency dental scheduling."
              keywordPassage source 1 "The guide excludes cosmetic dental scheduling." ]

        let logs = ResizeArray<string>()
        let storageRoot = tempStorageRoot ()

        let! enriched, _ =
            KnowledgeSources.attachKeywords
                storageRoot
                logs.Add
                (keywordOptions $"test-schema-{Guid.NewGuid():N}" (new InvalidJsonSchemaChatClient() :> IChatClient))
                source
                passages

        Assert.Equal<int>(2, enriched.Length)
        Assert.All(enriched, fun passage -> Assert.Empty passage.keywords)

        let schemaRejectionLogs =
            logs
            |> Seq.filter (fun log -> log.Contains("OpenAI rejected keyword schema and response-format fallbacks"))
            |> Seq.length

        Assert.Equal(1, schemaRejectionLogs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``keyword schema rejection retries with json mode`` () =
    async {
        let source =
            { kind = Markdown
              location = $"/tmp/schema-fallback-{Guid.NewGuid():N}.md"
              enabled = true }

        let passages =
            [ keywordPassage source 0 "The guide covers emergency dental scheduling."
              keywordPassage source 1 "The guide excludes cosmetic dental scheduling." ]

        let client =
            new SchemaFallbackChatClient(
                """{"items":[{"passageIndex":0,"keywords":["emergency dental scheduling"]},{"passageIndex":1,"keywords":["cosmetic dental exclusion"]}]}"""
            )

        let logs = ResizeArray<string>()
        let storageRoot = tempStorageRoot ()

        let! enriched, _ =
            KnowledgeSources.attachKeywords
                storageRoot
                logs.Add
                (keywordOptions $"test-schema-fallback-{Guid.NewGuid():N}" (client :> IChatClient))
                source
                passages

        Assert.Equal<string list>([ "emergency dental scheduling" ], enriched.Head.keywords)
        Assert.Equal<string list>([ "cosmetic dental exclusion" ], enriched.Tail.Head.keywords)
        Assert.True(client.StrictAttempts > 0)
        Assert.True(client.JsonModeAttempts > 0)

        Assert.True(
            logs
            |> Seq.exists (fun log -> log.Contains("retrying keyword generation in JSON mode"))
        )

        let finalSchemaRejectionLogs =
            logs
            |> Seq.filter (fun log -> log.Contains("indexing will continue without generated keywords"))
            |> Seq.length

        Assert.Equal(0, finalSchemaRejectionLogs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``keyword response format rejection retries with plain json`` () =
    async {
        let source =
            { kind = Markdown
              location = $"/tmp/format-fallback-{Guid.NewGuid():N}.md"
              enabled = true }

        let passages =
            [ keywordPassage source 0 "The guide covers emergency dental scheduling."
              keywordPassage source 1 "The guide excludes cosmetic dental scheduling." ]

        let client =
            new SchemaFallbackChatClient(
                """{"items":[{"passageIndex":0,"keywords":["emergency dental scheduling"]},{"passageIndex":1,"keywords":["cosmetic dental exclusion"]}]}""",
                rejectJsonMode = true
            )

        let logs = ResizeArray<string>()
        let storageRoot = tempStorageRoot ()

        let! enriched, _ =
            KnowledgeSources.attachKeywords
                storageRoot
                logs.Add
                (keywordOptions $"test-format-fallback-{Guid.NewGuid():N}" (client :> IChatClient))
                source
                passages

        Assert.Equal<string list>([ "emergency dental scheduling" ], enriched.Head.keywords)
        Assert.Equal<string list>([ "cosmetic dental exclusion" ], enriched.Tail.Head.keywords)
        Assert.True(client.StrictAttempts > 0)
        Assert.True(client.JsonModeAttempts > 0)
        Assert.True(client.PlainAttempts > 0)

        Assert.True(
            logs
            |> Seq.exists (fun log -> log.Contains("retrying keyword generation without response formatting"))
        )

        let finalSchemaRejectionLogs =
            logs
            |> Seq.filter (fun log -> log.Contains("indexing will continue without generated keywords"))
            |> Seq.length

        Assert.Equal(0, finalSchemaRejectionLogs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``keyword schema rejection keeps cached keyword records`` () =
    async {
        let schemaVersion = $"test-cache-schema-{Guid.NewGuid():N}"
        let storageRoot = tempStorageRoot ()

        let source =
            { kind = Markdown
              location = $"/tmp/schema-cache-{Guid.NewGuid():N}.md"
              enabled = true }

        let passages =
            [ keywordPassage source 0 "The guide covers emergency dental scheduling."
              keywordPassage source 1 "The guide excludes cosmetic dental scheduling." ]

        let options =
            keywordOptions schemaVersion (new InvalidJsonSchemaChatClient() :> IChatClient)

        seedKeywordCache storageRoot source options passages.Head [ "cached scheduling term" ]
        let logs = ResizeArray<string>()

        let! enriched, _ = KnowledgeSources.attachKeywords storageRoot logs.Add options source passages

        Assert.Equal<string list>([ "cached scheduling term" ], enriched.Head.keywords)
        Assert.Empty(enriched.Tail.Head.keywords)

        let schemaRejectionLogs =
            logs
            |> Seq.filter (fun log -> log.Contains("OpenAI rejected keyword schema and response-format fallbacks"))
            |> Seq.length

        Assert.Equal(1, schemaRejectionLogs)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``tool loader includes current time tool from tools project`` () =
    let providerFolder =
        System.IO.Path.GetDirectoryName(typeof<FsVoice.Ctx.Tools.CurrentTimeToolProvider>.Assembly.Location)

    let catalog = QaToolLoader.load (FakeQaToolHost()) (Some providerFolder)

    let hasCurrentTimeTool =
        catalog.tools
        |> List.exists (fun tool -> tool.PluginName = "FsVoiceTools" && tool.Name = "current_time")

    Assert.True(hasCurrentTimeTool)

[<Fact>]
let ``selected source search tool uses configured context chunk budget`` () =
    task {
        let requested = ResizeArray<int>()

        let host =
            { new IQaToolHost with
                member _.Report _ = ()

                member _.SearchKnowledgeAsync(_, maxResults, _) =
                    requested.Add maxResults
                    Task.FromResult("source context")

                member _.SourceInventoryAsync _ = Task.FromResult("No selected sources.")

                member _.SearchMemoryAsync(_, _, _) = Task.FromResult("No memory context.")

                member _.SearchBlackboardAsync(_, _) =
                    Task.FromResult("No blackboard context.") }

        let tool =
            QaToolLoader.builtInToolsWithLimit 7 host
            |> List.find (fun tool -> tool.Name = "selected_source_search")

        let defaultArgs =
            Dictionary<string, string>(dict [ "question", "summarize the study datasets" ])
            :> IReadOnlyDictionary<string, string>

        let cappedArgs =
            Dictionary<string, string>(dict [ "question", "summarize the study datasets"; "max_results", "20" ])
            :> IReadOnlyDictionary<string, string>

        let! _ = tool.InvokeAsync(defaultArgs, CancellationToken.None)
        let! _ = tool.InvokeAsync(cappedArgs, CancellationToken.None)

        Assert.Equal<int list>([ 7; 7 ], requested |> Seq.toList)
    }

[<Fact>]
let ``render context preserves full chunk text beyond old prefix limit`` () =
    let source =
        { kind = Pdf
          location = "/tmp/amem.pdf"
          enabled = true }

    let filler = String.replicate 700 "x"

    let chunk =
        { source = source
          index = 4
          sectionPath = []
          text =
            $"The study uses the LoCoMo dataset for long-term dialogue evaluation. {filler} Besides, the study uses a new dataset named DialSim for multi-party dialogue evaluation."
          score = 1.0f }

    let rendered = KnowledgeSources.renderContextWithLimit 1 [ chunk ]

    Assert.Contains("LoCoMo", rendered)
    Assert.Contains("DialSim", rendered)

[<Fact>]
let ``render context includes structured section path`` () =
    let source =
        { kind = Pdf
          location = "/tmp/amem.pdf"
          enabled = true }

    let chunk =
        { source = source
          index = 8
          sectionPath = [ "4 Experiment"; "4.3 Empirical Results" ]
          text = "DialSim appears in the empirical results discussion."
          score = 1.0f }

    let rendered = KnowledgeSources.renderContextWithLimit 1 [ chunk ]

    Assert.Contains("Section: 4 Experiment > 4.3 Empirical Results", rendered)
    Assert.Contains("DialSim appears", rendered)

[<Fact>]
let ``render context still limits number of chunks without truncating kept chunk`` () =
    let source =
        { kind = Pdf
          location = "/tmp/amem.pdf"
          enabled = true }

    let filler = String.replicate 700 "x"

    let firstChunkText =
        $"First chunk mentions LoCoMo near the start. {filler} DialSim remains in the same retrieved chunk."

    let chunks =
        [ { source = source
            index = 1
            sectionPath = []
            text = firstChunkText
            score = 1.0f }
          { source = source
            index = 2
            sectionPath = []
            text = "Second chunk should not be rendered."
            score = 0.5f } ]

    let rendered = KnowledgeSources.renderContextWithLimit 1 chunks

    Assert.Contains(firstChunkText, rendered)
    Assert.DoesNotContain("Second chunk should not be rendered.", rendered)

[<Fact>]
let ``rank promotes structured section target over lexical distractors`` () =
    async {
        let source =
            { kind = Pdf
              location = "/tmp/paper.pdf"
              enabled = true }

        let retrieval =
            { KnowledgeSources.emptyIndex with
                sources = [ source ]
                chunks =
                    [ { source = source
                        index = 1
                        sectionPath = []
                        text = "Section: Notes\nThis appendix mentions abstract abstract abstract."
                        score = 0.0f }
                      { source = source
                        index = 2
                        sectionPath = [ "Paper"; "ABSTRACT" ]
                        text = "This paper introduces a retrieval method."
                        score = 0.0f } ] }

        let! chunks = KnowledgeSources.rank None false false true ignore "Can you summarize the abstract?" 1 retrieval

        Assert.Equal(2, chunks.Head.index)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``rank promotes inline section label over checklist mentions`` () =
    async {
        let source =
            { kind = Pdf
              location = "/tmp/paper.pdf"
              enabled = true }

        let retrieval =
            { KnowledgeSources.emptyIndex with
                sources = [ source ]
                chunks =
                    [ { source = source
                        index = 1
                        sectionPath = []
                        text =
                          "The NeurIPS checklist asks whether claims made in the abstract and introduction reflect the paper. The paper abstract should summarize contributions."
                        score = 0.0f }
                      { source = source
                        index = 2
                        sectionPath = []
                        text =
                          "Abstract While LLM agents can use external tools, they require adaptive memory systems to leverage historical experiences."
                        score = 0.0f } ] }

        let! chunks = KnowledgeSources.rank None false false true ignore "summarize the paper abstract" 1 retrieval

        Assert.Equal(2, chunks.Head.index)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``rank corrects misspelled section target before searching`` () =
    async {
        let source =
            { kind = Pdf
              location = "/tmp/paper.pdf"
              enabled = true }

        let retrieval =
            { KnowledgeSources.emptyIndex with
                sources = [ source ]
                chunks =
                    [ { source = source
                        index = 1
                        sectionPath = []
                        text = "Section: Notes\nThis appendix mentions abstract abstract abstract."
                        score = 0.0f }
                      { source = source
                        index = 2
                        sectionPath = []
                        text = "Section: ABSTRACT\nThis paper introduces a retrieval method."
                        score = 0.0f } ] }

        let! chunks = KnowledgeSources.rank None false false true ignore "Can you summarize the abtract?" 1 retrieval

        Assert.Equal(2, chunks.Head.index)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``rank keeps coverage from each selected source for multi-document questions`` () =
    async {
        let sourceA =
            { kind = Pdf
              location = "/tmp/source-a.pdf"
              enabled = true }

        let sourceB =
            { kind = Pdf
              location = "/tmp/source-b.pdf"
              enabled = true }

        let retrieval =
            { KnowledgeSources.emptyIndex with
                sources = [ sourceA; sourceB ]
                chunks =
                    [ { source = sourceA
                        index = 1
                        sectionPath = []
                        text = "Latency latency latency for source A."
                        score = 0.0f }
                      { source = sourceA
                        index = 2
                        sectionPath = []
                        text = "Latency latency latency architecture in source A."
                        score = 0.0f }
                      { source = sourceB
                        index = 1
                        sectionPath = []
                        text = "Latency tradeoffs for source B."
                        score = 0.0f } ] }

        let! chunks = KnowledgeSources.rank None false false true ignore "Compare both documents on latency" 2 retrieval

        let sourceLocations =
            chunks |> List.map (fun chunk -> chunk.source.location) |> Set.ofList

        Assert.Equal<int>(2, chunks.Length)
        Assert.Contains(sourceA.location, sourceLocations)
        Assert.Contains(sourceB.location, sourceLocations)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``rank returns representative content for broad selected-document summaries`` () =
    async {
        let sourceA =
            { kind = Pdf
              location = "/tmp/broad-a.pdf"
              enabled = true }

        let sourceB =
            { kind = Pdf
              location = "/tmp/broad-b.pdf"
              enabled = true }

        let retrieval =
            { KnowledgeSources.emptyIndex with
                sources = [ sourceA; sourceB ]
                chunks =
                    [ { source = sourceA
                        index = 1
                        sectionPath = []
                        text = "Quantum scheduling methods are introduced here."
                        score = 0.0f }
                      { source = sourceB
                        index = 1
                        sectionPath = []
                        text = "Energy market simulations are introduced here."
                        score = 0.0f } ] }

        let! chunks = KnowledgeSources.rank None false false true ignore "Summarize these documents" 4 retrieval

        let sourceLocations =
            chunks |> List.map (fun chunk -> chunk.source.location) |> Set.ofList

        Assert.Contains(sourceA.location, sourceLocations)
        Assert.Contains(sourceB.location, sourceLocations)
    }
    |> Async.RunSynchronously

[<Fact>]
let ``durable memory types explicit user preference as directive`` () =
    let snapshot =
        { turnId = "turn_preference"
          itemId = "item_preference"
          revision = 1
          text = "Remember that I prefer concise technical answers."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let proposals =
        DurableMemory.proposalsFromExchange snapshot "Okay, I will remember that."

    let _ = Assert.Single proposals

    Assert.Equal(Directive, proposals.Head.kind)
    Assert.Contains("concise", proposals.Head.text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``durable memory hot overlay makes committed memory immediately recallable`` () =
    let snapshot =
        { turnId = "turn_decision"
          itemId = "item_decision"
          revision = 1
          text = "We decided to use typed memory records for realtime recall."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let proposals =
        DurableMemory.proposalsFromExchange snapshot "That decision is recorded."

    let store, updates, _ =
        DurableMemory.commitProposals (DurableMemory.inMemory []) proposals

    let spec =
        { query = "typed memory realtime recall decision"
          kinds = [ Decision ]
          scopes = [ Session ]
          namespaceId = DurableMemory.defaultNamespace
          temporalMode = CurrentOnly
          temporalReference = None
          includeSuperseded = false
          recallBudget = Fast
          maxCandidates = 10
          minScore = None
          latencyBudget = TimeSpan.FromMilliseconds 90. }

    let hits = DurableMemory.recall None spec store |> Async.RunSynchronously

    let _ = Assert.Single updates

    Assert.NotEmpty hits
    Assert.Equal(Decision, hits.Head.record.kind)

[<Fact>]
let ``durable memory correction supersedes related directive`` () =
    let original =
        { turnId = "turn_original"
          itemId = "item_original"
          revision = 1
          text = "I prefer verbose technical answers."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1.) }

    let correction =
        { turnId = "turn_correction"
          itemId = "item_correction"
          revision = 2
          text = "Actually, I prefer concise technical answers."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let store, _, _ =
        DurableMemory.commitProposals
            (DurableMemory.inMemory [])
            (DurableMemory.proposalsFromExchange original "Noted.")

    let store, updates, _ =
        DurableMemory.commitProposals store (DurableMemory.proposalsFromExchange correction "Updated.")

    let currentDirectives =
        store.records
        |> List.filter (fun record -> record.kind = Directive && record.status = Current)

    let supersededDirectives =
        store.records
        |> List.filter (fun record -> record.kind = Directive && record.status = Superseded)

    Assert.True(updates |> List.exists (fun update -> update.outcome = Contradiction))
    let _ = Assert.Single currentDirectives
    let _ = Assert.Single supersededDirectives

    Assert.Contains("concise", currentDirectives.Head.text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``durable memory forget request retracts related current record`` () =
    let original =
        { turnId = "turn_original_forget"
          itemId = "item_original_forget"
          revision = 1
          text = "I prefer verbose implementation notes."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1.) }

    let forget =
        { turnId = "turn_forget"
          itemId = "item_forget"
          revision = 2
          text = "Forget my preference for verbose implementation notes."
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let store, _, _ =
        DurableMemory.commitProposals
            (DurableMemory.inMemory [])
            (DurableMemory.proposalsFromExchange original "Noted.")

    let store, logs = DurableMemory.retractFromTurn store forget

    Assert.True(
        logs
        |> List.exists (fun log -> log.Contains("Retracted", StringComparison.OrdinalIgnoreCase))
    )

    Assert.True(
        store.records
        |> List.exists (fun record -> record.kind = Directive && record.status = Retracted)
    )

[<Fact>]
let ``durable memory service retract persists and prevents recall`` () =
    task {
        let path = Path.Combine(tempStorageRoot (), "memory.json")

        let remember =
            { turnId = "turn_service_remember"
              itemId = "item_service_remember"
              revision = 1
              text = "Remember that I prefer verbose implementation notes."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1.) }

        let forget =
            { turnId = "turn_service_forget"
              itemId = "item_service_forget"
              revision = 2
              text = "Forget my preference for verbose implementation notes."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow }

        seedDurableMemory path remember "Noted." |> ignore

        let service = DurableMemoryService(path) :> IMemoryService

        let! before = service.SearchAsync("verbose implementation notes", 10, CancellationToken.None)
        Assert.Contains("verbose implementation notes", before, StringComparison.OrdinalIgnoreCase)

        let retractLogs = service.RetractFromTurn forget
        Assert.Contains(retractLogs, fun log -> log.Contains("Retracted", StringComparison.OrdinalIgnoreCase))

        let! after = service.SearchAsync("verbose implementation notes", 10, CancellationToken.None)
        Assert.DoesNotContain("verbose implementation notes", after, StringComparison.OrdinalIgnoreCase)

        let reloaded = DurableMemoryService(path) :> IMemoryService
        let! afterReload = reloaded.SearchAsync("verbose implementation notes", 10, CancellationToken.None)
        Assert.DoesNotContain("verbose implementation notes", afterReload, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``durable memory service clear all hard clears persisted store`` () =
    task {
        let path = Path.Combine(tempStorageRoot (), "memory.json")

        let remember =
            { turnId = "turn_service_clear"
              itemId = "item_service_clear"
              revision = 1
              text = "Remember that the project codename is cobalt."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow }

        seedDurableMemory path remember "Noted." |> ignore
        Assert.True(File.Exists path)

        let service = DurableMemoryService(path) :> IMemoryService

        let clearLogs = service.ClearAll()
        Assert.Contains(clearLogs, fun log -> log.Contains("Cleared", StringComparison.OrdinalIgnoreCase))
        Assert.False(File.Exists path)

        let! after = service.SearchAsync("cobalt", 10, CancellationToken.None)
        Assert.DoesNotContain("cobalt", after, StringComparison.OrdinalIgnoreCase)

        let reloaded = DurableMemoryService(path) :> IMemoryService
        let! afterReload = reloaded.SearchAsync("cobalt", 10, CancellationToken.None)
        Assert.DoesNotContain("cobalt", afterReload, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``qa session forget turn retracts durable memory and skips writeback`` () =
    task {
        let storageRoot = tempStorageRoot ()
        let memoryPath = Path.Combine(storageRoot, "memory.json")

        let remember =
            { turnId = "turn_session_remember"
              itemId = "item_session_remember"
              revision = 1
              text = "Remember that I prefer verbose implementation notes."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1.) }

        seedDurableMemory memoryPath remember "Noted." |> ignore

        let longAnswer =
            String.replicate
                4
                "This acknowledgement is intentionally long enough to become an episode if forget turns were allowed to write back. "

        let transport =
            websocketAnswerTransport (fun request _ -> [ responsesCompletedEvent "resp_forget_answer" longAnswer ])

        let options =
            { testQaSessionOptions storageRoot with
                memoryStorePath = Some memoryPath }

        use session = new QaSession(options, transport)

        let request text =
            { turnId = Guid.NewGuid().ToString("N")
              question = text
              realtimeJudgement = None
              deadline = None }

        let loaded, _ = DurableMemory.load memoryPath
        Assert.Single loaded.records |> ignore
        Assert.Contains("verbose implementation notes", loaded.records.Head.text, StringComparison.OrdinalIgnoreCase)

        let! forgetAnswer =
            session.AnswerAsync(
                request
                    "Forget my preference for verbose implementation notes and remove that durable note before answering this verification turn.",
                CancellationToken.None
            )

        Assert.Contains(
            forgetAnswer.toolObservations,
            fun observation ->
                observation.toolName = "durable_memory_forget"
                && observation.content.Contains("Retracted", StringComparison.OrdinalIgnoreCase)
        )

        let afterForget, _ = DurableMemory.load memoryPath
        Assert.Single afterForget.records |> ignore
        Assert.All(afterForget.records, fun record -> Assert.Equal(Retracted, record.status))
    }

[<Fact>]
let ``qa session durable memory disabled skips recall writeback and memory tool`` () =
    task {
        let storageRoot = tempStorageRoot ()
        let memoryPath = Path.Combine(storageRoot, "memory.json")

        let remember =
            { turnId = "turn_disabled_existing"
              itemId = "item_disabled_existing"
              revision = 1
              text = "Remember that the hidden project codename is cobalt."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow }

        seedDurableMemory memoryPath remember "Noted." |> ignore

        let captured = ResizeArray<FsResponses.WebSocketCreateRequest>()

        let transport =
            websocketAnswerTransport (fun request _ ->
                captured.Add request

                [ responsesCompletedEvent $"resp_disabled_answer_{captured.Count}" "No memory used." ])

        let options =
            { testQaSessionOptions storageRoot with
                memoryStorePath = Some memoryPath
                enableDurableMemory = false }

        use session = new QaSession(options, transport)

        Assert.DoesNotContain(session.ToolCatalog.tools, fun tool -> tool.Name = "durable_memory_search")

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What do you remember?"
              realtimeJudgement = None
              deadline = None }

        let! _ = session.AnswerAsync(request, CancellationToken.None)

        let promptText =
            captured
            |> Seq.filter (fun request -> request.generate = Some true)
            |> Seq.map inputTextFromResponseRequest
            |> String.concat "\n"

        Assert.DoesNotContain("cobalt", promptText, StringComparison.OrdinalIgnoreCase)

        let rememberWhileDisabled =
            { request with
                turnId = Guid.NewGuid().ToString("N")
                question = "Remember that my disabled memory test phrase is violet." }

        let! _ = session.AnswerAsync(rememberWhileDisabled, CancellationToken.None)

        let loaded, _ = DurableMemory.load memoryPath
        Assert.Single loaded.records |> ignore

        Assert.DoesNotContain(
            loaded.records,
            fun record -> record.text.Contains("violet", StringComparison.OrdinalIgnoreCase)
        )
    }

[<Fact>]
let ``blackboard lexical search finds typed tool observations`` () =
    let observation =
        { pluginName = "FsVoiceTools"
          toolName = "selected_source_search"
          query = "renters claim process"
          content = "The selected source describes filing a tenant policy claim after damaged property."
          createdAt = DateTimeOffset.UtcNow }

    let board =
        Blackboard.empty 10
        |> Blackboard.add (BlackboardRecords.toolObservation "turn_blackboard" observation)

    let hits =
        board
        |> Blackboard.search
            { BlackboardSearchOptions.defaults with
                includeKinds = [ ToolObservation ] }
            "rental policy damage claim"

    Assert.NotEmpty hits
    Assert.Equal(ToolObservation, hits.Head.record.kind)
    Assert.Contains("tenant policy claim", hits.Head.record.text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``blackboard pruning selection preserves recent turns`` () =
    let records =
        [ for index in 1..8 do
              let turnId = $"turn_{index}"

              yield
                  BlackboardRecords.transcript (
                      testTranscript turnId (String.replicate 12 $"turn {index} important context ")
                  ) ]

    let board = boardFromRecords records

    let selection =
        board
        |> Blackboard.tryCreatePruneSelection
            { triggerChars = 1
              targetChars = 1
              preserveRecentTurns = 6 }
        |> Option.defaultWith (fun () -> failwith "Expected blackboard pruning selection.")

    let selectedTurnIds =
        selection.recordsToSummarize |> List.map _.turnId |> Set.ofList

    Assert.Equal<string list>(
        [ "turn_8"; "turn_7"; "turn_6"; "turn_5"; "turn_4"; "turn_3" ],
        selection.preservedTurnIds
    )

    Assert.Contains("turn_1", selectedTurnIds)
    Assert.Contains("turn_2", selectedTurnIds)
    Assert.DoesNotContain("turn_3", selectedTurnIds)
    Assert.DoesNotContain("turn_8", selectedTurnIds)

[<Fact>]
let ``blackboard pruning selection summarizes eligible old records and drops trace records`` () =
    let source =
        { kind = Json
          location = "fake://blackboard-pruning"
          enabled = true }

    let oldTurn = "turn_old"

    let oldTranscript =
        BlackboardRecords.transcript (testTranscript oldTurn "Old user goal about tenant claim deadlines.")

    let oldSource =
        BlackboardRecords.sourceEvidence
            oldTurn
            { source = source
              index = 7
              sectionPath = []
              text = "Old source evidence says tenant claims must be filed within thirty days."
              score = 1.0f }

    let oldJudgement =
        BlackboardRecords.realtimeJudgement
            oldTurn
            { turnKind = Some "question"
              topicContinuity = None
              memoryAction = None
              needsExternalContext = Some true
              confidence = 0.9
              riskFlags = RiskFlags.none }

    let oldCandidate =
        BlackboardRecords.answerCandidate oldTurn "Draft answer that should be dropped."

    let oldBlackboardSearch =
        BlackboardRecords.toolObservation
            oldTurn
            { pluginName = "FsVoiceTools"
              toolName = "blackboard_search"
              query = "old recursive lookup"
              content = "Recursive blackboard search content should not be summarized."
              createdAt = DateTimeOffset.UtcNow }

    let recentRecords =
        [ for index in 1..6 do
              let turnId = $"turn_recent_{index}"
              yield BlackboardRecords.transcript (testTranscript turnId $"Recent turn {index}.") ]

    let board =
        boardFromRecords (
            [ oldTranscript; oldSource; oldJudgement; oldCandidate; oldBlackboardSearch ]
            @ recentRecords
        )

    let selection =
        board
        |> Blackboard.tryCreatePruneSelection
            { triggerChars = 1
              targetChars = 1
              preserveRecentTurns = 6 }
        |> Option.defaultWith (fun () -> failwith "Expected blackboard pruning selection.")

    let summarizedIds = selection.recordsToSummarize |> List.map _.id |> Set.ofList
    let droppedIds = selection.recordsToDrop |> List.map _.id |> Set.ofList

    Assert.Contains(oldTranscript.id, summarizedIds)
    Assert.Contains(oldSource.id, summarizedIds)
    Assert.DoesNotContain(oldJudgement.id, summarizedIds)
    Assert.DoesNotContain(oldCandidate.id, summarizedIds)
    Assert.DoesNotContain(oldBlackboardSearch.id, summarizedIds)
    Assert.Contains(oldJudgement.id, droppedIds)
    Assert.Contains(oldCandidate.id, droppedIds)
    Assert.Contains(oldBlackboardSearch.id, droppedIds)

type TestToHost =
    | ShowThing of string
    | VoiceActivityChanged of bool
    | RequestSipRealtime of JsonElement

type TestFromHost =
    | ScreenShown of string
    | ProjectionCompleted of string
    | SipStateChanged of SipRealtimeState
    | SipConnectionFailed of string

type FakeTypedVoiceSession(startMessages: TestToHost list) =
    let toHost = Channel.CreateUnbounded<TestToHost>()
    let fromHost = Channel.CreateUnbounded<TestFromHost>()
    let mutable stopped = false

    member _.FromHost = fromHost.Reader
    member _.Stopped = stopped

    interface IVoiceSession<TestToHost, TestFromHost> with
        member _.ToHost = toHost.Reader

        member _.SendFromHostAsync(message, cancellationToken) =
            fromHost.Writer.WriteAsync(message, cancellationToken).AsTask()

        member _.StartAsync cancellationToken =
            task {
                for message in startMessages do
                    do! toHost.Writer.WriteAsync(message, cancellationToken).AsTask()
            }

        member _.StopAsync _ =
            task {
                stopped <- true
                toHost.Writer.TryComplete() |> ignore
                fromHost.Writer.TryComplete() |> ignore
            }

        member this.DisposeAsync() =
            task { do! (this :> IVoiceSession<TestToHost, TestFromHost>).StopAsync CancellationToken.None }
            |> ValueTask

type FakeTypedVoiceOrchestration() =
    let session =
        FakeTypedVoiceSession([ ShowThing "claim-123"; VoiceActivityChanged true ])

    member _.Session = session

    interface IVoiceOrchestration<TestToHost, TestFromHost> with
        member _.Definition =
            { VoiceOrchestrationDefinition.create "fake.typed" "0.1.0" "Fake Typed Orchestration" with
                description = Some "A typed orchestration used by tests." }

        member _.CreateSessionAsync(_, _, _) =
            Task.FromResult(session :> IVoiceSession<TestToHost, TestFromHost>)

type FakeSipVoiceOrchestration(startMessages: TestToHost list) =
    let session = FakeTypedVoiceSession(startMessages)
    let gate = obj ()
    let mutable createCount = 0
    let mutable connection: VoiceConnection option = None

    member _.Session = session
    member _.CreateCount = lock gate (fun () -> createCount)
    member _.Connection = lock gate (fun () -> connection)

    interface IVoiceOrchestration<TestToHost, TestFromHost> with
        member _.Definition =
            { VoiceOrchestrationDefinition.create "fake.sip" "0.1.0" "Fake SIP Orchestration" with
                description = Some "A SIP orchestration used by tests." }

        member _.CreateSessionAsync(_, voiceConnection, _) =
            lock gate (fun () ->
                createCount <- createCount + 1
                connection <- Some voiceConnection)

            Task.FromResult(session :> IVoiceSession<TestToHost, TestFromHost>)

type FakeOpenAiRealtimeWebRtcSession(?startError: exn) =
    let received = Event<JsonElement>()
    let connected = Event<unit>()
    let closed = Event<exn option>()
    let gate = obj ()
    let mutable startedSession: JsonElement option = None
    let mutable startedCodec: SipAudioCodec option = None
    let sentClientEvents = ResizeArray<JsonElement>()
    let mutable fromSipPipeCount = 0
    let mutable toSipPipeCount = 0
    let mutable disposed = false

    member _.StartedSession = lock gate (fun () -> startedSession)
    member _.StartedCodec = lock gate (fun () -> startedCodec)
    member _.SentClientEvents = lock gate (fun () -> sentClientEvents |> Seq.toList)
    member _.FromSipPipeCount = lock gate (fun () -> fromSipPipeCount)
    member _.ToSipPipeCount = lock gate (fun () -> toSipPipeCount)
    member _.Disposed = lock gate (fun () -> disposed)

    member _.EmitReceived(event: JsonElement) = received.Trigger(event.Clone())

    member _.Close(?error: exn) = closed.Trigger error

    interface IOpenAiRealtimeWebRtcSession with
        member _.Received = received.Publish
        member _.Connected = connected.Publish
        member _.Closed = closed.Publish

        member _.StartAsync(session, codec, _cancellationToken) =
            task {
                lock gate (fun () ->
                    startedSession <- Some(session.Clone())
                    startedCodec <- Some codec)

                match startError with
                | Some ex -> return raise ex
                | None -> connected.Trigger()
            }
            :> Task

        member _.SendClientEvent(event: JsonElement) =
            lock gate (fun () -> sentClientEvents.Add(event.Clone()))

        member _.PipeFromRtpSession(_, _, _, _) =
            lock gate (fun () -> fromSipPipeCount <- fromSipPipeCount + 1)
            null

        member _.PipeToRtpSession(_, _, _, _, _) =
            lock gate (fun () -> toSipPipeCount <- toSipPipeCount + 1)
            null

        member _.Dispose() = lock gate (fun () -> disposed <- true)

type FakeOpenAiRealtimeWebRtcSessionFactory(session: IOpenAiRealtimeWebRtcSession) =
    interface IOpenAiRealtimeWebRtcSessionFactory with
        member _.Create _ = session

let private typedVoiceConnection () =
    let inbound = Channel.CreateUnbounded<JsonElement>()
    let outbound = Channel.CreateUnbounded<JsonElement>()

    { VoiceConnection.receiver = inbound.Reader
      sender = outbound.Writer }

let private typedVoiceContext () : VoiceOrchestrationContext =
    { storageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      settings = Map.empty
      report = ignore }

let private testHostCodec: HostMessageCodec<TestToHost, TestFromHost> =
    let encodeToHost message =
        match message with
        | ShowThing value -> JsonSerializer.SerializeToElement({| case = "ShowThing"; value = value |})
        | VoiceActivityChanged active ->
            JsonSerializer.SerializeToElement(
                {| case = "VoiceActivityChanged"
                   active = active |}
            )
        | RequestSipRealtime session ->
            JsonSerializer.SerializeToElement(
                {| case = "RequestSipRealtime"
                   session = session |}
            )

    let decodeFromHost (json: JsonElement) =
        try
            match json.GetProperty("case").GetString() with
            | "ScreenShown" -> Ok(ScreenShown(json.GetProperty("value").GetString()))
            | "ProjectionCompleted" -> Ok(ProjectionCompleted(json.GetProperty("value").GetString()))
            | value -> Error $"Unsupported test host message: {value}"
        with ex ->
            Error ex.Message

    { encodeToHost = encodeToHost
      decodeFromHost = decodeFromHost }

let private bridgeOptions sessionId orchestration codec : BridgeSessionOptions<TestToHost, TestFromHost> =
    { sessionId = sessionId
      orchestration = orchestration
      context = typedVoiceContext ()
      hostMessageCodec = codec }

[<Fact>]
let ``typed voice orchestration emits host messages on start`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration() :> IVoiceOrchestration<_, _>

        let! session =
            orchestration.CreateSessionAsync(typedVoiceContext (), typedVoiceConnection (), CancellationToken.None)

        do! session.StartAsync CancellationToken.None
        let! message = session.ToHost.ReadAsync(CancellationToken.None).AsTask()

        Assert.Equal(ShowThing "claim-123", message)
    }

[<Fact>]
let ``typed voice session forwards host messages into orchestration`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration()

        let! session =
            (orchestration :> IVoiceOrchestration<_, _>)
                .CreateSessionAsync(typedVoiceContext (), typedVoiceConnection (), CancellationToken.None)

        do! session.SendFromHostAsync(ScreenShown "sources", CancellationToken.None)
        let! message = orchestration.Session.FromHost.ReadAsync(CancellationToken.None).AsTask()

        Assert.Equal(ScreenShown "sources", message)
    }

[<Fact>]
let ``typed voice session stops and completes host streams`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration()

        let! session =
            (orchestration :> IVoiceOrchestration<_, _>)
                .CreateSessionAsync(typedVoiceContext (), typedVoiceConnection (), CancellationToken.None)

        do! session.StopAsync CancellationToken.None

        Assert.True(orchestration.Session.Stopped)

        let! canRead = session.ToHost.WaitToReadAsync(CancellationToken.None).AsTask()
        Assert.False canRead
    }

[<Fact>]
let ``bridge session exposes typed host messages through codec`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration()
        let sessionId = BridgeSessionId.newId ()

        let options =
            bridgeOptions sessionId (orchestration :> IVoiceOrchestration<_, _>) (Some testHostCodec)

        use session = new BridgeSession<TestToHost, TestFromHost>(options)
        do! session.StartAsync CancellationToken.None

        let! event = session.WaitForServerEventAsync(CancellationToken.None)

        match event with
        | Some event ->
            Assert.Equal(HostMessage, event.kind)
            Assert.Equal("host.message", event.eventType)
            Assert.Equal("ShowThing", event.payload.Value.GetProperty("case").GetString())
            Assert.Equal("claim-123", event.payload.Value.GetProperty("value").GetString())
        | None -> failwith "Expected encoded host message."
    }

[<Fact>]
let ``bridge session forwards host messages into typed session through codec`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration()
        let sessionId = BridgeSessionId.newId ()

        let options =
            bridgeOptions sessionId (orchestration :> IVoiceOrchestration<_, _>) (Some testHostCodec)

        use session = new BridgeSession<TestToHost, TestFromHost>(options)
        do! session.StartAsync CancellationToken.None

        do!
            session.AcceptClientEventAsync(
                { eventId = "host_1"
                  kind = BridgeClientEventKind.HostMessage
                  eventType = "host.message"
                  payload =
                    JsonSerializer.SerializeToElement(
                        {| case = "ScreenShown"
                           value = "sources" |}
                    )
                    |> Some
                  receivedAt = DateTimeOffset.UtcNow },
                CancellationToken.None
            )

        let! message = orchestration.Session.FromHost.ReadAsync(CancellationToken.None).AsTask()

        Assert.Equal(ScreenShown "sources", message)
    }

[<Fact>]
let ``bridge session forwards raw voice events both directions`` () =
    task {
        let orchestration = FakeTypedVoiceOrchestration()
        let sessionId = BridgeSessionId.newId ()

        let options =
            bridgeOptions sessionId (orchestration :> IVoiceOrchestration<_, _>) None

        use session = new BridgeSession<TestToHost, TestFromHost>(options)
        do! session.StartAsync CancellationToken.None

        do!
            session.AcceptClientEventAsync(
                { eventId = "server_1"
                  kind = RealtimeServerEvent
                  eventType = "input_audio_buffer.speech_started"
                  payload =
                    JsonSerializer.SerializeToElement(
                        {| event_id = "server_1"
                           ``type`` = "input_audio_buffer.speech_started" |}
                    )
                    |> Some
                  receivedAt = DateTimeOffset.UtcNow },
                CancellationToken.None
            )

        let! inbound = session.Connection.receiver.ReadAsync(CancellationToken.None).AsTask()
        Assert.Equal("input_audio_buffer.speech_started", inbound.GetProperty("type").GetString())

        do!
            session.Connection.sender
                .WriteAsync(
                    JsonSerializer.SerializeToElement(
                        {| event_id = "client_1"
                           ``type`` = "response.create" |}
                    ),
                    CancellationToken.None
                )
                .AsTask()

        let! outbound = session.WaitForServerEventAsync(CancellationToken.None)

        match outbound with
        | Some event ->
            Assert.Equal(VoiceEvent, event.kind)
            Assert.Equal("response.create", event.eventType)
            Assert.Equal("client_1", event.eventId)
        | None -> failwith "Expected raw outbound voice event."
    }

[<Fact>]
let ``bridge session store creates and removes typed sessions`` () =
    task {
        let store = BridgeSessionStore<TestToHost, TestFromHost>()
        let sessionId = BridgeSessionId.newId ()
        let orchestration = FakeTypedVoiceOrchestration()

        let options =
            bridgeOptions sessionId (orchestration :> IVoiceOrchestration<_, _>) (Some testHostCodec)

        let! session = store.CreateAsync(options, CancellationToken.None)

        Assert.Equal(1, store.Count)
        Assert.True(store.TryGet(session.SessionId).IsSome)

        do! store.RemoveAsync session.SessionId

        Assert.Equal(0, store.Count)
        Assert.True(store.TryGet(session.SessionId).IsNone)
    }

let private sipRealtimeSession model =
    JsonSerializer.SerializeToElement(
        {| model = model
           instructions = "test realtime session" |}
    )

let private sipCallContext codec =
    { callId = Guid.NewGuid().ToString("N")
      sessionId = BridgeSessionId.newId ()
      sipUri = "sip:test@example.test"
      remoteEndPoint = "127.0.0.1:5060"
      negotiatedCodec = codec }

let private sipHostAdapter =
    { tryGetRealtimeSession =
        function
        | RequestSipRealtime session -> Some session
        | _ -> None
      stateChanged = SipStateChanged >> Some
      connectionFailed = SipConnectionFailed >> Some }

let private sipOptions enabled port =
    let options = SipListenerOptions()
    options.Enabled <- enabled
    options.ListenUdpPort <- port
    options.RealtimeRequestTimeoutSeconds <- 1
    Options.Create options

let private sipLoggerFactory () = LoggerFactory.Create(fun _ -> ())

let private createSipBridge orchestration openAi =
    let registration =
        { createSessionOptions =
            fun context ->
                { sessionId = context.sessionId
                  orchestration = orchestration
                  context = typedVoiceContext ()
                  hostMessageCodec = Some testHostCodec }
          hostAdapter = sipHostAdapter }

    let factory =
        FakeOpenAiRealtimeWebRtcSessionFactory(openAi) :> IOpenAiRealtimeWebRtcSessionFactory

    let loggerFactory = sipLoggerFactory ()

    new SipCallBridge<TestToHost, TestFromHost>(
        registration,
        sipOptions true 0,
        factory,
        loggerFactory.CreateLogger<SipCallBridge<TestToHost, TestFromHost>>()
    )

let private readSipConnectionFailureAsync (session: FakeTypedVoiceSession) =
    task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 3.0)
        let mutable result = None

        while result.IsNone do
            let! message = session.FromHost.ReadAsync(timeout.Token).AsTask()

            match message with
            | SipConnectionFailed message -> result <- Some message
            | _ -> ()

        return result.Value
    }

[<Fact>]
let ``sip codec helpers parse defaults and validate SDP answers`` () =
    Assert.Equal<SipAudioCodec list>([ PCMU; PCMA ], SipAudioCodec.defaultAllowed)
    Assert.Equal(Some PCMU, SipAudioCodec.tryParse "ulaw")
    Assert.Equal(Some PCMA, SipAudioCodec.tryParse "G.711A")
    Assert.Equal(Some Opus, SipAudioCodec.tryParse "opus")
    Assert.Equal<SipAudioCodec list>([ PCMA; PCMU ], SipAudioCodec.fromConfig [ "pcma"; "pcmu"; "pcma" ])

    let answer = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 0\r\na=rtpmap:0 PCMU/8000\r\n"

    Assert.True(SdpCodec.answerContainsCodec PCMU answer)
    Assert.False(SdpCodec.answerContainsCodec PCMA answer)

[<Fact>]
let ``sip call bridge creates orchestration and starts OpenAI realtime session`` () =
    task {
        let realtimeSession = sipRealtimeSession "gpt-realtime-test"

        let orchestration =
            FakeSipVoiceOrchestration([ RequestSipRealtime realtimeSession ])

        let openAi = new FakeOpenAiRealtimeWebRtcSession()

        let bridge =
            createSipBridge (orchestration :> IVoiceOrchestration<_, _>) (openAi :> IOpenAiRealtimeWebRtcSession)

        use cts = new CancellationTokenSource()

        let runTask = bridge.RunAsync(sipCallContext PCMU, null, null, cts.Token)

        do! waitForAsync "Expected OpenAI realtime session to start." (fun () -> openAi.StartedSession.IsSome)

        Assert.Equal(1, orchestration.CreateCount)
        Assert.Equal(Some PCMU, openAi.StartedCodec)
        Assert.Equal("gpt-realtime-test", openAi.StartedSession.Value.GetProperty("model").GetString())
        Assert.Equal(1, openAi.FromSipPipeCount)
        Assert.Equal(1, openAi.ToSipPipeCount)

        cts.Cancel()
        do! runTask
        Assert.True(openAi.Disposed)
    }

[<Fact>]
let ``sip call bridge forwards voice connection events to and from OpenAI data channel`` () =
    task {
        let realtimeSession = sipRealtimeSession "gpt-realtime-test"

        let orchestration =
            FakeSipVoiceOrchestration([ RequestSipRealtime realtimeSession ])

        let openAi = new FakeOpenAiRealtimeWebRtcSession()

        let bridge =
            createSipBridge (orchestration :> IVoiceOrchestration<_, _>) (openAi :> IOpenAiRealtimeWebRtcSession)

        use cts = new CancellationTokenSource()

        let runTask = bridge.RunAsync(sipCallContext PCMU, null, null, cts.Token)

        do! waitForAsync "Expected SIP voice connection to be created." (fun () -> orchestration.Connection.IsSome)
        do! waitForAsync "Expected OpenAI realtime session to start." (fun () -> openAi.StartedSession.IsSome)

        let connection = orchestration.Connection.Value

        do!
            connection.sender
                .WriteAsync(
                    JsonSerializer.SerializeToElement(
                        {| event_id = "client_1"
                           ``type`` = "session.update" |}
                    ),
                    CancellationToken.None
                )
                .AsTask()

        do!
            waitForAsync "Expected client event to reach OpenAI data channel." (fun () ->
                openAi.SentClientEvents.Length = 1)

        Assert.Equal("session.update", openAi.SentClientEvents.Head.GetProperty("type").GetString())

        openAi.EmitReceived(
            JsonSerializer.SerializeToElement(
                {| event_id = "server_1"
                   ``type`` = "response.created" |}
            )
        )

        let! received = connection.receiver.ReadAsync(CancellationToken.None).AsTask()
        Assert.Equal("response.created", received.GetProperty("type").GetString())

        cts.Cancel()
        do! runTask
    }

[<Fact>]
let ``sip call bridge reports OpenAI codec rejection to host`` () =
    task {
        let realtimeSession = sipRealtimeSession "gpt-realtime-test"

        let orchestration =
            FakeSipVoiceOrchestration([ RequestSipRealtime realtimeSession ])

        let openAi =
            new FakeOpenAiRealtimeWebRtcSession(
                OpenAiCodecRejectedException "OpenAI rejected requested strict codec PCMU."
            )

        let bridge =
            createSipBridge (orchestration :> IVoiceOrchestration<_, _>) (openAi :> IOpenAiRealtimeWebRtcSession)

        do! bridge.RunAsync(sipCallContext PCMU, null, null, CancellationToken.None)

        let! message = readSipConnectionFailureAsync orchestration.Session
        Assert.Contains("strict codec", message, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``sip hosted service disabled listener does not bind`` () =
    task {
        let orchestration = FakeSipVoiceOrchestration([])
        let openAi = new FakeOpenAiRealtimeWebRtcSession()

        let bridge =
            createSipBridge (orchestration :> IVoiceOrchestration<_, _>) (openAi :> IOpenAiRealtimeWebRtcSession)

        use loggerFactory = sipLoggerFactory ()

        let service =
            new SipHostedService<TestToHost, TestFromHost>(
                sipOptions false 0,
                bridge,
                loggerFactory.CreateLogger<SipHostedService<TestToHost, TestFromHost>>(),
                loggerFactory
            )

        do! (service :> Microsoft.Extensions.Hosting.IHostedService).StartAsync CancellationToken.None
        Assert.False service.IsListening
        do! (service :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
        Assert.False service.IsListening
    }

[<Fact>]
let ``sip hosted service enabled listener starts and stops`` () =
    task {
        let orchestration = FakeSipVoiceOrchestration([])
        let openAi = new FakeOpenAiRealtimeWebRtcSession()

        let bridge =
            createSipBridge (orchestration :> IVoiceOrchestration<_, _>) (openAi :> IOpenAiRealtimeWebRtcSession)

        use loggerFactory = sipLoggerFactory ()

        let service =
            new SipHostedService<TestToHost, TestFromHost>(
                sipOptions true 0,
                bridge,
                loggerFactory.CreateLogger<SipHostedService<TestToHost, TestFromHost>>(),
                loggerFactory
            )

        do! (service :> Microsoft.Extensions.Hosting.IHostedService).StartAsync CancellationToken.None
        Assert.True service.IsListening
        do! (service :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
        Assert.False service.IsListening
    }

[<Fact>]
let ``pure voice orchestration can use unit host messages`` () =
    task {
        let toHost = Channel.CreateUnbounded<unit>()

        let session =
            { new IVoiceSession<unit, unit> with
                member _.ToHost = toHost.Reader

                member _.SendFromHostAsync(_, _) = Task.CompletedTask

                member _.StartAsync _ = Task.CompletedTask

                member _.StopAsync _ =
                    toHost.Writer.TryComplete() |> ignore
                    Task.CompletedTask

                member _.DisposeAsync() =
                    toHost.Writer.TryComplete() |> ignore
                    ValueTask() }

        do! session.StartAsync CancellationToken.None
        do! session.SendFromHostAsync((), CancellationToken.None)
        do! session.StopAsync CancellationToken.None

        let! canRead = session.ToHost.WaitToReadAsync(CancellationToken.None).AsTask()
        Assert.False canRead
    }

[<Fact>]
let ``FsVoice Platform public contract stays dependency light`` () =
    let assembly = typeof<IVoiceOrchestration<unit, unit>>.Assembly
    let references = assembly.GetReferencedAssemblies() |> Array.map _.Name
    let publicApi = assembly.GetExportedTypes() |> Array.map _.FullName

    Assert.DoesNotContain("RTFlow", references)
    Assert.DoesNotContain("FsVoice.Ctx.Contracts", references)
    Assert.DoesNotContain("FsVoice.Retrieval", references)
    Assert.DoesNotContain("Microsoft.Maui", references)
    Assert.DoesNotContain("OpenAI", references)

    Assert.DoesNotContain(
        publicApi,
        fun name -> not (isNull name) && name.Contains("RTFlow", StringComparison.OrdinalIgnoreCase)
    )

let private demoVoiceConnectionWithChannels () =
    let inbound = Channel.CreateUnbounded<JsonElement>()
    let outbound = Channel.CreateUnbounded<JsonElement>()

    { VoiceConnection.receiver = inbound.Reader
      sender = outbound.Writer },
    inbound,
    outbound

let private demoVoiceConnection () =
    let connection, _, _ = demoVoiceConnectionWithChannels ()
    connection

let private writeServerEvent<'T> (writer: ChannelWriter<JsonElement>) (event: 'T) =
    task {
        let json = JsonSerializer.Serialize(event, SerDe.serOpts)
        use document = JsonDocument.Parse(json)
        do! writer.WriteAsync(document.RootElement.Clone(), CancellationToken.None).AsTask()
    }

let private readClientEventUntil (reader: ChannelReader<JsonElement>) predicate =
    task {
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let mutable result: JsonElement option = None

        while result.IsNone do
            let! message = reader.ReadAsync(cts.Token).AsTask()

            if predicate message then
                result <- Some message

        return result.Value
    }

let private tryReadClientEventUntil (reader: ChannelReader<JsonElement>) (timeout: TimeSpan) predicate =
    task {
        use cts = new CancellationTokenSource(timeout)
        let mutable result: JsonElement option = None

        try
            while result.IsNone do
                let! message = reader.ReadAsync(cts.Token).AsTask()

                if predicate message then
                    result <- Some message
        with
        | :? OperationCanceledException
        | :? ChannelClosedException -> ()

        return result
    }

let private hasEventType expected (message: JsonElement) =
    let mutable typeProperty = Unchecked.defaultof<JsonElement>

    message.TryGetProperty("type", &typeProperty)
    && typeProperty.ValueKind = JsonValueKind.String
    && typeProperty.GetString() = expected

let private tryProperty (name: string) (element: JsonElement) =
    let mutable property = Unchecked.defaultof<JsonElement>

    if
        element.ValueKind = JsonValueKind.Object
        && element.TryGetProperty(name, &property)
    then
        Some property
    else
        None

let private trySessionTurnDetection (message: JsonElement) =
    message
    |> tryProperty "session"
    |> Option.bind (tryProperty "audio")
    |> Option.bind (tryProperty "input")
    |> Option.bind (tryProperty "turn_detection")

let private trySessionTurnDetectionType message =
    message
    |> trySessionTurnDetection
    |> Option.bind (tryProperty "type")
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then
            Some(value.GetString())
        else
            None)

let private isSessionUpdateWithTurnDetection vadType message =
    hasEventType "session.update" message
    && trySessionTurnDetectionType message = Some vadType

let private trySessionTurnDetectionInterrupt message =
    message
    |> trySessionTurnDetection
    |> Option.bind (tryProperty "interrupt_response")
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False then
            Some(value.GetBoolean())
        else
            None)

let private isSessionUpdateWithServerVadInterrupt interruptResponse message =
    isSessionUpdateWithTurnDetection "server_vad" message
    && trySessionTurnDetectionInterrupt message = Some interruptResponse

let private demoVoiceContext () : VoiceOrchestrationContext =
    let storageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory storageRoot |> ignore

    { storageRoot = storageRoot
      settings = Map.empty
      report = ignore }

let private demoVoiceContextForStorage storageRoot : VoiceOrchestrationContext =
    Directory.CreateDirectory storageRoot |> ignore

    { storageRoot = storageRoot
      settings = Map.empty
      report = ignore }

let private demoRuntimeSettings values =
    let settings = Speak2Docs.RuntimeSettings.empty ()
    let defaults = [ Speak2Docs.RuntimeSettings.OpenAiKey, "test-api-key" ]
    Speak2Docs.RuntimeSettings.replace settings (Map.ofList (defaults @ values))
    settings

let private demoOrchestration settings =
    let qaPlugIn = FsVoice.Ctx.GenericQaPlugIn() :> IQaPlugIn

    let options: Speak2Docs.WorkFlow.DemoVoiceOrchestrationOptions =
        { settings = settings
          plugIn = PlugInDefinition.generic
          qaPlugIn = qaPlugIn
          retrievalMode = Speak2Docs.InternalDocumentIndex
          sources = [] }

    Speak2Docs.WorkFlow.DemoVoiceOrchestration(options)
    :> IVoiceOrchestration<Speak2Docs.WorkFlow.ToHost, Speak2Docs.WorkFlow.FromHost>

let private readDemoToHostUntil
    (session: IVoiceSession<Speak2Docs.WorkFlow.ToHost, Speak2Docs.WorkFlow.FromHost>)
    (predicate: Speak2Docs.WorkFlow.ToHost -> 'T option)
    =
    task {
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let mutable result: 'T option = None

        while result.IsNone do
            let! message = session.ToHost.ReadAsync(cts.Token).AsTask()

            match predicate message with
            | Some value -> result <- Some value
            | None -> ()

        return result.Value
    }

let private tryReadDemoToHostUntil
    (session: IVoiceSession<Speak2Docs.WorkFlow.ToHost, Speak2Docs.WorkFlow.FromHost>)
    (timeout: TimeSpan)
    (predicate: Speak2Docs.WorkFlow.ToHost -> 'T option)
    =
    task {
        use cts = new CancellationTokenSource(timeout)
        let mutable result: 'T option = None

        try
            while result.IsNone do
                let! message = session.ToHost.ReadAsync(cts.Token).AsTask()

                match predicate message with
                | Some value -> result <- Some value
                | None -> ()
        with
        | :? OperationCanceledException
        | :? ChannelClosedException -> ()

        return result
    }

let private responseWithId id =
    { Response.Default with
        id = Include(Some id) }

let private assertServerVadWithInterrupt interruptResponse turnDetection =
    match turnDetection with
    | Include(Some(VAD.Server_Vad turnDetection)) ->
        Assert.True turnDetection.create_response
        Assert.Equal(interruptResponse, turnDetection.interrupt_response)
        Assert.Equal(0.7, turnDetection.threshold)
        Assert.Equal(300, turnDetection.prefix_padding_ms)
        Assert.Equal(350, turnDetection.silence_duration_ms)
    | _ -> failwith "Expected realtime server VAD settings."

let private assertServerVad turnDetection =
    assertServerVadWithInterrupt true turnDetection

let private assertStartupServerVad turnDetection =
    assertServerVadWithInterrupt false turnDetection

let private assertRealtimeAudioSettings assertTurnDetection (requested: Session) =
    match requested.audio with
    | Include(Some audio) ->
        match audio.input with
        | Include(Some input) ->
            match input.noise_reduction with
            | Include(Some noiseReduction) -> Assert.Equal("near_field", noiseReduction.``type``)
            | _ -> failwith "Expected realtime input noise reduction."

            assertTurnDetection input.turn_detection
        | _ -> failwith "Expected realtime audio input settings."

        match audio.output with
        | Include(Some output) ->
            Assert.Equal("marin", output.voice)

            match output.speed with
            | Include(Some speed) -> Assert.Equal(1.0, speed)
            | _ -> failwith "Expected realtime output speed."
        | _ -> failwith "Expected realtime audio output settings."
    | _ -> failwith "Expected realtime audio settings."

[<Fact>]
let ``Speak2Docs orchestration has no UI or RTOpenAI Api references`` () =
    let references =
        typeof<Speak2Docs.WorkFlow.DemoVoiceOrchestration>.Assembly.GetReferencedAssemblies()
        |> Array.map _.Name
        |> Set.ofArray

    Assert.DoesNotContain("Fabulous", references)
    Assert.DoesNotContain("Microsoft.Maui", references)
    Assert.DoesNotContain("RTOpenAI.Api", references)

[<Fact>]
let ``demo orchestration start requests realtime connection`` () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings

        let! session =
            orchestration.CreateSessionAsync(demoVoiceContext (), demoVoiceConnection (), CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        let! requested =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection realtimeSession -> Some realtimeSession
                | _ -> None)

        Assert.True(requested.model |> Option.exists (String.IsNullOrWhiteSpace >> not))

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``Speak2Docs orchestration disables durable memory`` () =
    task {
        let storageRoot = tempStorageRoot ()

        let remember =
            { turnId = "turn_speak2docs_existing_memory"
              itemId = "item_speak2docs_existing_memory"
              revision = 1
              text = "Remember that Speak2Docs should not load this durable memory."
              isFinal = true
              receivedAt = DateTimeOffset.UtcNow }

        seedDurableMemory (DurableMemory.defaultPath storageRoot) remember "Noted."
        |> ignore

        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings

        let! session =
            orchestration.CreateSessionAsync(
                demoVoiceContextForStorage storageRoot,
                demoVoiceConnection (),
                CancellationToken.None
            )

        do! session.StartAsync CancellationToken.None

        let logs = ResizeArray<string>()
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let mutable configured = false

        while not configured do
            let! message = session.ToHost.ReadAsync(cts.Token).AsTask()

            match message with
            | Speak2Docs.WorkFlow.Log text ->
                logs.Add text

                if text.Contains("QA session configured", StringComparison.OrdinalIgnoreCase) then
                    configured <- true
            | _ -> ()

        Assert.DoesNotContain(
            logs,
            fun log -> log.Contains("durable memory record", StringComparison.OrdinalIgnoreCase)
        )

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration starts realtime audio with protected Server VAD`` () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings

        let! session =
            orchestration.CreateSessionAsync(demoVoiceContext (), demoVoiceConnection (), CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        let! requested =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection realtimeSession -> Some realtimeSession
                | _ -> None)

        assertRealtimeAudioSettings assertStartupServerVad requested

        do! session.StopAsync CancellationToken.None
    }

let private startProtectedStartupGreetingSession () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings
        let voiceConnection, serverEvents, clientEvents = demoVoiceConnectionWithChannels ()

        let! session = orchestration.CreateSessionAsync(demoVoiceContext (), voiceConnection, CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        let! requested =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection realtimeSession -> Some realtimeSession
                | _ -> None)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-created"
                  ``type`` = EventTypes.SessionCreated
                  session = requested }

        let! startupUpdate = readClientEventUntil clientEvents.Reader (isSessionUpdateWithServerVadInterrupt false)

        let startupTurnDetection =
            startupUpdate
            |> trySessionTurnDetection
            |> Option.defaultWith (fun () -> failwith "Missing VAD.")

        Assert.Equal(Some false, trySessionTurnDetectionInterrupt startupUpdate)
        Assert.Equal(JsonValueKind.Object, startupTurnDetection.ValueKind)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-updated"
                  ``type`` = EventTypes.SessionUpdated
                  session = requested }

        let! responseCreate = readClientEventUntil clientEvents.Reader (hasEventType "response.create")

        return session, serverEvents, clientEvents, responseCreate
    }

[<Fact>]
let ``demo orchestration startup greeting response is out of band`` () =
    task {
        let! session, _, _, responseCreate = startProtectedStartupGreetingSession ()
        let response = responseCreate.GetProperty("response")

        Assert.Equal("none", response.GetProperty("conversation").GetString())

        let metadata = response.GetProperty("metadata")
        Assert.Equal("startup_greeting", metadata.GetProperty("fsvoice_response_kind").GetString())

        Assert.Equal("none", response.GetProperty("tool_choice").GetString())
        Assert.Equal(JsonValueKind.Array, response.GetProperty("tools").ValueKind)
        Assert.Empty(response.GetProperty("tools").EnumerateArray())
        Assert.Contains("Greeting to speak exactly", response.GetProperty("instructions").GetString())

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration ignores transcript during protected startup greeting`` () =
    task {
        let! session, serverEvents, clientEvents, _ = startProtectedStartupGreetingSession ()

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-created"
                  ``type`` = EventTypes.ResponseCreated
                  response = responseWithId "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-speech-started"
                  ``type`` = EventTypes.InputAudioBufferSpeechStarted
                  audio_start_ms = 0
                  item_id = "item-1" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-transcript"
                  ``type`` = EventTypes.ConversationItemInputAudioTranscriptionCompleted
                  item_id = "item-1"
                  content_index = 0
                  transcript = " hello there "
                  usage = Usage.Default
                  logprobs = Skip }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-speech-stopped"
                  ``type`` = EventTypes.InputAudioBufferSpeechStopped
                  audio_end_ms = 1000
                  item_id = "item-1" }

        let! extraUpdate =
            tryReadClientEventUntil
                clientEvents.Reader
                (TimeSpan.FromMilliseconds 500.)
                (isSessionUpdateWithServerVadInterrupt true)

        Assert.True(extraUpdate.IsNone)

        let! transcript =
            tryReadDemoToHostUntil session (TimeSpan.FromMilliseconds 500.) (function
                | Speak2Docs.WorkFlow.TranscriptFinalized snapshot -> Some snapshot
                | _ -> None)

        Assert.True(transcript.IsNone)

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration enables interrupting Server VAD after protected greeting response done`` () =
    task {
        let! session, serverEvents, clientEvents, _ = startProtectedStartupGreetingSession ()

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-created"
                  ``type`` = EventTypes.ResponseCreated
                  response = responseWithId "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-done"
                  ``type`` = EventTypes.ResponseDone
                  response = responseWithId "resp-greeting" }

        let! serverUpdate = readClientEventUntil clientEvents.Reader (isSessionUpdateWithServerVadInterrupt true)

        let serverTurnDetection =
            serverUpdate
            |> trySessionTurnDetection
            |> Option.defaultWith (fun () -> failwith "Missing VAD.")

        Assert.Equal(0.7, serverTurnDetection.GetProperty("threshold").GetDouble())
        Assert.Equal(300, serverTurnDetection.GetProperty("prefix_padding_ms").GetInt32())
        Assert.Equal(350, serverTurnDetection.GetProperty("silence_duration_ms").GetInt32())
        Assert.True(serverTurnDetection.GetProperty("create_response").GetBoolean())
        Assert.True(serverTurnDetection.GetProperty("interrupt_response").GetBoolean())

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration waits for protected greeting output stopped before interruption`` () =
    task {
        let! session, serverEvents, clientEvents, _ = startProtectedStartupGreetingSession ()

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-created"
                  ``type`` = EventTypes.ResponseCreated
                  response = responseWithId "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-output-started"
                  ``type`` = EventTypes.OutputAudioBufferStarted
                  response_id = "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-done-before-output-stopped"
                  ``type`` = EventTypes.ResponseDone
                  response = responseWithId "resp-greeting" }

        let! earlyUpdate =
            tryReadClientEventUntil
                clientEvents.Reader
                (TimeSpan.FromMilliseconds 500.)
                (isSessionUpdateWithServerVadInterrupt true)

        Assert.True(earlyUpdate.IsNone)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-output-stopped"
                  ``type`` = EventTypes.OutputAudioBufferStopped
                  response_id = "resp-greeting" }

        let! serverUpdate = readClientEventUntil clientEvents.Reader (isSessionUpdateWithServerVadInterrupt true)

        let serverTurnDetection =
            serverUpdate
            |> trySessionTurnDetection
            |> Option.defaultWith (fun () -> failwith "Missing VAD.")

        Assert.True(serverTurnDetection.GetProperty("create_response").GetBoolean())
        Assert.True(serverTurnDetection.GetProperty("interrupt_response").GetBoolean())

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration accepts transcript after protected greeting completes`` () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings
        let voiceConnection, serverEvents, clientEvents = demoVoiceConnectionWithChannels ()

        let! session = orchestration.CreateSessionAsync(demoVoiceContext (), voiceConnection, CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        let! requested =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection realtimeSession -> Some realtimeSession
                | _ -> None)

        assertRealtimeAudioSettings assertStartupServerVad requested

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-created"
                  ``type`` = EventTypes.SessionCreated
                  session = requested }

        let! initialUpdate = readClientEventUntil clientEvents.Reader (isSessionUpdateWithServerVadInterrupt false)

        Assert.Equal(Some "server_vad", trySessionTurnDetectionType initialUpdate)
        Assert.Equal(Some false, trySessionTurnDetectionInterrupt initialUpdate)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-updated"
                  ``type`` = EventTypes.SessionUpdated
                  session = requested }

        let! _ = readClientEventUntil clientEvents.Reader (hasEventType "response.create")

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-response-created"
                  ``type`` = EventTypes.ResponseCreated
                  response = responseWithId "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-output-started"
                  ``type`` = EventTypes.OutputAudioBufferStarted
                  response_id = "resp-greeting" }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-output-stopped"
                  ``type`` = EventTypes.OutputAudioBufferStopped
                  response_id = "resp-greeting" }

        let! _ = readClientEventUntil clientEvents.Reader (isSessionUpdateWithServerVadInterrupt true)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-server-vad-active"
                  ``type`` = EventTypes.SessionUpdated
                  session = requested }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-transcript"
                  ``type`` = EventTypes.ConversationItemInputAudioTranscriptionCompleted
                  item_id = "item-1"
                  content_index = 0
                  transcript = " hello there "
                  usage = Usage.Default
                  logprobs = Skip }

        let! transcript =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.TranscriptFinalized snapshot -> Some snapshot
                | _ -> None)

        Assert.Equal("hello there", transcript.text)

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration greeting response does not create synthetic user input`` () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings
        let voiceConnection, serverEvents, clientEvents = demoVoiceConnectionWithChannels ()

        let! session = orchestration.CreateSessionAsync(demoVoiceContext (), voiceConnection, CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        let! requested =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection realtimeSession -> Some realtimeSession
                | _ -> None)

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-created"
                  ``type`` = EventTypes.SessionCreated
                  session = requested }

        do!
            writeServerEvent
                serverEvents.Writer
                { event_id = "test-session-updated"
                  ``type`` = EventTypes.SessionUpdated
                  session = requested }

        let! responseCreate = readClientEventUntil clientEvents.Reader (hasEventType "response.create")

        let response = responseCreate.GetProperty("response")
        let mutable inputProperty = Unchecked.defaultof<JsonElement>

        Assert.Equal("none", response.GetProperty("conversation").GetString())
        Assert.False(response.TryGetProperty("input", &inputProperty))

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration accepts source changes from host`` () =
    task {
        let settings = demoRuntimeSettings []
        let orchestration = demoOrchestration settings

        let! session =
            orchestration.CreateSessionAsync(demoVoiceContext (), demoVoiceConnection (), CancellationToken.None)

        do! session.StartAsync CancellationToken.None

        do!
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.RequestRealtimeConnection _ -> Some()
                | _ -> None)

        do!
            session.SendFromHostAsync(
                Speak2Docs.WorkFlow.SourcesChanged(Speak2Docs.FsColbertWithFallback, []),
                CancellationToken.None
            )

        let expectedMode =
            Speak2Docs.RetrievalModes.displayName Speak2Docs.FsColbertWithFallback

        let! logLine =
            readDemoToHostUntil session (function
                | Speak2Docs.WorkFlow.Log text when
                    text.Contains("Host sources changed") && text.Contains($"mode={expectedMode}")
                    ->
                    Some text
                | _ -> None)

        Assert.Contains(expectedMode, logLine)

        do! session.StopAsync CancellationToken.None
    }

[<Fact>]
let ``demo orchestration sees replaced runtime settings snapshots`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.UseLexicalFilter, "false" ]

    let first =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Speak2Docs.RuntimeSettings.replace settings (Map.ofList [ Speak2Docs.RuntimeSettings.UseLexicalFilter, "true" ])

    let second =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Assert.False first.useLexicalFilter
    Assert.True second.useLexicalFilter

[<Fact>]
let ``runtime settings apply answer model and loop settings`` () =
    let settings =
        demoRuntimeSettings
            [ Speak2Docs.RuntimeSettings.AnswerMaxOutputTokens, "2500"
              Speak2Docs.RuntimeSettings.AnswerReasoningEffort, "high"
              Speak2Docs.RuntimeSettings.AnswerToolCallLoopLimit, "5"
              Speak2Docs.RuntimeSettings.MaxContextChunks, "18" ]

    let plugIn =
        FsVoice.Ctx.PlugInDefinition.generic
        |> Speak2Docs.RuntimeSettings.composePlugIn
            Speak2Docs.InternalDocumentIndex
            (Speak2Docs.RuntimeSettings.snapshot settings)

    let answer = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Answer plugIn

    let flags =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Assert.Equal(2500, answer.maxOutputTokens |> Option.defaultValue 0)
    Assert.Equal(Some "high", answer.reasoningEffort)
    Assert.Equal(5, flags.answerToolCallLoopLimit)
    Assert.Equal(18, plugIn.runtime.memoryCandidateChunks)
    Assert.Equal(18, plugIn.runtime.maxContextChunks)

[<Fact>]
let ``runtime settings apply Speak2Docs realtime oracle timeout when plugin uses core default`` () =
    let settings = demoRuntimeSettings []

    let plugIn =
        FsVoice.Ctx.PlugInDefinition.generic
        |> Speak2Docs.RuntimeSettings.composePlugIn
            Speak2Docs.InternalDocumentIndex
            (Speak2Docs.RuntimeSettings.snapshot settings)

    Assert.Equal(
        Speak2Docs.RuntimeSettings.DefaultRealtimeOracleFunctionCallTimeoutMs,
        plugIn.runtime.functionCallTimeoutMs
    )

[<Fact>]
let ``Speak2Docs oracle answer transport defaults to persistent websocket`` () =
    Assert.Equal(
        FsVoice.Ctx.QaAnswerTransportMode.PersistentWebSocket,
        Speak2Docs.RuntimeSettings.DefaultOracleAnswerTransportMode
    )

[<Fact>]
let ``runtime settings preserve explicit plugin function call timeout`` () =
    let settings = demoRuntimeSettings []

    let definition =
        { FsVoice.Ctx.PlugInDefinition.generic with
            runtime =
                { FsVoice.Ctx.PlugInDefinition.generic.runtime with
                    functionCallTimeoutMs = 60000 } }

    let plugIn =
        definition
        |> Speak2Docs.RuntimeSettings.composePlugIn
            Speak2Docs.InternalDocumentIndex
            (Speak2Docs.RuntimeSettings.snapshot settings)

    Assert.Equal(60000, plugIn.runtime.functionCallTimeoutMs)

[<Fact>]
let ``runtime settings default answer reasoning effort is low`` () =
    let settings = demoRuntimeSettings []

    let plugIn =
        FsVoice.Ctx.PlugInDefinition.generic
        |> Speak2Docs.RuntimeSettings.composePlugIn
            Speak2Docs.InternalDocumentIndex
            (Speak2Docs.RuntimeSettings.snapshot settings)

    let answer = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Answer plugIn

    Assert.Equal(Some "low", answer.reasoningEffort)

[<Fact>]
let ``runtime settings default pdf visual descriptions off`` () =
    let settings = demoRuntimeSettings []

    let flags =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Assert.False flags.describePdfVisuals

[<Fact>]
let ``runtime settings force hybrid pdf parsing`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.UseHybridPdfParsing, "false" ]

    let flags =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Assert.True flags.useHybridPdfParsing

[<Fact>]
let ``runtime settings default audio speaker follows platform fallback`` () =
    let settings = demoRuntimeSettings []
    let values = Speak2Docs.RuntimeSettings.snapshot settings

    Assert.True(Speak2Docs.RuntimeSettings.audioDefaultToSpeaker true values)
    Assert.False(Speak2Docs.RuntimeSettings.audioDefaultToSpeaker false values)

[<Fact>]
let ``runtime settings default iOS audio route policy is speakerphone`` () =
    let settings = demoRuntimeSettings []
    let values = Speak2Docs.RuntimeSettings.snapshot settings

    Assert.Equal("speakerphone", Speak2Docs.RuntimeSettings.iosAudioRoutePolicy values)

[<Fact>]
let ``runtime settings carry audio default speaker flag`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.AudioDefaultToSpeaker, "true" ]

    let values = Speak2Docs.RuntimeSettings.snapshot settings

    Assert.True(Speak2Docs.RuntimeSettings.audioDefaultToSpeaker false values)

[<Fact>]
let ``runtime settings explicit receiver flag overrides speaker fallback`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.AudioDefaultToSpeaker, "false" ]

    let values = Speak2Docs.RuntimeSettings.snapshot settings

    Assert.False(Speak2Docs.RuntimeSettings.audioDefaultToSpeaker true values)

[<Fact>]
let ``runtime settings legacy iOS receiver route policy remains receiver`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.IosAudioRoutePolicy, "receiverOrHeadset" ]

    let values = Speak2Docs.RuntimeSettings.snapshot settings

    Assert.Equal("receiverOrHeadset", Speak2Docs.RuntimeSettings.iosAudioRoutePolicy values)
    Assert.False(Speak2Docs.RuntimeSettings.audioDefaultToSpeaker true values)

[<Fact>]
let ``runtime settings carry pdf visual description flag`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.DescribePdfVisuals, "true" ]

    let flags =
        Speak2Docs.RuntimeSettings.snapshot settings
        |> Speak2Docs.RuntimeSettings.sourceFlags

    Assert.True flags.describePdfVisuals

[<Fact>]
let ``runtime settings compose visual description model role override`` () =
    let visualModel = "gpt-visual-test"

    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.modelRoleKey FsVoice.Ctx.VisualDescription, visualModel ]

    let values = Speak2Docs.RuntimeSettings.snapshot settings
    let overrides = Speak2Docs.RuntimeSettings.modelRoleOverrides values

    let plugIn =
        FsVoice.Ctx.PlugInDefinition.generic
        |> Speak2Docs.RuntimeSettings.composePlugIn Speak2Docs.InternalDocumentIndex values

    let visualDescription =
        FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.VisualDescription plugIn

    Assert.Equal(visualModel, overrides[FsVoice.Ctx.VisualDescription])
    Assert.Equal(visualModel, visualDescription.modelId)
