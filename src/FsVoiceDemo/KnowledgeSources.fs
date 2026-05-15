namespace FsVoiceDemo.WorkFlow

open System
open System.IO
open Microsoft.Extensions.AI
open Microsoft.Maui.Storage
open FsVoiceDemo

module KnowledgeSources =
    type QueryType = FsVoice.QA.KnowledgeSources.QueryType
    type Expansion = FsVoice.QA.KnowledgeSources.Expansion
    type KeywordGenerationOptions = FsVoice.QA.KnowledgeSources.KeywordGenerationOptions

    module KeywordGenerationOptions =
        let defaults = FsVoice.QA.KnowledgeSources.KeywordGenerationOptions.defaults
        let disabled = FsVoice.QA.KnowledgeSources.KeywordGenerationOptions.disabled

    type RetrievalIndex =
        { qa: FsVoice.QA.KnowledgeSources.RetrievalIndex
          sources: KnowledgeSource list
          chunks: SourceChunk list
          colbertIndices: (KnowledgeSource * FsColbert.ColbertIndex) list
          encoder: FsColbert.OnnxColbertEncoder option }

    let private toQaKind =
        function
        | Pdf -> FsVoice.QA.KnowledgeSourceKind.Pdf
        | Markdown -> FsVoice.QA.KnowledgeSourceKind.Markdown
        | Json -> FsVoice.QA.KnowledgeSourceKind.Json

    let private fromQaKind =
        function
        | FsVoice.QA.KnowledgeSourceKind.Pdf -> Pdf
        | FsVoice.QA.KnowledgeSourceKind.Markdown -> Markdown
        | FsVoice.QA.KnowledgeSourceKind.Json -> Json

    let private toQaSource (source: KnowledgeSource) : FsVoice.QA.KnowledgeSource =
        { kind = toQaKind source.kind
          location = source.location
          enabled = source.enabled }

    let private fromQaSource (source: FsVoice.QA.KnowledgeSource) : KnowledgeSource =
        { kind = fromQaKind source.kind
          location = source.location
          enabled = source.enabled }

    let private toQaChunk (chunk: SourceChunk) : FsVoice.QA.SourceChunk =
        { source = toQaSource chunk.source
          index = chunk.index
          text = chunk.text
          score = chunk.score }

    let private fromQaChunk (chunk: FsVoice.QA.SourceChunk) : SourceChunk =
        { source = fromQaSource chunk.source
          index = chunk.index
          text = chunk.text
          score = chunk.score }

    let private fromQaIndex (index: FsVoice.QA.KnowledgeSources.RetrievalIndex) =
        { qa = index
          sources = index.sources |> List.map fromQaSource
          chunks = index.chunks |> List.map fromQaChunk
          colbertIndices =
            index.colbertIndices
            |> List.map (fun (source, value) -> fromQaSource source, value)
          encoder = index.encoder }

    let emptyIndex = FsVoice.QA.KnowledgeSources.emptyIndex |> fromQaIndex

    let disposeIndex (retrieval: RetrievalIndex) =
        FsVoice.QA.KnowledgeSources.disposeIndex retrieval.qa

    let private sourceKindFromDocument kind =
        match kind with
        | PdfFile -> Pdf
        | MarkdownFile -> Markdown
        | JsonFile -> Json

    let fromPdfDocuments (docs: PdfDocumentSource list) =
        docs
        |> PdfDocuments.selectedReady
        |> List.map (fun doc ->
            { kind = sourceKindFromDocument doc.kind
              location = doc.storedPath
              enabled = true })

    let selectedSources (docs: PdfDocumentSource list) = fromPdfDocuments docs

    let loadChunks (sources: KnowledgeSource list) : Async<SourceChunk list * string list> =
        async {
            let! chunks, errors = sources |> List.map toQaSource |> FsVoice.QA.KnowledgeSources.loadChunks

            return chunks |> List.map fromQaChunk, errors
        }

    let loadInternalIndex (sources: KnowledgeSource list) : Async<RetrievalIndex * string list> =
        async {
            let! index, errors = sources |> List.map toQaSource |> FsVoice.QA.KnowledgeSources.loadInternalIndex

            return fromQaIndex index, errors
        }

    let private fsColbertRoot () =
        Path.Combine(FileSystem.AppDataDirectory, "FsVoice", "FsColbert")

    let prebuiltFolder () =
        let path = Path.Combine(fsColbertRoot (), "Prebuilt")
        Directory.CreateDirectory path |> ignore
        path

    let prebuiltManifestPath () =
        Path.Combine(prebuiltFolder (), "prebuilt-indexes.installed.json")

    let clearPersistedIndexes () =
        FsVoice.QA.KnowledgeSources.clearPersistedIndexes FileSystem.AppDataDirectory

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
                    client = Some(createChatClient key C.NANO_MODEL)
                    modelId = C.NANO_MODEL })
            |> Option.defaultValue
                { KeywordGenerationOptions.defaults with
                    client = None
                    modelId = C.NANO_MODEL }

    let private pdfParsingMode useHybridPdfParsing =
        if useHybridPdfParsing then
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Hybrid
        else
            FsVoice.QA.KnowledgeSources.PdfParsingMode.Legacy

    let InindexSource report keywordOptions useHybridPdfParsing (source: KnowledgeSource) =
        source
        |> toQaSource
        |> FsVoice.QA.KnowledgeSources.InindexSource
            FileSystem.AppDataDirectory
            report
            keywordOptions
            (pdfParsingMode useHybridPdfParsing)

    let loadIndex
        report
        (keywordOptions: KeywordGenerationOptions)
        useHybridPdfParsing
        (sources: KnowledgeSource list)
        =
        async {
            let! index, errors =
                sources
                |> List.map toQaSource
                |> FsVoice.QA.KnowledgeSources.loadIndex
                    FileSystem.AppDataDirectory
                    report
                    keywordOptions
                    (pdfParsingMode useHybridPdfParsing)
                    false

            return fromQaIndex index, errors
        }

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
                use client = createChatClient key C.NANO_MODEL

                let! chunks =
                    FsVoice.QA.KnowledgeSources.rank
                        (Some client)
                        logExpansions
                        logChunks
                        useLexicalFilter
                        report
                        query
                        maxResults
                        retrieval.qa

                return chunks |> List.map fromQaChunk
            | _ ->
                let! chunks =
                    FsVoice.QA.KnowledgeSources.rank
                        None
                        logExpansions
                        logChunks
                        useLexicalFilter
                        report
                        query
                        maxResults
                        retrieval.qa

                return chunks |> List.map fromQaChunk
        }

    let renderContext (chunks: SourceChunk list) =
        chunks |> List.map toQaChunk |> FsVoice.QA.KnowledgeSources.renderContext

    let renderInventory (sources: KnowledgeSource list) =
        sources |> List.map toQaSource |> FsVoice.QA.KnowledgeSources.renderInventory
