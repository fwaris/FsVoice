namespace Speak2Docs.WorkFlow

open System
open System.IO
open Microsoft.Extensions.AI
open Speak2Docs

module KnowledgeSources =
    type QueryType = FsVoice.Retrieval.KnowledgeSources.QueryType
    type Expansion = FsVoice.Retrieval.KnowledgeSources.Expansion
    type KeywordGenerationOptions = FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions
    type PdfVisualDescriptionOptions = FsVoice.Retrieval.PdfVisualDescriptionOptions
    type IndexPreviewVectorSummary = FsVoice.Retrieval.KnowledgeSources.IndexPreviewVectorSummary
    type IndexPreviewRecord = FsVoice.Retrieval.KnowledgeSources.IndexPreviewRecord
    type IndexPreview = FsVoice.Retrieval.KnowledgeSources.IndexPreview

    module KeywordGenerationOptions =
        let defaults = FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions.defaults
        let disabled = FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions.disabled

    module PdfVisualDescriptionOptions =
        let defaults = FsVoice.Retrieval.PdfVisualDescriptionOptions.defaults
        let disabled = FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled

    type RetrievalIndex = FsVoice.Retrieval.KnowledgeSources.RetrievalIndex

    let emptyIndex = FsVoice.Retrieval.KnowledgeSources.emptyIndex

    let disposeIndex (retrieval: RetrievalIndex) =
        FsVoice.Retrieval.KnowledgeSources.disposeIndex retrieval

    let private sourceKindFromDocument kind =
        match kind with
        | PdfFile -> FsVoice.Ctx.KnowledgeSourceKind.Pdf
        | MarkdownFile -> FsVoice.Ctx.KnowledgeSourceKind.Markdown
        | JsonFile -> FsVoice.Ctx.KnowledgeSourceKind.Json

    let fromPdfDocuments (docs: PdfDocumentSource list) =
        docs
        |> PdfDocuments.selectedReady
        |> List.map (fun doc ->
            ({ kind = sourceKindFromDocument doc.kind
               location = doc.storedPath
               enabled = true }
            : KnowledgeSource))

    let selectedSources (docs: PdfDocumentSource list) = fromPdfDocuments docs

    let loadChunks (sources: KnowledgeSource list) : Async<SourceChunk list * string list> =
        async { return! FsVoice.Retrieval.KnowledgeSources.loadChunks sources }

    let loadInternalIndex (sources: KnowledgeSource list) : Async<RetrievalIndex * string list> =
        async { return! FsVoice.Retrieval.KnowledgeSources.loadInternalIndex sources }

    let private defaultKeywordModel = FsVoice.Ctx.QaDefaults.keywordModel

    let private fsColbertRoot storageRoot =
        Path.Combine(storageRoot, "Speak2Docs", "FsColbert")

    let prebuiltFolder storageRoot =
        let path = Path.Combine(fsColbertRoot storageRoot, "Prebuilt")
        Directory.CreateDirectory path |> ignore
        path

    let prebuiltManifestPath storageRoot =
        Path.Combine(prebuiltFolder storageRoot, "prebuilt-indexes.installed.json")

    let clearPersistedIndexes storageRoot =
        FsVoice.Retrieval.KnowledgeSources.clearPersistedIndexes storageRoot

    let private createChatClient (key: string) (modelId: string) : IChatClient =
        let client = OpenAI.OpenAIClient(key)
        client.GetResponsesClient().AsIChatClient(modelId)

    let keywordOptionsFromApiKey enabled apiKey =
        if not enabled then
            KeywordGenerationOptions.disabled
        else
            apiKey
            |> Option.bind Text.notEmpty
            |> Option.map (fun key ->
                { KeywordGenerationOptions.defaults with
                    client = Some(createChatClient key defaultKeywordModel)
                    modelId = defaultKeywordModel })
            |> Option.defaultValue
                { KeywordGenerationOptions.defaults with
                    client = None
                    modelId = defaultKeywordModel }

    let private pdfParsingMode useHybridPdfParsing useLayoutAnalysis =
        if useHybridPdfParsing && useLayoutAnalysis then
            FsVoice.Retrieval.KnowledgeSources.PdfParsingMode.Hybrid
        else
            FsVoice.Retrieval.KnowledgeSources.PdfParsingMode.Legacy

    let private pdfIngestionOptions
        useHybridPdfParsing
        useLayoutAnalysis
        visualOptions
        : FsVoice.Retrieval.KnowledgeSources.PdfIngestionOptions =
        { parsingMode = pdfParsingMode useHybridPdfParsing useLayoutAnalysis
          visualDescriptions =
            if useHybridPdfParsing && useLayoutAnalysis then
                visualOptions
            else
                FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled }

    let configurePdfParserWithVisualOptions useLayoutAnalysis visualOptions =
        { FsVoice.Retrieval.DoclingHybrid.defaults with
            enableLayoutAnalysis = useLayoutAnalysis
            visualDescriptions =
                if useLayoutAnalysis then
                    visualOptions
                else
                    FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled }
        |> FsVoice.Retrieval.DoclingHybrid.setDefaultOptions

    let configurePdfParser useLayoutAnalysis =
        configurePdfParserWithVisualOptions useLayoutAnalysis FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled

    let InindexSourceWithVisualOptions
        storageRoot
        report
        keywordOptions
        visualOptions
        useHybridPdfParsing
        useLayoutAnalysis
        (source: KnowledgeSource)
        =
        configurePdfParserWithVisualOptions useLayoutAnalysis visualOptions

        source
        |> FsVoice.Retrieval.KnowledgeSources.InindexSourceWithOptions
            storageRoot
            report
            keywordOptions
            (pdfIngestionOptions useHybridPdfParsing useLayoutAnalysis visualOptions)

    let InindexSource storageRoot report keywordOptions useHybridPdfParsing useLayoutAnalysis source =
        InindexSourceWithVisualOptions
            storageRoot
            report
            keywordOptions
            FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled
            useHybridPdfParsing
            useLayoutAnalysis
            source

    let loadIndexWithVisualOptions
        storageRoot
        report
        (keywordOptions: KeywordGenerationOptions)
        visualOptions
        useHybridPdfParsing
        useLayoutAnalysis
        (sources: KnowledgeSource list)
        =
        async {
            configurePdfParserWithVisualOptions useLayoutAnalysis visualOptions

            let! index, errors =
                sources
                |> FsVoice.Retrieval.KnowledgeSources.loadIndexWithOptions
                    storageRoot
                    report
                    keywordOptions
                    (pdfIngestionOptions useHybridPdfParsing useLayoutAnalysis visualOptions)
                    false

            return index, errors
        }

    let loadIndex storageRoot report keywordOptions useHybridPdfParsing useLayoutAnalysis sources =
        loadIndexWithVisualOptions
            storageRoot
            report
            keywordOptions
            FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled
            useHybridPdfParsing
            useLayoutAnalysis
            sources

    let loadIndexPreviewWithVisualOptions
        storageRoot
        report
        visualOptions
        useHybridPdfParsing
        useLayoutAnalysis
        maxRecords
        source
        =
        source
        |> FsVoice.Retrieval.KnowledgeSources.loadIndexPreviewWithOptions
            storageRoot
            report
            (pdfIngestionOptions useHybridPdfParsing useLayoutAnalysis visualOptions)
            maxRecords

    let loadIndexPreview storageRoot report useHybridPdfParsing useLayoutAnalysis maxRecords source =
        loadIndexPreviewWithVisualOptions
            storageRoot
            report
            FsVoice.Retrieval.PdfVisualDescriptionOptions.disabled
            useHybridPdfParsing
            useLayoutAnalysis
            maxRecords
            source

    let rank
        (apiKey: string option)
        (logExpansions: bool)
        (logChunks: bool)
        (useLexicalFilter: bool)
        (report: string -> unit)
        (query: string)
        (maxResults: int)
        (retrieval: RetrievalIndex)
        : Async<SourceChunk list> =
        async {
            match apiKey |> Option.bind Text.notEmpty, useLexicalFilter with
            | Some key, true ->
                use client = createChatClient key defaultKeywordModel

                let! chunks =
                    FsVoice.Retrieval.KnowledgeSources.rank
                        (Some client)
                        logExpansions
                        logChunks
                        useLexicalFilter
                        report
                        query
                        maxResults
                        retrieval

                return chunks
            | _ ->
                let! chunks =
                    FsVoice.Retrieval.KnowledgeSources.rank
                        None
                        logExpansions
                        logChunks
                        useLexicalFilter
                        report
                        query
                        maxResults
                        retrieval

                return chunks
        }

    let renderContext (chunks: SourceChunk list) =
        FsVoice.Retrieval.KnowledgeSources.renderContext chunks

    let renderInventory (sources: KnowledgeSource list) =
        FsVoice.Retrieval.KnowledgeSources.renderInventory sources
