namespace FsVoice.Ctx

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type KnowledgeSourceKind =
    | Pdf
    | Markdown
    | Json

type KnowledgeSource =
    { kind: KnowledgeSourceKind
      location: string
      enabled: bool }

    member this.DisplayName =
        match this.kind with
        | Pdf -> $"PDF: {this.location}"
        | Markdown -> $"Markdown: {this.location}"
        | Json -> $"JSON: {this.location}"

[<RequireQualifiedAccess>]
type SourceContentRole =
    | Unknown
    | FrontMatter
    | Abstract
    | MainBody
    | References
    | Appendix
    | SubmissionChecklist

module SourceContentRole =
    let displayName =
        function
        | SourceContentRole.Unknown -> "Unknown"
        | SourceContentRole.FrontMatter -> "Front matter"
        | SourceContentRole.Abstract -> "Abstract"
        | SourceContentRole.MainBody -> "Main body"
        | SourceContentRole.References -> "References"
        | SourceContentRole.Appendix -> "Appendix"
        | SourceContentRole.SubmissionChecklist -> "Submission checklist"

type SourceChunk =
    { source: KnowledgeSource
      index: int
      sectionPath: string list
      contentRole: SourceContentRole
      pageNumbers: int list
      layoutLabels: string list
      captions: string list
      text: string
      score: float32 }

type PdfTextExtraction =
    | LegacyText
    | StructuredText of layoutAnalysis: bool

type PdfOcrPolicy =
    { repairCorruptText: bool
      parseSparsePages: bool }

module PdfOcrPolicy =
    let defaults =
        { repairCorruptText = true
          parseSparsePages = false }

type PdfIngestionProfile =
    { textExtraction: PdfTextExtraction
      ocr: PdfOcrPolicy
      describeVisuals: bool }

module PdfIngestionProfile =
    let defaults =
        { textExtraction = StructuredText true
          ocr = PdfOcrPolicy.defaults
          describeVisuals = false }

    let fromLegacyFlags useHybridPdfParsing useLayoutAnalysis useOpticalParsing useAutoOcrFallback describePdfVisuals =
        { textExtraction =
            if useHybridPdfParsing then
                StructuredText useLayoutAnalysis
            else
                LegacyText
          ocr =
            { repairCorruptText = useAutoOcrFallback
              parseSparsePages = useOpticalParsing }
          describeVisuals = describePdfVisuals }

type SourceIngestionProfile = { pdf: PdfIngestionProfile }

module SourceIngestionProfile =
    let defaults = { pdf = PdfIngestionProfile.defaults }

    let fromLegacyFlags useHybridPdfParsing useLayoutAnalysis useOpticalParsing useAutoOcrFallback describePdfVisuals =
        { pdf =
            PdfIngestionProfile.fromLegacyFlags
                useHybridPdfParsing
                useLayoutAnalysis
                useOpticalParsing
                useAutoOcrFallback
                describePdfVisuals }

type SourcePreviewVectorSummary =
    { tokenCount: int
      embeddingDim: int
      valueSample: float32 list }

type SourcePreviewRecord =
    { index: int
      sectionPath: string list
      contentRole: SourceContentRole
      pageNumbers: int list
      layoutLabels: string list
      captions: string list
      text: string
      keywords: string list
      terms: string list
      vector: SourcePreviewVectorSummary }

type SourcePreview =
    { source: KnowledgeSource
      totalChunks: int
      sampledCount: int
      records: SourcePreviewRecord list }

module SourcePreview =
    let mapRecords mapRecord preview =
        { preview with
            records = preview.records |> List.map mapRecord }

module SourceRendering =
    let private renderPageNumbers pages =
        pages |> List.map string |> String.concat ", "

    let renderContextWithLimit maxChunks (chunks: SourceChunk list) =
        chunks
        |> List.truncate (max 1 maxChunks)
        |> List.mapi (fun index chunk ->
            let metadata =
                seq {
                    yield $"source={chunk.source.DisplayName}"
                    yield $"chunk={chunk.index}"

                    if not (List.isEmpty chunk.pageNumbers) then
                        yield $"pages={renderPageNumbers chunk.pageNumbers}"

                    if chunk.contentRole <> SourceContentRole.Unknown then
                        yield $"role={SourceContentRole.displayName chunk.contentRole}"

                    if not (List.isEmpty chunk.sectionPath) then
                        let sectionPath = String.concat " > " chunk.sectionPath
                        yield $"section={sectionPath}"

                    if not (List.isEmpty chunk.layoutLabels) then
                        let layoutLabels = String.concat "," chunk.layoutLabels
                        yield $"layout={layoutLabels}"
                }
                |> String.concat "; "

            $"[{index + 1}] {metadata}\n{chunk.text}")
        |> String.concat "\n\n"

    let renderContext chunks = renderContextWithLimit 12 chunks

    let renderInventory (sources: KnowledgeSource list) =
        if List.isEmpty sources then
            "No selected source context providers are loaded."
        else
            sources
            |> List.mapi (fun index source -> $"[{index + 1}] {source.DisplayName}")
            |> String.concat "\n"

type QaContextRequest = { query: string; maxResults: int }

type RetrievalMode =
    | InternalDocumentIndex
    | FsColbertWithFallback

module RetrievalModes =
    let toStorageValue mode =
        match mode with
        | InternalDocumentIndex -> "internal"
        | FsColbertWithFallback -> "fscolbert-with-fallback"

    let ofStorageValue value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "internal"
        | "internal-document-index" -> InternalDocumentIndex
        | _ -> FsColbertWithFallback

    let displayName mode =
        match mode with
        | InternalDocumentIndex -> "Internal document index"
        | FsColbertWithFallback -> "FsColbert index with fallback"

type RiskFlags =
    { memoryMutation: bool
      sensitive: bool
      conflictLikely: bool }

module RiskFlags =
    let none =
        { memoryMutation = false
          sensitive = false
          conflictLikely = false }

type RealtimeJudgement =
    { turnKind: string option
      topicContinuity: string option
      memoryAction: string option
      needsExternalContext: bool option
      confidence: float
      riskFlags: RiskFlags }

type QaToolParameter =
    { name: string
      description: string
      required: bool }

type QaToolResult =
    { content: string
      metadata: IReadOnlyDictionary<string, string> }

module QaToolResult =
    let text content =
        { content = content
          metadata = Dictionary<string, string>() :> IReadOnlyDictionary<string, string> }

type IQaTool =
    abstract PluginName: string
    abstract Name: string
    abstract Description: string
    abstract Parameters: QaToolParameter list
    abstract InvokeAsync: IReadOnlyDictionary<string, string> * CancellationToken -> Task<QaToolResult>

type IQaToolHost =
    abstract Report: string -> unit

    abstract SearchKnowledgeAsync:
        question: string * maxResults: int * cancellationToken: CancellationToken -> Task<string>

    abstract SourceInventoryAsync: cancellationToken: CancellationToken -> Task<string>

    abstract SearchMemoryAsync: query: string * maxResults: int * cancellationToken: CancellationToken -> Task<string>

    abstract SearchBlackboardAsync: query: string * cancellationToken: CancellationToken -> Task<string>

type IQaToolProvider =
    abstract ContractVersion: int
    abstract GetTools: IQaToolHost -> IQaTool list

type IQaContextProvider =
    inherit IAsyncDisposable

    abstract ProviderId: string
    abstract DisplayName: string
    abstract Sources: KnowledgeSource list
    abstract LoadAsync: CancellationToken -> Task<string list>
    abstract RetrieveAsync: QaContextRequest * CancellationToken -> Task<SourceChunk list>
    abstract InventoryAsync: CancellationToken -> Task<string>

type ISemanticIndexResourceProvider =
    abstract SemanticIndexResource: obj option

type ISourceIndexService =
    abstract CreateContextProvider: SourceIngestionProfile * RetrievalMode * KnowledgeSource list -> IQaContextProvider

    abstract PreviewAsync:
        SourceIngestionProfile * KnowledgeSource * int * CancellationToken -> Task<Result<SourcePreview, string>>

    abstract DeleteArtifactsAsync: KnowledgeSource * CancellationToken -> Task<Result<int * string list, exn>>

type QaTurnRequest =
    { turnId: string
      question: string
      realtimeJudgement: RealtimeJudgement option
      deadline: DateTimeOffset option }

type QaToolObservation =
    { pluginName: string
      toolName: string
      query: string
      content: string
      createdAt: DateTimeOffset }

type QaAnswer =
    { turnId: string
      answer: string
      model: string
      context: SourceChunk list
      sourceRetrievalElapsedMs: float
      inventory: KnowledgeSource list
      toolObservations: QaToolObservation list
      timedOut: bool
      createdAt: DateTimeOffset }

type IQaSession =
    inherit IAsyncDisposable

    abstract LoadSourcesAsync: RetrievalMode * KnowledgeSource list * CancellationToken -> Task<string list>

    abstract AnswerAsync: QaTurnRequest * CancellationToken -> Task<QaAnswer>

type IQaOrchestrator =
    inherit IAsyncDisposable

    abstract ConfigureAsync: IQaContextProvider list * CancellationToken -> Task<string list>
    abstract AnswerAsync: QaTurnRequest * CancellationToken -> Task<QaAnswer>

type IQaAnswerTransportPreparer =
    abstract PrepareAnswerTransportAsync: CancellationToken -> Task
