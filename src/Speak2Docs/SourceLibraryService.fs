namespace Speak2Docs

open System
open System.Threading
open FsVoice.Ctx

module SourceLibraryService =
    type ProcessingRequest =
        { report: string -> unit
          keywordOptions: FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions
          visualOptions: FsVoice.Retrieval.PdfVisualDescriptionOptions
          useHybridPdfParsing: bool
          useLayoutAnalysis: bool
          useOpticalParsing: bool
          useAutoOcrFallback: bool
          cancellationToken: CancellationToken
          documents: PdfDocumentSource list }

    let processDocuments request =
        PdfLibrary.processDocuments
            request.report
            request.keywordOptions
            request.visualOptions
            request.useHybridPdfParsing
            request.useLayoutAnalysis
            request.useOpticalParsing
            request.useAutoOcrFallback
            request.cancellationToken
            request.documents

    let previewAsync
        (sourceIndexService: ISourceIndexService)
        (profile: SourceIngestionProfile)
        maxRecords
        (source: KnowledgeSource)
        cancellationToken
        =
        sourceIndexService.PreviewAsync(profile, source, maxRecords, cancellationToken)
        |> Async.AwaitTask

    let deleteDocumentAndArtifacts
        (sourceIndexService: ISourceIndexService)
        (doc: PdfDocumentSource)
        (source: KnowledgeSource)
        cancellationToken
        =
        async {
            let! removedFile, prebuiltIndexCount, prebuiltErrors = PdfLibrary.deleteStoredDocumentAndPrebuiltIndexes doc

            let! artifactResult =
                sourceIndexService.DeleteArtifactsAsync(source, cancellationToken)
                |> Async.AwaitTask

            let persistedIndexCount, persistedErrors =
                match artifactResult with
                | Ok(deleted, errors) -> deleted, errors
                | Error ex -> 0, [ ex.Message ]

            return removedFile, prebuiltIndexCount + persistedIndexCount, prebuiltErrors @ persistedErrors
        }
