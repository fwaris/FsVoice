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
open FSharp.Control
open FsVoice
open FsVoice.Core
open FsVoice.Hosting.AspNetCore
open FsVoice.QA
open FsVoice.Testing
open FsVoice.Types
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

type EchoVoiceTool(pluginId: VoicePluginId) =
    let toolId =
        { pluginId = pluginId
          name = VoiceToolName.create "echo" }

    interface IVoiceTool with
        member _.Definition =
            { id = toolId
              description = "Echoes the input text."
              parameters =
                [ { name = "text"
                    description = "Text to echo."
                    required = true } ]
              inputSchema = None
              timeout = None }

        member _.InvokeAsync(call, _) =
            task {
                let text =
                    match call.arguments.TryGetProperty "text" with
                    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                    | _ -> ""
                    |> Option.ofObj
                    |> Option.defaultValue ""

                return
                    Ok
                        { callId = call.callId
                          toolId = toolId
                          content = JsonSerializer.SerializeToElement {| echoed = text |}
                          metadata = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
                          completedAt = DateTimeOffset.UtcNow }
            }

type FakeVoicePlugin() =
    let pluginId = VoicePluginId.create "fake"

    interface IVoicePlugin with
        member _.ContractVersion = 1
        member _.PluginId = pluginId

        member _.Definition =
            { id = "fake"
              version = "0.1.0"
              displayName = "Fake Plugin"
              description = None
              prompts = Map.empty
              settings = Map.empty }

        member _.GetTools _ =
            [ EchoVoiceTool(pluginId) :> IVoiceTool ]

        member _.GetAgents _ = []

let private voiceHostContext () : VoicePluginHostContext =
    { storageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      settings = Map.empty
      report = ignore }

type FakeContextProvider(source: KnowledgeSource) =
    interface IQaContextProvider with
        member _.ProviderId = "fake.context"
        member _.DisplayName = "Fake Context"
        member _.Sources = [ source ]

        member _.LoadAsync _ = Task.FromResult([])

        member _.RetrieveAsync(request, _) =
            [ { source = source
                index = 0
                text = $"Fake context for {request.query}."
                score = 1.0f } ]
            |> Task.FromResult

        member _.InventoryAsync _ =
            Task.FromResult("Fake Context inventory.")

        member _.DisposeAsync() = ValueTask()

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
let ``generic PlugIn supplies model roles and runtime defaults`` () =
    let plugin = new GenericQaPlugIn() :> IQaPlugIn
    let definition = plugin.Definition |> PlugInDefinition.sanitize

    Assert.Equal(PlugInDefinition.currentContractVersion, plugin.ContractVersion)
    Assert.Equal("generic", definition.id)
    Assert.Equal("gpt-realtime-2", (PlugInDefinition.model Realtime definition).modelId)
    Assert.Equal("gpt-5.5", (PlugInDefinition.model Answer definition).modelId)
    Assert.Equal("gpt-5-nano", (PlugInDefinition.model Keyword definition).modelId)
    Assert.True(definition.runtime.enableToolPlanner)
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

[<Fact>]
let ``qa session applies custom prompts and answer role options`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let recorder = new RecordingChatClient("custom response")

        let answerConfig =
            { ModelRoleConfig.create "gpt-4.1-mini" with
                maxOutputTokens = Some 123
                temperature = Some 0.1f }

        let options =
            { QaSessionOptions.create (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                clients =
                    { QaModelClients.none with
                        answerGenerator = Some recorder }
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                prompts =
                    { PromptSet.empty with
                        answerSystem = Some "CUSTOM SYSTEM"
                        answerUserTemplate = Some "Q={{question}}\nCTX={{sourceContext}}\nINV={{sourceInventory}}" } }

        use session = new QaSession(options)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("custom response", answer.answer)
        Assert.Equal(123, recorder.MaxOutputTokens.Value)
        Assert.Equal(0.1f, recorder.Temperature.Value)
        Assert.Equal("CUSTOM SYSTEM", recorder.Messages[0].Text)
        Assert.Contains("Q=What does the fake context say?", recorder.Messages[1].Text)
        Assert.Contains("Fake context for What does the fake context say?", recorder.Messages[1].Text)
    }

[<Fact>]
let ``qa session retries empty answer response with larger output budget`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let client = new SequencedChatClient([ ""; "retry answer" ])
        let logs = ResizeArray<string>()

        let answerConfig =
            { ModelRoleConfig.create "gpt-5.5" with
                maxOutputTokens = Some 50 }

        let options =
            { QaSessionOptions.create (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                clients =
                    { QaModelClients.none with
                        answerGenerator = Some client }
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "What does the fake context say?"
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal("retry answer", answer.answer)
        Assert.Equal(2, client.Calls.Length)
        Assert.Equal(50, client.Calls.Head.Value)
        Assert.True(client.Calls[1].Value >= 1200)
        Assert.Contains(logs, fun log -> log.Contains("Answer model returned empty text"))
        Assert.Contains(logs, fun log -> log.Contains("Answer model retry succeeded"))
    }

[<Fact>]
let ``qa session reports token limit when answer response is length finished`` () =
    task {
        let source =
            { kind = Json
              location = "memory://fake"
              enabled = true }

        let client = new LengthFinishChatClient("partial answer")
        let logs = ResizeArray<string>()

        let answerConfig =
            { ModelRoleConfig.create "gpt-5.5" with
                maxOutputTokens = Some 321 }

        let options =
            { QaSessionOptions.create (tempStorageRoot ()) with
                autoWriteback = false
                contextProviders = [ FakeContextProvider(source) ]
                clients =
                    { QaModelClients.none with
                        answerGenerator = Some client }
                answerModelId = answerConfig.modelId
                modelRoles = PlugInDefinition.defaultModels |> Map.add Answer answerConfig
                report = fun msg -> logs.Add msg }

        use session = new QaSession(options)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "Give me a long answer."
              realtimeJudgement = None
              deadline = None }

        let! answer = session.AnswerAsync(request, CancellationToken.None)

        Assert.Contains("max answer token limit of 321", answer.answer)
        Assert.Contains("Disconnect, open Settings, increase Max Answer Tokens", answer.answer)
        Assert.Equal(321, client.MaxOutputTokens.Value)
        Assert.Contains(logs, fun log -> log.Contains("Answer model hit output token limit"))
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

    let generic = Text.QueryPostProcessing.forVoiceLikeRetrieval "medicare gap"

    let profiled =
        Text.QueryPostProcessing.forVoiceLikeRetrievalWithProfile profile "medicare gap"

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
    Assert.Contains("pdfParserQuality=layout-sparse-fallback-v1", hybrid)
    Assert.False(String.Equals(hybrid, withoutLayout, StringComparison.Ordinal))

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
            { QaSessionOptions.create storageRoot with
                autoWriteback = false }

        let session = new QaSession(options)
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
let ``qa session skips llm tool planner when only built-in context tools are available`` () =
    task {
        let source =
            { kind = Json
              location = "fake://source"
              enabled = true }

        let planner = new CountingChatClient("""{"calls":[]}""")
        let provider = FakeContextProvider source :> IQaContextProvider
        let storageRoot = tempStorageRoot ()

        let options =
            { QaSessionOptions.create storageRoot with
                autoWriteback = false
                clients =
                    { QaModelClients.none with
                        toolPlanner = Some planner } }

        let session = new QaSession(options)
        let! _ = (session :> IQaOrchestrator).ConfigureAsync([ provider ], CancellationToken.None)

        let request =
            { turnId = Guid.NewGuid().ToString("N")
              question = "composition"
              realtimeJudgement = None
              deadline = None }

        let! _ = session.AnswerAsync(request, CancellationToken.None)

        Assert.Equal(0, planner.Count)

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
            { QaSessionOptions.create (tempStorageRoot ()) with
                autoWriteback = false
                clients =
                    { QaModelClients.none with
                        queryExpansion = Some expansion } }

        use session = new QaSession(options)

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
        System.IO.Path.GetDirectoryName(typeof<FsVoice.Tools.CurrentTimeToolProvider>.Assembly.Location)

    let catalog = QaToolLoader.load (FakeQaToolHost()) (Some providerFolder)

    let hasCurrentTimeTool =
        catalog.tools
        |> List.exists (fun tool -> tool.PluginName = "FsVoiceTools" && tool.Name = "current_time")

    Assert.True(hasCurrentTimeTool)

[<Fact>]
let ``rank promotes exact section target over lexical distractors`` () =
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
                        text = "Section: Notes\nThis appendix mentions abstract abstract abstract."
                        score = 0.0f }
                      { source = source
                        index = 2
                        text = "Section: ABSTRACT\nThis paper introduces a retrieval method."
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
                        text =
                          "The NeurIPS checklist asks whether claims made in the abstract and introduction reflect the paper. The paper abstract should summarize contributions."
                        score = 0.0f }
                      { source = source
                        index = 2
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
                        text = "Section: Notes\nThis appendix mentions abstract abstract abstract."
                        score = 0.0f }
                      { source = source
                        index = 2
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
                        text = "Latency latency latency for source A."
                        score = 0.0f }
                      { source = sourceA
                        index = 2
                        text = "Latency latency latency architecture in source A."
                        score = 0.0f }
                      { source = sourceB
                        index = 1
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
                        text = "Quantum scheduling methods are introduced here."
                        score = 0.0f }
                      { source = sourceB
                        index = 1
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
let ``voice event bus preserves publish order`` () =
    let bus = VoiceEventBus()
    let seen = ResizeArray<string>()

    use _subscription = bus.Subscribe(fun event -> seen.Add event.name)

    let publisher = bus :> IVoiceEventPublisher
    publisher.Publish(VoiceEvents.create "session.started" (Some "s1") None None)
    publisher.Publish(VoiceEvents.create "turn.completed" (Some "s1") None None)

    Assert.Equal<string list>([ "session.started"; "turn.completed" ], List.ofSeq seen)
    Assert.Equal<string list>([ "session.started"; "turn.completed" ], bus.Events |> List.map _.name)

[<Fact>]
let ``voice tool dispatcher records successful result shape`` () =
    task {
        let pluginId = VoicePluginId.create "fake"
        let dispatcher = VoiceToolDispatcher([ EchoVoiceTool(pluginId) :> IVoiceTool ])

        let call =
            { callId = "call_1"
              toolId =
                { pluginId = pluginId
                  name = VoiceToolName.create "echo" }
              arguments = JsonSerializer.SerializeToElement {| text = "hello" |}
              requestedAt = DateTimeOffset.UtcNow }

        let! result = dispatcher.DispatchAsync(call, CancellationToken.None)

        match result with
        | ToolSucceeded toolResult ->
            Assert.Equal("call_1", toolResult.callId)
            Assert.Equal("hello", toolResult.content.GetProperty("echoed").GetString())
        | _ -> failwith "Expected tool dispatch to succeed."
    }

[<Fact>]
let ``voice runtime publishes session and tool events`` () =
    task {
        use transport = new FakeVoiceTransport([])
        let engine = VoiceRuntimeEngine(FakeVoicePlugin(), voiceHostContext (), transport)

        let pluginId = VoicePluginId.create "fake"

        let call =
            { callId = "call_2"
              toolId =
                { pluginId = pluginId
                  name = VoiceToolName.create "echo" }
              arguments = JsonSerializer.SerializeToElement {| text = "runtime" |}
              requestedAt = DateTimeOffset.UtcNow }

        do! engine.StartAsync CancellationToken.None
        let! result = engine.DispatchToolAsync(call, CancellationToken.None)
        do! engine.StopAsync CancellationToken.None

        match result with
        | ToolSucceeded _ -> ()
        | _ -> failwith "Expected runtime tool dispatch to succeed."

        let eventNames = engine.Events |> List.map _.name

        Assert.Contains("session.started", eventNames)
        Assert.Contains("tool.started", eventNames)
        Assert.Contains("tool.completed", eventNames)
        Assert.Contains("session.ended", eventNames)

        let observation = Assert.Single engine.Blackboard.toolObservations
        Assert.Equal("fake.echo", observation.toolName)
    }

[<Fact>]
let ``text runtime captures submitted user turn`` () =
    task {
        let runtime = VoiceTextRuntime(FakeVoicePlugin(), voiceHostContext ())
        let! snapshot = runtime.SubmitAsync("what changed?", CancellationToken.None)

        let turn = Assert.Single snapshot.turns

        Assert.Equal("user", turn.role)
        Assert.Equal("what changed?", turn.text)
    }

[<Fact>]
let ``bridge session logs browser and webrtc events`` () =
    task {
        let sessionId = BridgeSessionId.newId ()

        let options =
            { sessionId = sessionId
              plugin = FakeVoicePlugin()
              hostContext = voiceHostContext ()
              runtimeOptions = None }

        use session = new BridgeSession(options)
        do! session.StartAsync CancellationToken.None

        do!
            session.AcceptClientEventAsync(
                { eventId = "browser_1"
                  kind = BrowserEvent
                  eventType = "connected"
                  payload = JsonSerializer.SerializeToElement {| tab = "agent" |} |> Some
                  receivedAt = DateTimeOffset.UtcNow },
                CancellationToken.None
            )

        do!
            session.AcceptClientEventAsync(
                { eventId = "webrtc_1"
                  kind = WebRtcSignal
                  eventType = "offer"
                  payload = JsonSerializer.SerializeToElement {| sdp = "fake" |} |> Some
                  receivedAt = DateTimeOffset.UtcNow },
                CancellationToken.None
            )

        let eventTypes = session.SnapshotEvents() |> List.map _.eventType

        Assert.Contains("session.started", eventTypes)
        Assert.Contains("browser.connected", eventTypes)
        Assert.Contains("webrtc.offer", eventTypes)
    }

[<Fact>]
let ``bridge session forwards realtime server events into runtime event log`` () =
    task {
        let sessionId = BridgeSessionId.newId ()

        let options =
            { sessionId = sessionId
              plugin = FakeVoicePlugin()
              hostContext = voiceHostContext ()
              runtimeOptions = None }

        use session = new BridgeSession(options)
        do! session.StartAsync CancellationToken.None

        let runTask = session.Engine.RunUntilClosedAsync(CancellationToken.None)

        do!
            session.AcceptClientEventAsync(
                { eventId = "server_1"
                  kind = RealtimeServerEvent
                  eventType = "speech.started"
                  payload = None
                  receivedAt = DateTimeOffset.UtcNow },
                CancellationToken.None
            )

        use waitCts = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)
        let mutable observedSpeech = false

        while not observedSpeech && not waitCts.IsCancellationRequested do
            let! event = session.WaitForServerEventAsync waitCts.Token

            observedSpeech <-
                match event with
                | Some event -> event.eventType = "speech.started"
                | None -> false

        do! (session :> IAsyncDisposable).DisposeAsync().AsTask()

        try
            do! runTask
        with :? OperationCanceledException ->
            ()

        let eventTypes = session.SnapshotEvents() |> List.map _.eventType
        Assert.True(observedSpeech)
        Assert.Contains("speech.started", eventTypes)
    }

[<Fact>]
let ``bridge session store creates and removes sessions`` () =
    task {
        let store = BridgeSessionStore()
        let sessionId = BridgeSessionId.newId ()

        let options =
            { sessionId = sessionId
              plugin = FakeVoicePlugin()
              hostContext = voiceHostContext ()
              runtimeOptions = None }

        let! session = store.CreateAsync(options, CancellationToken.None)

        Assert.Equal(1, store.Count)
        Assert.True(store.TryGet(session.SessionId).IsSome)

        do! store.RemoveAsync session.SessionId

        Assert.Equal(0, store.Count)
        Assert.True(store.TryGet(session.SessionId).IsNone)
    }

type TestToHost =
    | ShowThing of string
    | VoiceActivityChanged of bool

type TestFromHost =
    | ScreenShown of string
    | ProjectionCompleted of string

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

let private typedVoiceConnection () =
    let inbound = Channel.CreateUnbounded<JsonElement>()
    let outbound = Channel.CreateUnbounded<JsonElement>()

    { VoiceConnection.receiver = inbound.Reader
      sender = outbound.Writer }

let private typedVoiceContext () : VoiceOrchestrationContext =
    { storageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
      settings = Map.empty
      report = ignore }

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
let ``FsVoice Types public contract does not reference RTFlow`` () =
    let assembly = typeof<IVoiceOrchestration<unit, unit>>.Assembly
    let references = assembly.GetReferencedAssemblies() |> Array.map _.Name
    let publicApi = assembly.GetExportedTypes() |> Array.map _.FullName

    Assert.DoesNotContain("RTFlow", references)

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

let private hasEventType expected (message: JsonElement) =
    let mutable typeProperty = Unchecked.defaultof<JsonElement>

    message.TryGetProperty("type", &typeProperty)
    && typeProperty.ValueKind = JsonValueKind.String
    && typeProperty.GetString() = expected

let private demoVoiceContext () : VoiceOrchestrationContext =
    let storageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory storageRoot |> ignore

    { storageRoot = storageRoot
      settings = Map.empty
      report = ignore }

let private demoRuntimeSettings values =
    let settings = Speak2Docs.RuntimeSettings.empty ()
    Speak2Docs.RuntimeSettings.replace settings (Map.ofList values)
    settings

let private demoOrchestration settings =
    let qaPlugIn = FsVoice.QA.GenericQaPlugIn() :> IQaPlugIn

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
let ``demo orchestration configures realtime audio for iOS speakerphone barge-in`` () =
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

        match requested.audio with
        | Include(Some audio) ->
            match audio.input with
            | Include(Some input) ->
                match input.noise_reduction with
                | Include(Some noiseReduction) -> Assert.Equal("far_field", noiseReduction.``type``)
                | _ -> failwith "Expected realtime input noise reduction."

                match input.turn_detection with
                | Include(Some(VAD.Server_Vad turnDetection)) ->
                    Assert.True turnDetection.create_response
                    Assert.True turnDetection.interrupt_response
                    Assert.Equal(0.7, turnDetection.threshold)
                    Assert.Equal(300, turnDetection.prefix_padding_ms)
                    Assert.Equal(350, turnDetection.silence_duration_ms)
                | _ -> failwith "Expected realtime server VAD settings."
            | _ -> failwith "Expected realtime audio input settings."
        | _ -> failwith "Expected realtime audio settings."

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
let ``runtime settings apply answer max output tokens to answer model`` () =
    let settings =
        demoRuntimeSettings [ Speak2Docs.RuntimeSettings.AnswerMaxOutputTokens, "2500" ]

    let plugIn =
        FsVoice.QA.PlugInDefinition.generic
        |> Speak2Docs.RuntimeSettings.composePlugIn
            Speak2Docs.InternalDocumentIndex
            (Speak2Docs.RuntimeSettings.snapshot settings)

    let answer = FsVoice.QA.PlugInDefinition.model FsVoice.QA.Answer plugIn

    Assert.Equal(2500, answer.maxOutputTokens |> Option.defaultValue 0)
