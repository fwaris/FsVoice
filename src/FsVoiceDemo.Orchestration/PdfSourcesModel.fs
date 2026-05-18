namespace FsVoiceDemo

open System
open System.IO

type DocumentKind =
    | PdfFile
    | MarkdownFile
    | JsonFile

type PickedSourceFileKind =
    | PickedDocument
    | PickedIndexBundle
    | UnsupportedPickedSourceFile

module PickedSourceFiles =
    let kind (fileName: string) =
        let extension =
            fileName
            |> Option.ofObj
            |> Option.defaultValue ""
            |> Path.GetExtension
            |> fun value -> value.ToLowerInvariant()

        match extension with
        | ".pdf"
        | ".md" -> PickedDocument
        | ".zip" -> PickedIndexBundle
        | _ -> UnsupportedPickedSourceFile

    let isDocument fileName =
        match kind fileName with
        | PickedDocument -> true
        | PickedIndexBundle
        | UnsupportedPickedSourceFile -> false

    let isIndexBundle fileName =
        match kind fileName with
        | PickedIndexBundle -> true
        | PickedDocument
        | UnsupportedPickedSourceFile -> false

type PdfProcessingStatus =
    | Queued
    | Processing
    | Ready
    | Failed

type PdfDocumentSource =
    { id: string
      kind: DocumentKind
      displayName: string
      storedPath: string
      originalPath: string
      selected: bool
      status: PdfProcessingStatus
      chunkCount: int
      error: string option }

type PdfProcessResult =
    { id: string
      chunkCount: int
      error: string option }

type PdfProcessingOutcome =
    | Completed of PdfProcessResult list
    | Canceled of PdfProcessResult list

type PdfDeleteResult =
    { id: string
      displayName: string
      removedFile: bool
      removedIndexCount: int
      indexErrors: string list }

type RetrievalMode =
    | InternalDocumentIndex
    | FsColbertWithFallback

module RetrievalModes =
    let labels = [ "Internal document index"; "FsColbert index with fallback" ]

    let toStorageValue mode =
        match mode with
        | InternalDocumentIndex -> "internal"
        | FsColbertWithFallback -> "fscolbert-with-fallback"

    let ofStorageValue value =
        match (defaultArg (Option.ofObj value) "").Trim().ToLowerInvariant() with
        | "internal"
        | "internal-document-index" -> InternalDocumentIndex
        | _ -> FsColbertWithFallback

    let toIndex mode =
        match mode with
        | InternalDocumentIndex -> 0
        | FsColbertWithFallback -> 1

    let ofIndex index =
        match index with
        | 0 -> InternalDocumentIndex
        | _ -> FsColbertWithFallback

    let displayName mode = labels |> List.item (toIndex mode)

module PdfDocuments =
    let normalizeBuiltInOriginalPath value =
        value
        |> Text.notEmpty
        |> Option.bind (fun path ->
            let path = path.Trim().Replace('\\', '/')
            let prefix = "app://"

            if path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                let assetPath = path.Substring(prefix.Length).TrimStart('/')

                if String.IsNullOrWhiteSpace assetPath then
                    None
                else
                    Some($"{prefix}{assetPath}".ToLowerInvariant())
            else
                None)

    let isBuiltIn doc =
        normalizeBuiltInOriginalPath doc.originalPath |> Option.isSome

    let kindLabel doc =
        match doc.kind with
        | PdfFile -> "PDF"
        | MarkdownFile -> "Markdown"
        | JsonFile -> "JSON"

    let isReady doc =
        match doc.status with
        | Ready -> true
        | _ -> false

    let canSelect doc = isReady doc

    let selectedReady docs =
        docs |> List.filter (fun doc -> doc.selected && isReady doc)

    let statusText doc =
        match doc.status with
        | Queued -> "Queued"
        | Processing -> "Processing..."
        | Ready -> $"Ready - {doc.chunkCount} chunk(s) - {kindLabel doc}"
        | Failed -> defaultArg doc.error "Failed"
