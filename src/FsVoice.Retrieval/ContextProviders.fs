namespace FsVoice.Retrieval

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Ctx

type FsColbertContextProviderOptions =
    { storageRoot: string
      retrievalMode: RetrievalMode
      sources: KnowledgeSource list
      queryExpansionClient: IChatClient option
      keywordGenerationClient: IChatClient option
      disposeQueryExpansionClient: bool
      disposeKeywordGenerationClient: bool
      plugInProfile: QaPlugInProfile
      plugInFingerprint: string
      keywordModelId: string
      elaborateIndexKeywords: bool
      pdfParsingMode: KnowledgeSources.PdfParsingMode
      enableOpticalParsing: bool
      enableAutoOpticalParsing: bool
      pdfVisualDescriptionOptions: PdfVisualDescriptionOptions
      buildMissingIndexes: bool
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      report: string -> unit }

module FsColbertContextProviderOptions =
    let create storageRoot retrievalMode sources =
        { storageRoot = storageRoot
          retrievalMode = retrievalMode
          sources = sources
          queryExpansionClient = None
          keywordGenerationClient = None
          disposeQueryExpansionClient = false
          disposeKeywordGenerationClient = false
          plugInProfile = QaPlugInProfile.generic
          plugInFingerprint = ""
          keywordModelId = QaDefaults.keywordModel
          elaborateIndexKeywords = true
          pdfParsingMode = KnowledgeSources.PdfParsingMode.Hybrid
          enableOpticalParsing = false
          enableAutoOpticalParsing = true
          pdfVisualDescriptionOptions = PdfVisualDescriptionOptions.disabled
          buildMissingIndexes = true
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          report = ignore }

type FsColbertContextProvider(options: FsColbertContextProviderOptions) =
    let mutable retrieval = KnowledgeSources.emptyIndex

    member _.Retrieval = retrieval

    interface ISemanticIndexResourceProvider with
        member _.SemanticIndexResource = retrieval.encoder |> Option.map box

    interface IQaContextProvider with
        member _.ProviderId = "fsvoice.fscolbert"

        member _.DisplayName = RetrievalModes.displayName options.retrievalMode

        member _.Sources = retrieval.sources

        member _.LoadAsync cancellationToken =
            task {
                let sources = options.sources |> List.filter _.enabled

                options.report
                    $"Loading {sources.Length} knowledge source(s) with {RetrievalModes.displayName options.retrievalMode}."

                let loadWork =
                    match options.retrievalMode with
                    | InternalDocumentIndex -> KnowledgeSources.loadInternalIndex sources
                    | FsColbertWithFallback ->
                        let keywordOptions =
                            { KnowledgeSources.KeywordGenerationOptions.defaults with
                                enabled = options.elaborateIndexKeywords
                                client = options.keywordGenerationClient
                                modelId = options.keywordModelId
                                plugInProfile = options.plugInProfile
                                plugInFingerprint = options.plugInFingerprint }

                        KnowledgeSources.loadIndexWithOptions
                            options.storageRoot
                            options.report
                            keywordOptions
                            { parsingMode = options.pdfParsingMode
                              enableOpticalParsing = options.enableOpticalParsing
                              enableAutoOpticalParsing = options.enableAutoOpticalParsing
                              visualDescriptions = options.pdfVisualDescriptionOptions }
                            options.buildMissingIndexes
                            sources

                let! loaded, errors = Async.StartAsTask(loadWork, cancellationToken = cancellationToken)

                KnowledgeSources.disposeIndex retrieval
                retrieval <- loaded
                options.report $"Loaded {retrieval.chunks.Length} source chunk(s)."

                return errors
            }

        member _.RetrieveAsync(request, cancellationToken) =
            KnowledgeSources.rankWithProfile
                options.plugInProfile
                options.queryExpansionClient
                options.logExpansions
                options.logChunks
                options.useLexicalFilter
                options.report
                request.query
                request.maxResults
                retrieval
            |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

        member _.InventoryAsync _ =
            KnowledgeSources.renderInventory retrieval.sources |> Task.FromResult

        member _.DisposeAsync() =
            KnowledgeSources.disposeIndex retrieval

            if options.disposeQueryExpansionClient then
                options.queryExpansionClient |> Option.iter _.Dispose()

            if options.disposeKeywordGenerationClient then
                options.keywordGenerationClient |> Option.iter _.Dispose()

            ValueTask()

type ExternalFsColbertContextProviderOptions =
    { storageRoot: string
      bundleDirectory: string
      plugInProfile: QaPlugInProfile
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      report: string -> unit }

module ExternalFsColbertContextProviderOptions =
    let create storageRoot bundleDirectory =
        { storageRoot = storageRoot
          bundleDirectory = bundleDirectory
          plugInProfile = QaPlugInProfile.generic
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          report = ignore }

type ExternalFsColbertContextProvider(options: ExternalFsColbertContextProviderOptions) =
    let mutable retrieval = KnowledgeSources.emptyIndex
    let mutable bundleInfo: KnowledgeSources.ExternalIndexBundleInfo option = None

    member _.Retrieval = retrieval
    member _.BundleInfo = bundleInfo

    interface ISemanticIndexResourceProvider with
        member _.SemanticIndexResource = retrieval.encoder |> Option.map box

    interface IQaContextProvider with
        member _.ProviderId = "fsvoice.fscolbert.external"

        member _.DisplayName = "External FsColbert bundle"

        member _.Sources = retrieval.sources

        member _.LoadAsync cancellationToken =
            task {
                let! result =
                    KnowledgeSources.loadExternalIndexBundle options.storageRoot options.report options.bundleDirectory
                    |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

                KnowledgeSources.disposeIndex retrieval

                match result with
                | Error errors ->
                    retrieval <- KnowledgeSources.emptyIndex
                    bundleInfo <- None
                    return errors
                | Ok(loaded, info) ->
                    retrieval <- loaded
                    bundleInfo <- Some info
                    return []
            }

        member _.RetrieveAsync(request, cancellationToken) =
            KnowledgeSources.rankWithProfile
                options.plugInProfile
                None
                options.logExpansions
                options.logChunks
                options.useLexicalFilter
                options.report
                request.query
                request.maxResults
                retrieval
            |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

        member _.InventoryAsync _ =
            KnowledgeSources.renderInventory retrieval.sources |> Task.FromResult

        member _.DisposeAsync() =
            KnowledgeSources.disposeIndex retrieval
            retrieval <- KnowledgeSources.emptyIndex
            bundleInfo <- None
            ValueTask()

type FsColbertSourceIndexServiceOptions =
    { storageRoot: string
      queryExpansionClient: IChatClient option
      keywordGenerationClient: IChatClient option
      disposeQueryExpansionClient: bool
      disposeKeywordGenerationClient: bool
      plugInProfile: QaPlugInProfile
      plugInFingerprint: string
      keywordModelId: string
      elaborateIndexKeywords: bool
      pdfVisualDescriptionOptions: PdfVisualDescriptionOptions
      buildMissingIndexes: bool
      logExpansions: bool
      logChunks: bool
      useLexicalFilter: bool
      report: string -> unit }

module FsColbertSourceIndexServiceOptions =
    let create storageRoot =
        { storageRoot = storageRoot
          queryExpansionClient = None
          keywordGenerationClient = None
          disposeQueryExpansionClient = false
          disposeKeywordGenerationClient = false
          plugInProfile = QaPlugInProfile.generic
          plugInFingerprint = ""
          keywordModelId = QaDefaults.keywordModel
          elaborateIndexKeywords = true
          pdfVisualDescriptionOptions = PdfVisualDescriptionOptions.disabled
          buildMissingIndexes = true
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          report = ignore }

type FsColbertSourceIndexService(options: FsColbertSourceIndexServiceOptions) =
    let pdfOptions profile =
        KnowledgeSources.PdfIngestionOptions.fromSourceProfile options.pdfVisualDescriptionOptions profile

    let previewVector (vector: KnowledgeSources.IndexPreviewVectorSummary) : SourcePreviewVectorSummary =
        { tokenCount = vector.tokenCount
          embeddingDim = vector.embeddingDim
          valueSample = vector.valueSample }

    let previewRecord (record: KnowledgeSources.IndexPreviewRecord) : SourcePreviewRecord =
        { index = record.index
          sectionPath = record.sectionPath
          contentRole = record.contentRole
          pageNumbers = record.pageNumbers
          layoutLabels = record.layoutLabels
          captions = record.captions
          text = record.text
          keywords = record.keywords
          terms = record.terms
          vector = previewVector record.vector }

    let preview (preview: KnowledgeSources.IndexPreview) : SourcePreview =
        { source = preview.source
          totalChunks = preview.totalChunks
          sampledCount = preview.sampledCount
          records = preview.records |> List.map previewRecord }

    interface ISourceIndexService with
        member _.CreateContextProvider(profile, mode, sources) =
            let pdfOptions = pdfOptions profile

            let providerOptions =
                { FsColbertContextProviderOptions.create options.storageRoot mode sources with
                    queryExpansionClient = options.queryExpansionClient
                    keywordGenerationClient = options.keywordGenerationClient
                    disposeQueryExpansionClient = options.disposeQueryExpansionClient
                    disposeKeywordGenerationClient = options.disposeKeywordGenerationClient
                    plugInProfile = options.plugInProfile
                    plugInFingerprint = options.plugInFingerprint
                    keywordModelId = options.keywordModelId
                    elaborateIndexKeywords = options.elaborateIndexKeywords
                    pdfParsingMode = pdfOptions.parsingMode
                    enableOpticalParsing = pdfOptions.enableOpticalParsing
                    enableAutoOpticalParsing = pdfOptions.enableAutoOpticalParsing
                    pdfVisualDescriptionOptions = pdfOptions.visualDescriptions
                    buildMissingIndexes = options.buildMissingIndexes
                    logExpansions = options.logExpansions
                    logChunks = options.logChunks
                    useLexicalFilter = options.useLexicalFilter
                    report = options.report }

            new FsColbertContextProvider(providerOptions) :> IQaContextProvider

        member _.PreviewAsync(profile, source, maxRecords, cancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                return
                    KnowledgeSources.loadIndexPreviewWithOptions
                        options.storageRoot
                        options.report
                        (pdfOptions profile)
                        maxRecords
                        source
                    |> Result.map preview
            }

        member _.DeleteArtifactsAsync(source, cancellationToken) =
            task {
                try
                    let! result =
                        KnowledgeSources.clearPersistedIndexesForSource options.storageRoot source
                        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

                    return Ok result
                with ex ->
                    return Error ex
            }
