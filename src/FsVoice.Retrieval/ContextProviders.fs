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
          keywordModelId = QaDefaults.nanoModel
          elaborateIndexKeywords = true
          pdfParsingMode = KnowledgeSources.PdfParsingMode.Hybrid
          pdfVisualDescriptionOptions = PdfVisualDescriptionOptions.disabled
          buildMissingIndexes = true
          logExpansions = false
          logChunks = false
          useLexicalFilter = true
          report = ignore }

type FsColbertContextProvider(options: FsColbertContextProviderOptions) =
    let mutable retrieval = KnowledgeSources.emptyIndex

    member _.Retrieval = retrieval

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
