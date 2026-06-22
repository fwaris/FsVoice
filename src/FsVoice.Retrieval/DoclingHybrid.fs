namespace FsVoice.Retrieval

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Threading
open FSharp.Control
open FSharp.SystemTextJson
open Microsoft.Extensions.AI
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open FsColbert
open RapidOcrNet
open SkiaSharp
open FsVoice.Core

type DoclingLayoutModelProviderContext =
    { storageRoot: string
      report: string -> unit
      cancellationToken: CancellationToken }

type DoclingLayoutModelProviderResult =
    { predictor: IDoclingLayoutPredictor
      disposable: IDisposable option }

type IDoclingLayoutModelProvider =
    abstract Id: string
    abstract DisplayName: string
    abstract CreateAsync: DoclingLayoutModelProviderContext -> Async<Result<DoclingLayoutModelProviderResult, string>>

type PdfVisualDescriptionOptions =
    { enabled: bool
      client: IChatClient option
      modelId: string
      schemaVersion: string
      maxOutputTokens: int
      parallelism: int
      cacheStorageRoot: string option }

module PdfVisualDescriptionOptions =
    [<Literal>]
    let defaultModelId = "gpt-5-mini"

    [<Literal>]
    let defaultSchemaVersion = "pdf-visual-descriptions-v1"

    let defaults =
        { enabled = false
          client = None
          modelId = defaultModelId
          schemaVersion = defaultSchemaVersion
          maxOutputTokens = 1000
          parallelism = 2
          cacheStorageRoot = None }

    let disabled = defaults

    let sanitize (options: PdfVisualDescriptionOptions) =
        { options with
            modelId = options.modelId |> Text.notEmpty |> Option.defaultValue defaultModelId
            schemaVersion =
                options.schemaVersion
                |> Text.notEmpty
                |> Option.defaultValue defaultSchemaVersion
            maxOutputTokens = max 128 options.maxOutputTokens
            parallelism = max 1 options.parallelism }

    let fingerprint (options: PdfVisualDescriptionOptions) =
        let options = sanitize options

        if not options.enabled then
            "pdfVisualDescriptions=disabled"
        else
            [ "pdfVisualDescriptions=enabled"
              $"pdfVisualDescriptionModel={options.modelId}"
              $"pdfVisualDescriptionSchema={options.schemaVersion}"
              "pdfVisualDescriptionPrompt=visual-descriptions-v2" ]
            |> String.concat "\n"

type DoclingHybridOptions =
    { minNativeCharsPerPage: int
      ocrDedupeOverlapThreshold: float
      rasterDpi: int
      enableOcr: bool
      enableAutoOpticalParsing: bool
      enableLayoutAnalysis: bool
      enableFigureClassification: bool
      layoutModelProvider: IDoclingLayoutModelProvider option
      visualDescriptions: PdfVisualDescriptionOptions }

type DoclingRasterizerInfo = { id: string; displayName: string }

module DoclingHybrid =
    let private normalizeFingerprintPart fallback value =
        match Text.notEmpty value with
        | Some text -> text.Trim().ToLowerInvariant().Replace(" ", "-").Replace(":", "-").Replace("=", "-")
        | None -> fallback

    let defaults =
        { minNativeCharsPerPage = 24
          ocrDedupeOverlapThreshold = 0.75
          rasterDpi = 96
          enableOcr = true
          enableAutoOpticalParsing = true
          enableLayoutAnalysis = true
          enableFigureClassification = false
          layoutModelProvider = None
          visualDescriptions = PdfVisualDescriptionOptions.disabled }

    let mutable private activeDefaults = defaults

    let setDefaultOptions options = activeDefaults <- options

    let currentDefaultOptions () = activeDefaults

    let resetDefaultOptions () = activeDefaults <- defaults

    let private timed report label operation =
        async {
            let timer = Stopwatch.StartNew()
            report $"{label}..."

            try
                let! result = operation
                timer.Stop()
                report (sprintf "%s completed in %.1fs." label timer.Elapsed.TotalSeconds)
                return result
            with ex ->
                timer.Stop()
                report (sprintf "%s failed after %.1fs: %s" label timer.Elapsed.TotalSeconds ex.Message)
                return raise ex
        }

    let private withTimeout timeoutMs errorMessage operation =
        async {
            try
                let! child = Async.StartChild(operation, timeoutMs)
                return! child
            with :? TimeoutException ->
                return Error errorMessage
        }

    let private throwIfCanceled (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

    let private layoutConversionTimeoutMs pageCount =
        max 45000 (60000 + (max 1 pageCount * 10000))

    let private fsColbertRoot storageRoot =
        let path = Path.Combine(storageRoot, "FsVoice", "FsColbert")
        Directory.CreateDirectory path |> ignore
        path

    let private doclingModelFolder storageRoot name =
        Path.Combine(fsColbertRoot storageRoot, "Models", name)

    [<Literal>]
    let private ppDocLayoutMModelFileName = "pp-doclayout-m.onnx"

    [<Literal>]
    let private ppDocLayoutMModelUrl =
        "https://github.com/GreatV/oar-ocr/releases/download/v0.3.0/pp-doclayout-m.onnx"

    [<Literal>]
    let private ppDocLayoutMModelSha256 =
        "8e458bfc919bbf7a35be9802485b5cd30151cb356364cfad09911d2ee1fc1f76"

    let private rapidOcrModelFolders storageRoot =
        [ Environment.GetEnvironmentVariable "FSKAME_RAPIDOCR_MODELS"
          Path.Combine(AppContext.BaseDirectory, "models", "v5")
          Path.Combine(AppContext.BaseDirectory, "models")
          Path.Combine(fsColbertRoot storageRoot, "Models", "rapidocr")
          Path.Combine(storageRoot, "models", "v5")
          Path.Combine(storageRoot, "models", "rapidocr")
          Path.Combine(AppContext.BaseDirectory, "FsVoice", "FsColbert", "Models", "rapidocr") ]
        |> List.choose Text.notEmpty
        |> List.distinctBy _.ToLowerInvariant()

    let private toBitmap (image: DoclingRgbImage) =
        DoclingRgbImage.validate image

        let bitmap =
            new SKBitmap(image.width, image.height, SKColorType.Bgra8888, SKAlphaType.Opaque)

        for y = 0 to image.height - 1 do
            for x = 0 to image.width - 1 do
                let offset = (y * image.width + x) * 3

                bitmap.SetPixel(
                    x,
                    y,
                    SKColor(image.pixels[offset], image.pixels[offset + 1], image.pixels[offset + 2], 255uy)
                )

        bitmap

    type private VisualDescriptionResult =
        { description: string
          keywords: string list }

    type private VisualDescriptionCandidate =
        { selfRef: string
          parent: string
          label: DoclingLabel
          contentLayer: DoclingContentLayer
          prov: DoclingProvenance list
          keywords: string list
          sourceId: string option
          sourceDisplayName: string option }

    let private visualDescriptionPromptVersion = "visual-descriptions-v2"

    let private visualDescriptionJsonOptions =
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.Converters.Add(JsonFSharpConverter())
        options

    let private hashBytes (bytes: byte[]) =
        use sha = SHA256.Create()

        sha.ComputeHash bytes
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    let private hashText (value: string) =
        value |> Encoding.UTF8.GetBytes |> hashBytes

    let private pdfSourceFingerprint (path: string) =
        let info = FileInfo path

        if info.Exists then
            $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
        else
            path

    let private cleanKeywords maxValues values =
        values
        |> Seq.choose (Text.normalizeWhitespace >> Text.notEmpty)
        |> Seq.distinctBy _.ToLowerInvariant()
        |> Seq.truncate maxValues
        |> Seq.toList

    let private labelText label =
        match label with
        | DoclingLabel.Picture -> "picture"
        | DoclingLabel.Chart -> "chart"
        | DoclingLabel.Table -> "table"
        | _ -> label.ToString().ToLowerInvariant()

    let private visualCacheFolder storageRoot =
        let path = Path.Combine(fsColbertRoot storageRoot, "VisualDescriptionCache")
        Directory.CreateDirectory path |> ignore
        path

    let private visualCacheRoot options path =
        options.cacheStorageRoot
        |> Option.orElseWith (fun () -> path |> Path.GetFullPath |> Path.GetDirectoryName |> Option.ofObj)
        |> Option.defaultValue (Path.GetTempPath())

    let private visualCachePath options path key =
        Path.Combine(visualCacheFolder (visualCacheRoot options path), $"{key}.json")

    let private tryReadVisualDescriptionCache cachePath =
        try
            if not (File.Exists cachePath) then
                None
            else
                use document = JsonDocument.Parse(File.ReadAllText cachePath)
                let root = document.RootElement

                let mutable descriptionProperty = Unchecked.defaultof<JsonElement>
                let mutable keywordsProperty = Unchecked.defaultof<JsonElement>

                let description =
                    if
                        root.TryGetProperty("description", &descriptionProperty)
                        && descriptionProperty.ValueKind = JsonValueKind.String
                    then
                        descriptionProperty.GetString() |> Text.notEmpty
                    else
                        None

                let keywords =
                    if
                        root.TryGetProperty("keywords", &keywordsProperty)
                        && keywordsProperty.ValueKind = JsonValueKind.Array
                    then
                        keywordsProperty.EnumerateArray()
                        |> Seq.choose (fun item ->
                            if item.ValueKind = JsonValueKind.String then
                                item.GetString() |> Text.notEmpty
                            else
                                None)
                        |> cleanKeywords 16
                    else
                        []

                description
                |> Option.map (fun description ->
                    { description = description
                      keywords = keywords })
        with _ ->
            None

    let private writeVisualDescriptionCache cachePath result =
        try
            let json =
                JsonSerializer.Serialize(
                    {| description = result.description
                       keywords = result.keywords |},
                    visualDescriptionJsonOptions
                )

            let tempPath = $"{cachePath}.tmp"
            File.WriteAllText(tempPath, json)

            if File.Exists cachePath then
                File.Delete cachePath

            File.Move(tempPath, cachePath)
        with _ ->
            ()

    let private topLeftBoxForImage (image: DoclingRgbImage) (bbox: DoclingBoundingBox) =
        let l, t, r, b =
            match bbox.coordOrigin with
            | DoclingCoordinateOrigin.BottomLeft ->
                bbox.l, float image.height - bbox.t, bbox.r, float image.height - bbox.b
            | _ -> bbox.l, bbox.t, bbox.r, bbox.b

        let width = max 1.0 (r - l)
        let height = max 1.0 (b - t)
        let padding = max 4.0 (0.04 * max width height)

        let clamp (minValue: float) (maxValue: float) (value: float) =
            Math.Min(maxValue, Math.Max(minValue, value))

        let left = clamp 0.0 (float image.width - 1.0) (l - padding)
        let top = clamp 0.0 (float image.height - 1.0) (t - padding)
        let right = clamp (left + 1.0) (float image.width) (r + padding)
        let bottom = clamp (top + 1.0) (float image.height) (b + padding)

        int (Math.Floor left), int (Math.Floor top), int (Math.Ceiling right), int (Math.Ceiling bottom)

    let private tryCropVisualPng (pageInputs: DoclingPageInput list) (candidate: VisualDescriptionCandidate) =
        match candidate.prov |> List.tryHead with
        | None -> Error "visual region has no page provenance"
        | Some prov ->
            match pageInputs |> List.tryFind (fun page -> page.pageNo = prov.pageNo) with
            | None -> Error $"page {prov.pageNo} was not available for visual cropping"
            | Some page ->
                try
                    let left, top, right, bottom = topLeftBoxForImage page.image prov.bbox
                    let width = right - left
                    let height = bottom - top

                    if width <= 1 || height <= 1 then
                        Error "visual crop was empty"
                    else
                        use source = toBitmap page.image
                        use cropped = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque)
                        use canvas = new SKCanvas(cropped)
                        canvas.Clear(SKColors.White)
                        let sourceRect = SKRectI(left, top, right, bottom)
                        let destinationRect = SKRect(0f, 0f, float32 width, float32 height)
                        canvas.DrawBitmap(source, sourceRect, destinationRect)
                        use data = cropped.Encode(SKEncodedImageFormat.Png, 90)
                        Ok(data.ToArray())
                with ex ->
                    Error $"visual crop failed: {ex.Message}"

    let private visualDescriptionCacheKey options path candidate pngBytes =
        let provText =
            candidate.prov
            |> List.map (fun prov ->
                let bbox = prov.bbox

                $"{prov.pageNo}:{bbox.l:F2}:{bbox.t:F2}:{bbox.r:F2}:{bbox.b:F2}:{bbox.coordOrigin}")
            |> String.concat "|"

        [ pdfSourceFingerprint path
          labelText candidate.label
          provText
          hashBytes pngBytes
          options.modelId
          options.schemaVersion
          visualDescriptionPromptVersion ]
        |> String.concat "\n"
        |> hashText

    let private visualDescriptionPrompt candidate =
        $"""
Describe this detected PDF visual region for document retrieval indexing.

Visual kind: {labelText candidate.label}

Return only a JSON object:
{{"description":"one compact factual description","keywords":["4-8 short retrieval keywords"]}}

Rules:
- Use only what is visible in the image.
- Mention the most important labels, axes, entities, relationships, and visible trends when present.
- Do not speculate about facts that are not visible.
- Keep the description under 45 words.
- Keep each keyword under 4 words.
"""

    let private parseVisualDescriptionResponse (text: string) =
        let fallback () =
            let trimmed = if isNull text then "" else text.Trim()

            if
                trimmed.StartsWith("{", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
            then
                None
            else
                text
                |> Text.normalizeWhitespace
                |> Text.notEmpty
                |> Option.map (fun description ->
                    { description = description
                      keywords = [] })

        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                fallback ()
            else
                let mutable descriptionProperty = Unchecked.defaultof<JsonElement>
                let mutable keywordsProperty = Unchecked.defaultof<JsonElement>

                let description =
                    if
                        root.TryGetProperty("description", &descriptionProperty)
                        && descriptionProperty.ValueKind = JsonValueKind.String
                    then
                        descriptionProperty.GetString() |> Text.normalizeWhitespace |> Text.notEmpty
                    else
                        None

                let keywords =
                    if
                        root.TryGetProperty("keywords", &keywordsProperty)
                        && keywordsProperty.ValueKind = JsonValueKind.Array
                    then
                        keywordsProperty.EnumerateArray()
                        |> Seq.choose (fun item ->
                            if item.ValueKind = JsonValueKind.String then
                                item.GetString() |> Text.notEmpty
                            else
                                None)
                        |> cleanKeywords 16
                    else
                        []

                description
                |> Option.map (fun description ->
                    { description = description
                      keywords = keywords })
        with _ ->
            fallback ()

    let private describeVisualWithModel
        report
        (path: string)
        (options: PdfVisualDescriptionOptions)
        (client: IChatClient)
        pageInputs
        candidate
        cancellationToken
        =
        async {
            match tryCropVisualPng pageInputs candidate with
            | Error err ->
                report $"PDF visual description skipped for {Path.GetFileName path}: {err}."
                return None
            | Ok pngBytes ->
                let cacheKey = visualDescriptionCacheKey options path candidate pngBytes
                let cachePath = visualCachePath options path cacheKey

                match tryReadVisualDescriptionCache cachePath with
                | Some cached -> return Some(candidate, cached)
                | None ->
                    try
                        let contents = ResizeArray<AIContent>()
                        contents.Add(TextContent(visualDescriptionPrompt candidate) :> AIContent)
                        contents.Add(DataContent(ReadOnlyMemory<byte>(pngBytes), "image/png") :> AIContent)

                        let messages = [ ChatMessage(ChatRole.User, contents) ]
                        let chatOptions = ChatOptions()
                        chatOptions.MaxOutputTokens <- Nullable options.maxOutputTokens
                        chatOptions.ResponseFormat <- ChatResponseFormat.Json

                        let! response =
                            client.GetResponseAsync(messages, chatOptions, cancellationToken)
                            |> Async.AwaitTask

                        match parseVisualDescriptionResponse response.Text with
                        | None ->
                            report
                                $"PDF visual description returned no usable text for {Path.GetFileName path} {labelText candidate.label}."

                            return None
                        | Some result ->
                            writeVisualDescriptionCache cachePath result
                            return Some(candidate, result)
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException cancellationToken)
                    | ex ->
                        report
                            $"PDF visual description failed for {Path.GetFileName path} {labelText candidate.label}: {ex.Message}"

                        return None
        }

    let private tableTextLength (table: DoclingTableItem) =
        table.data.tableCells
        |> List.sumBy (fun cell ->
            if String.IsNullOrWhiteSpace cell.text then
                0
            else
                cell.text.Trim().Length)

    let private visualCandidates (document: DoclingDocument) =
        let fromPicture (picture: DoclingPictureItem) =
            match picture.label, picture.prov with
            | (DoclingLabel.Picture | DoclingLabel.Chart), _ :: _ ->
                Some
                    { selfRef = picture.selfRef
                      parent = picture.parent
                      label = picture.label
                      contentLayer = picture.contentLayer
                      prov = picture.prov
                      keywords = picture.keywords
                      sourceId = picture.sourceId
                      sourceDisplayName = picture.sourceDisplayName }
            | _ -> None

        let fromSparseTable (table: DoclingTableItem) =
            match table.prov with
            | _ :: _ when tableTextLength table < 32 ->
                Some
                    { selfRef = table.selfRef
                      parent = table.parent
                      label = DoclingLabel.Table
                      contentLayer = table.contentLayer
                      prov = table.prov
                      keywords = table.keywords
                      sourceId = table.sourceId
                      sourceDisplayName = table.sourceDisplayName }
            | _ -> None

        [ yield! document.pictures |> List.choose fromPicture
          yield! document.tables |> List.choose fromSparseTable ]

    let private syntheticVisualText baseIndex index candidate result =
        let kind = labelText candidate.label

        let description = result.description |> Text.normalizeWhitespace

        let text = $"[Visual description: {kind}] {description}"

        { selfRef = $"#/texts/{baseIndex + index}"
          parent = candidate.parent
          label = DoclingLabel.Text
          text = text
          orig = text
          contentLayer = candidate.contentLayer
          prov = candidate.prov
          keywords =
            seq {
                yield kind
                yield "visual"
                yield! candidate.keywords
                yield! result.keywords
            }
            |> cleanKeywords 24
          sourceId = candidate.sourceId
          sourceDisplayName = candidate.sourceDisplayName }

    let private insertVisualTexts
        (document: DoclingDocument)
        (items: (VisualDescriptionCandidate * VisualDescriptionResult) list)
        =
        if List.isEmpty items then
            document
        else
            let syntheticTexts =
                items
                |> List.mapi (fun index (candidate, result) ->
                    candidate.selfRef, syntheticVisualText document.texts.Length index candidate result)

            let syntheticByParent = syntheticTexts |> Map.ofList

            let bodyChildren, inserted =
                document.bodyChildren
                |> List.fold
                    (fun (children, inserted) child ->
                        match syntheticByParent |> Map.tryFind child with
                        | None -> child :: children, inserted
                        | Some synthetic -> synthetic.selfRef :: child :: children, Set.add synthetic.selfRef inserted)
                    ([], Set.empty)

            let appended =
                syntheticTexts
                |> List.map snd
                |> List.filter (fun item -> not (Set.contains item.selfRef inserted))
                |> List.map _.selfRef

            { document with
                texts = document.texts @ (syntheticTexts |> List.map snd)
                bodyChildren = (List.rev bodyChildren) @ appended }

    let private enrichDocumentWithVisualDescriptions
        options
        report
        (path: string)
        pageInputs
        (document: DoclingDocument)
        cancellationToken
        =
        async {
            let options = PdfVisualDescriptionOptions.sanitize options
            throwIfCanceled cancellationToken

            if not options.enabled then
                return document
            else
                match options.client with
                | None ->
                    report
                        $"PDF visual descriptions are enabled for {Path.GetFileName path}, but no visual-description model client is configured; skipping."

                    return document
                | Some client ->
                    let candidates = visualCandidates document

                    if List.isEmpty candidates then
                        return document
                    else
                        report
                            $"PDF visual descriptions: describing {candidates.Length} detected visual region(s) in {Path.GetFileName path} with {options.modelId}."

                        let! described =
                            candidates
                            |> AsyncSeq.ofSeq
                            |> AsyncSeq.mapAsyncParallelThrottled options.parallelism (fun candidate ->
                                describeVisualWithModel
                                    report
                                    path
                                    options
                                    client
                                    pageInputs
                                    candidate
                                    cancellationToken)
                            |> AsyncSeq.toListAsync

                        throwIfCanceled cancellationToken

                        let described = described |> List.choose id

                        if List.isEmpty described then
                            return document
                        else
                            report
                                $"PDF visual descriptions: added {described.Length} visual description(s) for {Path.GetFileName path}."

                            return insertVisualTexts document described
        }

    type private RasterizerRegistration =
        { info: DoclingRasterizerInfo
          factory: DoclingHybridOptions -> IDoclingPageRasterizer }

    let private missingRasterizerInfo =
        { id = "none"
          displayName = "No registered rasterizer" }

    let mutable private rasterizerRegistration: RasterizerRegistration option = None

    let setRasterizerFactoryWithInfo id displayName factory =
        rasterizerRegistration <-
            Some
                { info =
                    { id = normalizeFingerprintPart "custom" id
                      displayName = displayName |> Text.notEmpty |> Option.defaultValue "Custom PDF rasterizer" }
                  factory = factory }

    let currentRasterizerInfo () =
        rasterizerRegistration
        |> Option.map _.info
        |> Option.defaultValue missingRasterizerInfo

    let setRasterizerFactory factory =
        setRasterizerFactoryWithInfo "custom" "Custom PDF rasterizer" factory

    let clearRasterizerFactory () = rasterizerRegistration <- None

    let private createRasterizer options =
        match rasterizerRegistration with
        | Some registration -> registration.factory options
        | None ->
            { new IDoclingPageRasterizer with
                member _.RasterizeAsync path =
                    async {
                        return
                            Error
                                $"No document structure PDF rasterizer is registered for '{path}'. Reference FsVoice.Retrieval.PdfRasterization and call PdfRasterizer.register() before using Hybrid PDF parsing."
                    } }

    type private RapidOcrProvider(ocr: RapidOcr) =
        let gate = obj ()

        let textBlockCell (block: TextBlock) =
            let text = block.Text |> Text.normalizeWhitespace

            if
                String.IsNullOrWhiteSpace text
                || isNull block.BoxPoints
                || block.BoxPoints.Length = 0
            then
                None
            else
                let xs = block.BoxPoints |> Array.map _.X
                let ys = block.BoxPoints |> Array.map _.Y

                Some
                    { text = text
                      bbox =
                        DoclingGeometry.topLeftBox
                            (float (Array.min xs))
                            (float (Array.min ys))
                            (float (Array.max xs))
                            (float (Array.max ys))
                      confidence = Some(float block.BoxScore) }

        interface IDoclingOcrProvider with
            member _.RecognizeAsync page =
                async {
                    try
                        let cells =
                            lock gate (fun () ->
                                use bitmap = toBitmap page.image
                                let result = ocr.Detect(bitmap, RapidOcrOptions.Default)

                                if isNull result.TextBlocks then
                                    []
                                else
                                    result.TextBlocks |> Array.choose textBlockCell |> Array.toList)

                        return Ok cells
                    with ex ->
                        return Error $"RapidOCR failed on page {page.pageNo}: {ex.Message}"
                }

        interface IDisposable with
            member _.Dispose() =
                try
                    ocr.Dispose()
                with _ ->
                    ()

    let private findFile predicate folder =
        if Directory.Exists folder then
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            |> Seq.tryFind (fun path -> predicate (Path.GetFileName(path).ToLowerInvariant()))
        else
            None

    let private findRapidOcrFiles folder =
        let isOnnx (name: string) =
            name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)

        let det = findFile (fun name -> isOnnx name && name.Contains "det") folder
        let cls = findFile (fun name -> isOnnx name && name.Contains "cls") folder
        let recModel = findFile (fun name -> isOnnx name && name.Contains "rec") folder

        let keys =
            findFile
                (fun name ->
                    name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    && (name.Contains "dict" || name.Contains "keys"))
                folder
            |> Option.orElseWith (fun () ->
                findFile (fun name -> name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) folder)

        match det, cls, recModel, keys with
        | Some det, Some cls, Some recModel, Some keys -> Some(det, cls, recModel, keys)
        | _ -> None

    let private tryCreateRapidOcrProvider options storageRoot =
        if not options.enableOcr then
            Ok None
        else
            rapidOcrModelFolders storageRoot
            |> List.tryPick (fun folder -> findRapidOcrFiles folder |> Option.map (fun files -> folder, files))
            |> function
                | None -> Ok None
                | Some(_, (det, cls, recModel, keys)) ->
                    let ocr = new RapidOcr()

                    try
                        ocr.InitModels(det, cls, recModel, keys, max 1 (Environment.ProcessorCount / 2))
                        Ok(Some(new RapidOcrProvider(ocr) :> IDoclingOcrProvider))
                    with ex ->
                        try
                            ocr.Dispose()
                        with _ ->
                            ()

                        Error $"RapidOCR model setup failed: {ex.Message}"

    let private sha256Hex (bytes: byte[]) =
        use sha = SHA256.Create()

        sha.ComputeHash bytes
        |> Array.map (fun value -> value.ToString("x2"))
        |> String.concat ""

    let private ppDocLayoutMResourceName =
        "FsVoice.Retrieval.Resources.Models.pp-doclayout-m.onnx"

    let private tryExtractEmbeddedPpDocLayoutM path =
        try
            let assembly = Assembly.GetExecutingAssembly()

            use stream = assembly.GetManifestResourceStream ppDocLayoutMResourceName

            if isNull stream then
                Error $"Embedded PP-DocLayout-M ONNX resource '{ppDocLayoutMResourceName}' was not found."
            else
                use sha = SHA256.Create()

                use tempStream =
                    new FileStream($"{path}.tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None)

                stream.CopyTo tempStream
                tempStream.Position <- 0L

                let actualHash =
                    sha.ComputeHash tempStream
                    |> Array.map (fun value -> value.ToString("x2"))
                    |> String.concat ""

                if not (String.Equals(actualHash, ppDocLayoutMModelSha256, StringComparison.Ordinal)) then
                    tempStream.Dispose()
                    File.Delete $"{path}.tmp"

                    Error
                        $"Embedded PP-DocLayout-M ONNX hash mismatch. Expected {ppDocLayoutMModelSha256}, got {actualHash}."
                else
                    let tempPath = $"{path}.tmp"
                    tempStream.Dispose()

                    if File.Exists path then
                        File.Delete path

                    File.Move(tempPath, path)
                    Ok path
        with ex ->
            Error $"Unable to extract embedded PP-DocLayout-M ONNX model: {ex.Message}"

    let private ensurePpDocLayoutMDownloadedAsync (client: HttpClient) storageRoot =
        async {
            let folder = doclingModelFolder storageRoot "pp-doclayout-m-onnx-v0.3.0"
            Directory.CreateDirectory folder |> ignore
            let path = Path.Combine(folder, ppDocLayoutMModelFileName)

            let isValidModel path =
                File.Exists path
                && String.Equals(sha256Hex (File.ReadAllBytes path), ppDocLayoutMModelSha256, StringComparison.Ordinal)

            if isValidModel path then
                return Ok path
            else
                match tryExtractEmbeddedPpDocLayoutM path with
                | Ok path -> return Ok path
                | Error embeddedError ->
                    try
                        let! bytes = client.GetByteArrayAsync(ppDocLayoutMModelUrl) |> Async.AwaitTask
                        let actualHash = sha256Hex bytes

                        if not (String.Equals(actualHash, ppDocLayoutMModelSha256, StringComparison.Ordinal)) then
                            return
                                Error
                                    $"PP-DocLayout-M ONNX hash mismatch. Expected {ppDocLayoutMModelSha256}, got {actualHash}. Embedded model extraction also failed: {embeddedError}"
                        else
                            let tempPath = $"{path}.tmp"
                            File.WriteAllBytes(tempPath, bytes)

                            if File.Exists path then
                                File.Delete path

                            File.Move(tempPath, path)
                            return Ok path
                    with ex ->
                        return
                            Error
                                $"Unable to load PP-DocLayout-M ONNX model from embedded resource or download. Embedded error: {embeddedError}. Download error: {ex.Message}"
        }

    type private PpDocLayoutMOnnx(modelPath: string, report: string -> unit) =
        let threshold = 0.3f
        let width = 640
        let height = 640
        let scaleFactor = 1.0f / 255.0f

        let labels =
            [ 0, "paragraph_title"
              1, "image"
              2, "text"
              3, "number"
              4, "abstract"
              5, "content"
              6, "figure_title"
              7, "formula"
              8, "table"
              9, "table_title"
              10, "reference"
              11, "doc_title"
              12, "footnote"
              13, "header"
              14, "algorithm"
              15, "footer"
              16, "seal"
              17, "chart_title"
              18, "chart"
              19, "formula_number"
              20, "header_image"
              21, "footer_image"
              22, "aside_text" ]
            |> Map.ofList

        let session = new InferenceSession(modelPath)
        let gate = obj ()

        let inputName name =
            session.InputMetadata.Keys
            |> Seq.tryFind (fun key -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith $"PP-DocLayout-M ONNX input '{name}' was not found.")

        let imageInputName = inputName "image"
        let scaleFactorInputName = inputName "scale_factor"

        let primaryOutputName =
            session.OutputMetadata.Keys
            |> Seq.tryFind (fun key -> String.Equals(key, "fetch_name_0", StringComparison.OrdinalIgnoreCase))
            |> Option.orElseWith (fun () -> session.OutputMetadata.Keys |> Seq.tryHead)
            |> Option.defaultWith (fun () -> failwith "PP-DocLayout-M ONNX model has no outputs.")

        let resize (image: DoclingRgbImage) =
            use source = toBitmap image

            let resized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque)

            use canvas = new SKCanvas(resized)
            canvas.Clear(SKColors.White)
            canvas.DrawBitmap(source, SKRect(0f, 0f, float32 width, float32 height))

            resized

        let imageInput (image: DoclingRgbImage) =
            use bitmap = resize image
            let values = Array.zeroCreate<float32> (3 * width * height)
            let plane = width * height

            for y = 0 to height - 1 do
                for x = 0 to width - 1 do
                    let color = bitmap.GetPixel(x, y)
                    let offset = y * width + x
                    values[offset] <- float32 color.Red * scaleFactor
                    values[plane + offset] <- float32 color.Green * scaleFactor
                    values[(2 * plane) + offset] <- float32 color.Blue * scaleFactor

            let tensor = DenseTensor<float32>(values, [| 1; 3; height; width |])
            NamedOnnxValue.CreateFromTensor(imageInputName, tensor)

        let scaleFactorInput (image: DoclingRgbImage) =
            let values =
                [| float32 height / float32 image.height; float32 width / float32 image.width |]

            let tensor = DenseTensor<float32>(values, [| 1; 2 |])
            NamedOnnxValue.CreateFromTensor(scaleFactorInputName, tensor)

        let outputTensor (outputs: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) =
            outputs
            |> Seq.tryFind (fun output -> String.Equals(output.Name, primaryOutputName, StringComparison.Ordinal))
            |> Option.defaultWith (fun () ->
                failwith $"PP-DocLayout-M ONNX output '{primaryOutputName}' was not returned.")
            |> _.AsTensor<float32>()

        let labelFor label =
            match label with
            | "paragraph_title" -> DoclingLabel.SectionHeader
            | "doc_title" -> DoclingLabel.Title
            | "image"
            | "header_image"
            | "footer_image" -> DoclingLabel.Picture
            | "chart" -> DoclingLabel.Chart
            | "figure_title"
            | "table_title"
            | "chart_title" -> DoclingLabel.Caption
            | "formula"
            | "formula_number" -> DoclingLabel.Formula
            | "table" -> DoclingLabel.Table
            | "header" -> DoclingLabel.PageHeader
            | "footer" -> DoclingLabel.PageFooter
            | "footnote" -> DoclingLabel.Footnote
            | "algorithm" -> DoclingLabel.Code
            | _ -> DoclingLabel.Text

        let thresholdFor label =
            match label with
            | "paragraph_title"
            | "formula" -> 0.3f
            | "text" -> 0.4f
            | "seal" -> 0.45f
            | _ -> 0.5f

        let clamp limit value =
            Math.Min(float limit, Math.Max(0.0, float value))

        let boxFor (image: DoclingRgbImage) x1 y1 x2 y2 =
            let normalized = x2 <= 1.05f && y2 <= 1.05f && x1 >= -0.05f && y1 >= -0.05f

            let l, t, r, b =
                if normalized then
                    float x1 * float image.width,
                    float y1 * float image.height,
                    float x2 * float image.width,
                    float y2 * float image.height
                else
                    float x1, float y1, float x2, float y2

            { l = clamp image.width l
              t = clamp image.height t
              r = clamp image.width r
              b = clamp image.height b
              coordOrigin = DoclingCoordinateOrigin.TopLeft }

        let boxArea (bbox: DoclingBoundingBox) =
            max 0.0 (bbox.r - bbox.l) * max 0.0 (bbox.b - bbox.t)

        let intersectionOverUnion (left: DoclingBoundingBox) (right: DoclingBoundingBox) =
            let l = max left.l right.l
            let t = max left.t right.t
            let r = min left.r right.r
            let b = min left.b right.b
            let intersection = max 0.0 (r - l) * max 0.0 (b - t)
            let union = boxArea left + boxArea right - intersection

            if union <= 0.0 then 0.0 else intersection / union

        let applyNms (clusters: DoclingLayoutCluster list) =
            let sorted = clusters |> List.sortByDescending (fun cluster -> cluster.confidence)

            let rec loop (kept: DoclingLayoutCluster list) (remaining: DoclingLayoutCluster list) =
                match remaining with
                | [] -> List.rev kept
                | cluster :: rest ->
                    let shouldKeep =
                        kept
                        |> List.forall (fun (keptCluster: DoclingLayoutCluster) ->
                            let threshold = if keptCluster.label = cluster.label then 0.6 else 0.98

                            intersectionOverUnion keptCluster.bbox cluster.bbox < threshold)

                    if shouldKeep then
                        loop (cluster :: kept) rest
                    else
                        loop kept rest

            loop [] sorted

        let predictOne (page: DoclingPageInput) =
            let imageInput = imageInput page.image
            let scaleFactorInput = scaleFactorInput page.image

            use outputs = session.Run([ imageInput; scaleFactorInput ])
            let tensor = outputTensor outputs
            let values = tensor |> Seq.toArray

            let boxCount =
                if tensor.Dimensions.Length >= 2 then
                    tensor.Dimensions[0]
                else
                    values.Length / 6

            let clusters =
                [ for index = 0 to boxCount - 1 do
                      let offset = index * 6

                      if offset + 5 < values.Length then
                          let classId = int (MathF.Round values[offset])
                          let score = values[offset + 1]

                          match labels |> Map.tryFind classId with
                          | Some rawLabel when score >= threshold && score >= thresholdFor rawLabel ->
                              let bbox =
                                  boxFor
                                      page.image
                                      values[offset + 2]
                                      values[offset + 3]
                                      values[offset + 4]
                                      values[offset + 5]

                              if bbox.r > bbox.l && bbox.b > bbox.t then
                                  { id = index
                                    label = labelFor rawLabel
                                    confidence = score
                                    bbox = bbox
                                    cells = [] }
                          | _ -> () ]
                |> applyNms
                |> List.truncate 100

            { pageNo = page.pageNo
              clusters = clusters }

        interface IDoclingLayoutPredictor with
            member _.PredictLayoutAsync pages =
                async {
                    try
                        let predictions =
                            lock gate (fun () ->
                                pages
                                |> List.mapi (fun index page ->
                                    let timer = Stopwatch.StartNew()
                                    let prediction = predictOne page
                                    timer.Stop()
                                    let elapsedSeconds = sprintf "%.1f" timer.Elapsed.TotalSeconds

                                    report
                                        $"Document structure layout page {index + 1}/{pages.Length} ({page.pageNo}) produced {prediction.clusters.Length} cluster(s) in {elapsedSeconds}s."

                                    prediction))

                        return Ok predictions
                    with ex ->
                        return Error $"PP-DocLayout-M ONNX prediction failed: {ex.Message}"
                }

        interface IDisposable with
            member _.Dispose() = session.Dispose()

    type private PpDocLayoutMProvider() =
        interface IDoclingLayoutModelProvider with
            member _.Id = "pp-doclayout-m"

            member _.DisplayName = "PP-DocLayout-M"

            member _.CreateAsync context =
                async {
                    try
                        context.cancellationToken.ThrowIfCancellationRequested()
                        use client = new HttpClient()
                        let! modelPath = ensurePpDocLayoutMDownloadedAsync client context.storageRoot
                        context.cancellationToken.ThrowIfCancellationRequested()

                        match modelPath with
                        | Error err -> return Error err
                        | Ok modelPath ->
                            let predictor = new PpDocLayoutMOnnx(modelPath, context.report)

                            return
                                Ok
                                    { predictor = predictor :> IDoclingLayoutPredictor
                                      disposable = Some(predictor :> IDisposable) }
                    with
                    | :? OperationCanceledException ->
                        return raise (OperationCanceledException context.cancellationToken)
                    | ex -> return Error $"Unable to initialize PP-DocLayout-M ONNX model: {ex.Message}"
                }

    type private HeronLayoutOnnx(files: DoclingOnnxModelFiles, report: string -> unit) =
        let width = 640
        let height = 640
        let threshold = 0.3f
        let session = new InferenceSession(files.modelPath)
        let gate = obj ()

        let inputName name =
            session.InputMetadata.Keys
            |> Seq.tryFind (fun key -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith $"Document structure Heron ONNX input '{name}' was not found.")

        let outputName name =
            session.OutputMetadata.Keys
            |> Seq.tryFind (fun key -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith $"Document structure Heron ONNX output '{name}' was not found.")

        let imageInputName = inputName "images"
        let targetSizesInputName = inputName "orig_target_sizes"
        let labelsOutputName = outputName "labels"
        let boxesOutputName = outputName "boxes"
        let scoresOutputName = outputName "scores"

        let labelFor classId =
            match classId with
            | 0 -> DoclingLabel.Caption
            | 1 -> DoclingLabel.Footnote
            | 2 -> DoclingLabel.Formula
            | 4 -> DoclingLabel.PageFooter
            | 5 -> DoclingLabel.PageHeader
            | 6 -> DoclingLabel.Picture
            | 7 -> DoclingLabel.SectionHeader
            | 8 -> DoclingLabel.Table
            | 10 -> DoclingLabel.Title
            | 12 -> DoclingLabel.Code
            | _ -> DoclingLabel.Text

        let resize (image: DoclingRgbImage) =
            use source = toBitmap image

            let resized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque)

            use canvas = new SKCanvas(resized)
            canvas.Clear(SKColors.White)
            canvas.DrawBitmap(source, SKRect(0f, 0f, float32 width, float32 height))

            resized

        let imageInput (image: DoclingRgbImage) =
            use bitmap = resize image
            let values = Array.zeroCreate<byte> (3 * width * height)
            let plane = width * height

            for y = 0 to height - 1 do
                for x = 0 to width - 1 do
                    let color = bitmap.GetPixel(x, y)
                    let offset = y * width + x
                    values[offset] <- color.Red
                    values[plane + offset] <- color.Green
                    values[(2 * plane) + offset] <- color.Blue

            DenseTensor<byte>(values, [| 1; 3; height; width |])
            |> fun tensor -> NamedOnnxValue.CreateFromTensor(imageInputName, tensor)

        let targetSizesInput (image: DoclingRgbImage) =
            let values = [| int64 image.height; int64 image.width |]

            DenseTensor<int64>(values, [| 1; 2 |])
            |> fun tensor -> NamedOnnxValue.CreateFromTensor(targetSizesInputName, tensor)

        let outputValue name (outputs: IDisposableReadOnlyCollection<DisposableNamedOnnxValue>) =
            outputs
            |> Seq.tryFind (fun output -> String.Equals(output.Name, name, StringComparison.Ordinal))
            |> Option.defaultWith (fun () ->
                failwith $"Document structure Heron ONNX output '{name}' was not returned.")

        let clamp limit value =
            Math.Min(float limit, Math.Max(0.0, float value))

        let boxFor (image: DoclingRgbImage) x1 y1 x2 y2 =
            { l = clamp image.width x1
              t = clamp image.height y1
              r = clamp image.width x2
              b = clamp image.height y2
              coordOrigin = DoclingCoordinateOrigin.TopLeft }

        let predictOne (page: DoclingPageInput) =
            let imageInput = imageInput page.image
            let targetSizesInput = targetSizesInput page.image
            use outputs = session.Run([ imageInput; targetSizesInput ])
            let labels = (outputValue labelsOutputName outputs).AsTensor<int64>()
            let boxes = (outputValue boxesOutputName outputs).AsTensor<float32>()
            let scores = (outputValue scoresOutputName outputs).AsTensor<float32>()

            let candidateCount =
                if scores.Dimensions.Length >= 2 then
                    scores.Dimensions[1]
                else
                    int scores.Length

            let clusters =
                [ for index = 0 to candidateCount - 1 do
                      let score = scores[0, index]

                      if score >= threshold then
                          let classId = int labels[0, index]
                          let offset = index * 4
                          let x1 = boxes.GetValue(offset) |> float
                          let y1 = boxes.GetValue(offset + 1) |> float
                          let x2 = boxes.GetValue(offset + 2) |> float
                          let y2 = boxes.GetValue(offset + 3) |> float
                          let bbox = boxFor page.image x1 y1 x2 y2

                          if bbox.r > bbox.l && bbox.b > bbox.t then
                              { id = index
                                label = labelFor classId
                                confidence = score
                                bbox = bbox
                                cells = [] } ]

            { pageNo = page.pageNo
              clusters = clusters |> List.sortByDescending _.confidence |> List.truncate 100 }

        interface IDoclingLayoutPredictor with
            member _.PredictLayoutAsync pages =
                async {
                    try
                        let predictions =
                            lock gate (fun () ->
                                pages
                                |> List.mapi (fun index page ->
                                    let timer = Stopwatch.StartNew()
                                    let prediction = predictOne page
                                    timer.Stop()

                                    report
                                        $"Document structure Heron layout page {index + 1}/{pages.Length} ({page.pageNo}) produced {prediction.clusters.Length} cluster(s) in {timer.Elapsed.TotalSeconds:F1}s."

                                    prediction))

                        return Ok predictions
                    with ex ->
                        return Error $"Document structure Heron layout prediction failed: {ex.Message}"
                }

        interface IDisposable with
            member _.Dispose() = session.Dispose()

    type private HeronLayoutProvider() =
        interface IDoclingLayoutModelProvider with
            member _.Id = "heron"

            member _.DisplayName = "Document structure Heron"

            member _.CreateAsync context =
                async {
                    try
                        context.cancellationToken.ThrowIfCancellationRequested()
                        use client = new HttpClient()

                        let! files =
                            ModelCatalog.ensureDoclingOnnxDownloadedAsync
                                client
                                (doclingModelFolder context.storageRoot "docling-layout-heron")
                                ModelCatalog.doclingLayoutHeronOnnx

                        context.cancellationToken.ThrowIfCancellationRequested()
                        let predictor = new HeronLayoutOnnx(files, context.report)

                        return
                            Ok
                                { predictor = predictor :> IDoclingLayoutPredictor
                                  disposable = Some(predictor :> IDisposable) }
                    with
                    | :? OperationCanceledException ->
                        return raise (OperationCanceledException context.cancellationToken)
                    | ex ->
                        return Error $"Unable to initialize document structure Heron layout ONNX model: {ex.Message}"
                }

    let ppDocLayoutMProvider () =
        PpDocLayoutMProvider() :> IDoclingLayoutModelProvider

    let heronLayoutProvider () =
        HeronLayoutProvider() :> IDoclingLayoutModelProvider

    let tryBuiltInLayoutProvider id =
        match (defaultArg (Option.ofObj id) "").Trim().ToLowerInvariant() with
        | ""
        | "pp"
        | "pp-doclayout"
        | "pp-doclayout-m"
        | "pp-doclayout-m-onnx" -> Some(ppDocLayoutMProvider ())
        | "heron"
        | "docling-heron"
        | "docling-layout-heron" -> Some(heronLayoutProvider ())
        | _ -> None

    let layoutModelFingerprint (options: DoclingHybridOptions) =
        let provider =
            options.layoutModelProvider |> Option.defaultWith ppDocLayoutMProvider

        let id = provider.Id |> normalizeFingerprintPart "unknown"
        $"pdfLayoutModel={id}"

    let rasterizerFingerprint () =
        let info = currentRasterizerInfo ()
        let id = info.id |> normalizeFingerprintPart "unknown"
        $"pdfRasterizer={id}"

    [<Literal>]
    let private nativeTextQualityHeuristicVersion = "native-text-quality-v2"

    let parserRuntimeFingerprint (options: DoclingHybridOptions) =
        let autoOcrFallbackEnabled = options.enableOcr && options.enableAutoOpticalParsing

        [ yield $"pdfLayoutAnalysis={options.enableLayoutAnalysis.ToString().ToLowerInvariant()}"
          yield $"pdfOpticalParsing={options.enableOcr.ToString().ToLowerInvariant()}"
          yield $"pdfAutoOcrFallback={autoOcrFallbackEnabled.ToString().ToLowerInvariant()}"
          if autoOcrFallbackEnabled then
              yield $"pdfNativeTextQualityHeuristic={nativeTextQualityHeuristicVersion}"
          if options.enableOcr then
              yield "pdfOcrEngine=RapidOcrNet"
              yield "pdfOcrModel=rapidocr/pp-ocrv5-latin"
          yield layoutModelFingerprint options
          yield rasterizerFingerprint () ]
        |> String.concat "\n"

    type private NativeTextQuality =
        { normalizedLength: int
          corruptionSignals: int
          failedSignals: int
          printableRatio: float
          suspiciousGlyphRatio: float
          privateUseRatio: float
          extendedLatinRatio: float
          extendedLatinRunRatio: float
          symbolRatio: float
          letterDigitRatio: float
          tokenCharRatio: float
          repeatedRunRatio: float
          looksGarbled: bool }

    let private ratio count total =
        if total <= 0 then 0.0 else float count / float total

    let private isPrivateUseCharacter (value: char) =
        let code = int value

        (code >= 0xE000 && code <= 0xF8FF)
        || (code >= 0xF0000 && code <= 0xFFFFD)
        || (code >= 0x100000 && code <= 0x10FFFD)

    let private isSuspiciousGlyph (value: char) =
        let code = int value

        code = 0xFFFD
        || Char.IsSurrogate value
        || (code >= 0x25A0 && code <= 0x25FF)
        || isPrivateUseCharacter value

    let private isSymbolCharacter (value: char) =
        match Char.GetUnicodeCategory value with
        | UnicodeCategory.CurrencySymbol
        | UnicodeCategory.MathSymbol
        | UnicodeCategory.ModifierSymbol
        | UnicodeCategory.OtherSymbol -> true
        | _ -> false

    let private isExtendedLatinLetter (value: char) =
        let code = int value

        Char.IsLetter value
        && ((code >= 0x00C0 && code <= 0x024F) || (code >= 0x1E00 && code <= 0x1EFF))

    let private runRatio predicate minRunLength (text: string) =
        if String.IsNullOrEmpty text then
            0.0
        else
            let chars = text.ToCharArray()
            let mutable runChars = 0
            let mutable runLength = 0

            let addRun length =
                if length >= minRunLength then
                    runChars <- runChars + length

            for value in chars do
                if predicate value then
                    runLength <- runLength + 1
                else
                    addRun runLength
                    runLength <- 0

            addRun runLength
            ratio runChars chars.Length

    let private repeatedRunRatio (text: string) =
        if String.IsNullOrEmpty text then
            0.0
        else
            let chars = text.ToCharArray()
            let mutable repeatedChars = 0
            let mutable previous = chars[0]
            let mutable runLength = 1

            let addRun length =
                if length >= 4 then
                    repeatedChars <- repeatedChars + length

            for index = 1 to chars.Length - 1 do
                let current = chars[index]

                if current = previous then
                    runLength <- runLength + 1
                else
                    addRun runLength
                    previous <- current
                    runLength <- 1

            addRun runLength
            ratio repeatedChars chars.Length

    let private cellsToNormalizedText (cells: DoclingOcrCell list) =
        cells |> List.map _.text |> String.concat " " |> Text.normalizeWhitespace

    let private nativeTextQuality minNativeCharsPerPage cells =
        let text = cellsToNormalizedText cells
        let charCount = text.Length

        if charCount = 0 then
            { normalizedLength = 0
              corruptionSignals = 0
              failedSignals = 0
              printableRatio = 1.0
              suspiciousGlyphRatio = 0.0
              privateUseRatio = 0.0
              extendedLatinRatio = 0.0
              extendedLatinRunRatio = 0.0
              symbolRatio = 0.0
              letterDigitRatio = 0.0
              tokenCharRatio = 0.0
              repeatedRunRatio = 0.0
              looksGarbled = false }
        else
            let chars = text.ToCharArray()

            let controlCount =
                chars
                |> Array.sumBy (fun value ->
                    if Char.IsControl value && not (Char.IsWhiteSpace value) then
                        1
                    else
                        0)

            let suspiciousGlyphCount =
                chars |> Array.sumBy (fun value -> if isSuspiciousGlyph value then 1 else 0)

            let privateUseCount =
                chars |> Array.sumBy (fun value -> if isPrivateUseCharacter value then 1 else 0)

            let extendedLatinCount =
                chars |> Array.sumBy (fun value -> if isExtendedLatinLetter value then 1 else 0)

            let symbolCount =
                chars |> Array.sumBy (fun value -> if isSymbolCharacter value then 1 else 0)

            let letterDigitCount =
                chars |> Array.sumBy (fun value -> if Char.IsLetterOrDigit value then 1 else 0)

            let tokenChars = Text.terms text |> List.sumBy _.Length
            let printableRatio = 1.0 - ratio controlCount charCount
            let suspiciousGlyphRatio = ratio suspiciousGlyphCount charCount
            let privateUseRatio = ratio privateUseCount charCount
            let extendedLatinRatio = ratio extendedLatinCount charCount
            let extendedLatinRunRatio = runRatio isExtendedLatinLetter 8 text
            let symbolRatio = ratio symbolCount charCount
            let letterDigitRatio = ratio letterDigitCount charCount
            let tokenCharRatio = ratio tokenChars charCount
            let repeatedRunRatio = repeatedRunRatio text

            let hasControlCharacters = controlCount >= 1 && charCount >= 8

            let hasSevereControlCorruption =
                controlCount >= 3 || (charCount >= 40 && printableRatio < 0.96)

            let hasSuspiciousGlyphs = suspiciousGlyphRatio >= 0.025 || suspiciousGlyphCount >= 2
            let hasPrivateUseCharacters = privateUseRatio >= 0.02 || privateUseCount >= 2
            let hasLowLetterDigitRatio = charCount >= 40 && letterDigitRatio < 0.30
            let hasLowTokenRatio = charCount >= 40 && tokenCharRatio < 0.12
            let hasRepeatedRuns = charCount >= 24 && repeatedRunRatio >= 0.20
            let isSymbolHeavy = charCount >= 40 && symbolRatio >= 0.35 && tokenCharRatio < 0.25

            let hasExtendedLatinAlphabetSoup =
                charCount >= 40 && extendedLatinRatio >= 0.25 && tokenCharRatio < 0.35

            let hasExtendedLatinRuns =
                charCount >= 40 && extendedLatinRunRatio >= 0.20 && tokenCharRatio < 0.45

            let corruptionSignals =
                [ hasControlCharacters
                  hasSuspiciousGlyphs
                  hasPrivateUseCharacters
                  hasRepeatedRuns
                  hasExtendedLatinAlphabetSoup
                  hasExtendedLatinRuns ]
                |> List.sumBy (fun failed -> if failed then 1 else 0)

            let failedSignals =
                [ hasControlCharacters
                  hasSuspiciousGlyphs
                  hasPrivateUseCharacters
                  hasLowLetterDigitRatio
                  hasLowTokenRatio
                  hasRepeatedRuns
                  isSymbolHeavy
                  hasExtendedLatinAlphabetSoup
                  hasExtendedLatinRuns ]
                |> List.sumBy (fun failed -> if failed then 1 else 0)

            { normalizedLength = charCount
              corruptionSignals = corruptionSignals
              failedSignals = failedSignals
              printableRatio = printableRatio
              suspiciousGlyphRatio = suspiciousGlyphRatio
              privateUseRatio = privateUseRatio
              extendedLatinRatio = extendedLatinRatio
              extendedLatinRunRatio = extendedLatinRunRatio
              symbolRatio = symbolRatio
              letterDigitRatio = letterDigitRatio
              tokenCharRatio = tokenCharRatio
              repeatedRunRatio = repeatedRunRatio
              looksGarbled =
                charCount >= max 24 minNativeCharsPerPage
                && ((corruptionSignals >= 1 && failedSignals >= 2) || hasSevereControlCorruption) }

    let nativeTextLooksGarbledForTesting minNativeCharsPerPage text =
        let cell: DoclingOcrCell =
            { text = text
              bbox = DoclingGeometry.topLeftBox 0.0 0.0 1.0 1.0
              confidence = None }

        (nativeTextQuality minNativeCharsPerPage [ cell ]).looksGarbled

    let private ocrQualityUsableForCorruptNative minNativeCharsPerPage ocrCells =
        let ocrQuality = nativeTextQuality minNativeCharsPerPage ocrCells

        let hasEnoughOcrText = DoclingCells.hasEnoughText minNativeCharsPerPage ocrCells

        hasEnoughOcrText && not ocrQuality.looksGarbled, ocrQuality

    let private createLayoutPredictor options report storageRoot cancellationToken =
        let provider =
            options.layoutModelProvider |> Option.defaultWith ppDocLayoutMProvider

        let context =
            { storageRoot = storageRoot
              report = report
              cancellationToken = cancellationToken }

        async {
            report $"Document structure parser using {provider.DisplayName} layout model."
            return! provider.CreateAsync context
        }

    let private loadFigureClassifier options storageRoot =
        async {
            if not options.enableFigureClassification then
                return Ok(None, None)
            else
                try
                    use client = new HttpClient()

                    let! files =
                        ModelCatalog.ensureDoclingOnnxDownloadedAsync
                            client
                            (doclingModelFolder storageRoot "docling-figure-classifier-v2.5")
                            ModelCatalog.doclingDocumentFigureClassifierV25Onnx

                    let classifier = new DoclingFigureClassifierOnnx(files)

                    return Ok(Some(classifier :> IDoclingFigureClassifier), Some(classifier :> IDisposable))
                with ex ->
                    return Error $"Unable to initialize document structure figure classifier ONNX model: {ex.Message}"
        }

    let buildPageInputsWithCancellation
        (options: DoclingHybridOptions)
        (report: string -> unit)
        (path: string)
        (rasterizer: IDoclingPageRasterizer)
        (nativeTextProvider: string -> Async<Result<DoclingNativePageText list, string>>)
        (ocrProvider: IDoclingOcrProvider option)
        (cancellationToken: CancellationToken)
        =
        async {
            throwIfCanceled cancellationToken

            let! rasterized =
                match rasterizer with
                | :? ICancelableDoclingPageRasterizer as cancelable ->
                    cancelable.RasterizeAsync(path, cancellationToken)
                | _ -> rasterizer.RasterizeAsync path
                |> timed
                    report
                    $"Document structure parser rasterizing {Path.GetFileName path} at {max 36 options.rasterDpi} DPI"
                |> withTimeout
                    45000
                    $"Document structure rasterization timed out for {Path.GetFileName path}; falling back to legacy PDF parsing."

            throwIfCanceled cancellationToken

            match rasterized with
            | Error err -> return Error err
            | Ok(rasterized: DoclingRasterPage list) ->
                report $"Document structure parser rasterized {rasterized.Length} page(s) for {Path.GetFileName path}."

                let pageSizes =
                    rasterized
                    |> List.truncate 12
                    |> List.map (fun page -> $"{page.pageNo}:{page.image.width}x{page.image.height}")
                    |> String.concat ", "

                let suffix = if rasterized.Length > 12 then ", ..." else ""

                report $"Document structure raster page sizes for {Path.GetFileName path}: {pageSizes}{suffix}."

                throwIfCanceled cancellationToken

                let! nativeResult =
                    nativeTextProvider path
                    |> timed report $"Document structure parser reading native PDF text for {Path.GetFileName path}"
                    |> withTimeout
                        30000
                        $"Document structure native text extraction timed out for {Path.GetFileName path}; falling back to legacy PDF parsing."

                throwIfCanceled cancellationToken

                match nativeResult with
                | Error err -> return Error err
                | Ok nativePages ->
                    report $"Document structure parser read native text from {nativePages.Length} page(s)."

                    let nativeByPage =
                        nativePages |> List.map (fun page -> page.pageNo, page) |> Map.ofList

                    let recognizeWithOcr page (ocrProvider: IDoclingOcrProvider) =
                        async {
                            let! ocrResult =
                                match ocrProvider with
                                | :? ICancelableDoclingOcrProvider as cancelable ->
                                    cancelable.RecognizeAsync(page, cancellationToken)
                                | _ -> ocrProvider.RecognizeAsync page
                                |> timed report $"Document structure OCR page {page.pageNo}"
                                |> withTimeout
                                    30000
                                    $"Document structure OCR timed out on page {page.pageNo}; falling back to legacy PDF parsing."

                            throwIfCanceled cancellationToken
                            return ocrResult
                        }

                    let availableOcrProvider = if options.enableOcr then ocrProvider else None

                    let autoOcrFallbackEnabled = options.enableOcr && options.enableAutoOpticalParsing

                    let rec loop (pages: DoclingRasterPage list) (acc: DoclingPageInput list) =
                        async {
                            match pages with
                            | [] -> return Ok(List.rev acc)
                            | (page: DoclingRasterPage) :: rest ->
                                throwIfCanceled cancellationToken

                                let nativePage =
                                    nativeByPage
                                    |> Map.tryFind page.pageNo
                                    |> Option.defaultValue
                                        { pageNo = page.pageNo
                                          size =
                                            { width = float page.image.width
                                              height = float page.image.height }
                                          cells = [] }

                                let nativeCells =
                                    DoclingCells.scaleCellsToImage nativePage.size page.image nativePage.cells

                                let nativeHasEnoughText =
                                    DoclingCells.hasEnoughText options.minNativeCharsPerPage nativeCells

                                let nativeQuality = nativeTextQuality options.minNativeCharsPerPage nativeCells

                                if nativeHasEnoughText then
                                    match autoOcrFallbackEnabled, nativeQuality.looksGarbled, availableOcrProvider with
                                    | true, true, Some ocrProvider ->
                                        let! ocrResult = recognizeWithOcr page ocrProvider

                                        match ocrResult with
                                        | Error err -> return Error err
                                        | Ok ocrCells ->
                                            let useOcr, _ =
                                                ocrQualityUsableForCorruptNative options.minNativeCharsPerPage ocrCells

                                            let selectedCells =
                                                if useOcr then
                                                    report
                                                        $"Auto OCR fallback page {page.pageNo}: native text looked garbled; using OCR text."

                                                    ocrCells
                                                else
                                                    report
                                                        $"Auto OCR fallback page {page.pageNo}: native text looked garbled, but OCR text was not usable; keeping native text."

                                                    nativeCells

                                            let input: DoclingPageInput =
                                                { pageNo = page.pageNo
                                                  image = page.image
                                                  ocrCells = selectedCells }

                                            return! loop rest (input :: acc)
                                    | _ ->
                                        if autoOcrFallbackEnabled && nativeQuality.looksGarbled then
                                            report
                                                $"Auto OCR fallback page {page.pageNo}: native text looked garbled, but OCR is not available; keeping native text."

                                        let input: DoclingPageInput =
                                            { pageNo = page.pageNo
                                              image = page.image
                                              ocrCells = nativeCells }

                                        return! loop rest (input :: acc)
                                else
                                    match availableOcrProvider with
                                    | None ->
                                        return
                                            Error
                                                $"Page {page.pageNo} has insufficient native PDF text and no RapidOCR model is configured."
                                    | Some ocrProvider ->
                                        let! ocrResult = recognizeWithOcr page ocrProvider

                                        match ocrResult with
                                        | Error err -> return Error err
                                        | Ok ocrCells ->
                                            let merged =
                                                if nativeQuality.corruptionSignals > 0 then
                                                    report
                                                        $"Auto OCR fallback page {page.pageNo}: sparse native text had corruption signals; using OCR text only."

                                                    ocrCells
                                                else
                                                    DoclingCells.mergePreferPrimary
                                                        (float page.image.height)
                                                        options.ocrDedupeOverlapThreshold
                                                        nativeCells
                                                        ocrCells

                                            let input: DoclingPageInput =
                                                { pageNo = page.pageNo
                                                  image = page.image
                                                  ocrCells = merged }

                                            return! loop rest (input :: acc)
                        }

                    return! loop rasterized []
        }

    let buildPageInputs options report path rasterizer nativeTextProvider ocrProvider =
        buildPageInputsWithCancellation
            options
            report
            path
            rasterizer
            nativeTextProvider
            ocrProvider
            CancellationToken.None

    let private convertPageInputsWithCancellation
        options
        report
        chunkOptions
        passageSource
        (path: string)
        layoutPredictor
        figureClassifier
        (pageInputs: DoclingPageInput list)
        (cancellationToken: CancellationToken)
        =
        async {
            throwIfCanceled cancellationToken
            let documentName = Path.GetFileNameWithoutExtension path
            report $"Document structure parser converting {pageInputs.Length} page(s) for {Path.GetFileName path}."

            let! document =
                DoclingStandardHybrid.convertPagesWithOptionsWithCancellation
                    DoclingConversionOptions.defaults
                    documentName
                    (Some(Path.GetFileName path))
                    layoutPredictor
                    figureClassifier
                    pageInputs
                    cancellationToken
                |> timed report $"Document structure layout conversion for {Path.GetFileName path}"
                |> withTimeout
                    (layoutConversionTimeoutMs pageInputs.Length)
                    $"Document structure layout conversion timed out for {Path.GetFileName path} after processing budget for {pageInputs.Length} page(s); falling back to legacy PDF parsing."

            throwIfCanceled cancellationToken

            match document with
            | Error err -> return Error err
            | Ok document ->
                let! document =
                    enrichDocumentWithVisualDescriptions
                        options.visualDescriptions
                        report
                        path
                        pageInputs
                        document
                        cancellationToken

                throwIfCanceled cancellationToken
                return document |> DoclingPassages.toPassages chunkOptions passageSource |> Ok
        }

    let private convertPageInputs
        options
        report
        chunkOptions
        passageSource
        path
        layoutPredictor
        figureClassifier
        pageInputs
        =
        convertPageInputsWithCancellation
            options
            report
            chunkOptions
            passageSource
            path
            layoutPredictor
            figureClassifier
            pageInputs
            CancellationToken.None

    let private convertNativeTextOnly
        announce
        report
        chunkOptions
        passageSource
        (path: string)
        (pageInputs: DoclingPageInput list)
        =
        let documentName = Path.GetFileNameWithoutExtension path

        let numberedHeadingPattern =
            Regex(@"^\s*(\d{1,2}(?:\.\d+)*)\s+(.{2,100})\s*$", RegexOptions.Compiled)

        let appendixHeadingPattern =
            Regex(@"^\s*([A-Z](?:\.\d+)+)\s+(.{2,100})\s*$", RegexOptions.Compiled)

        let dotLeaderPattern = Regex(@"(?:\.\s*){4,}", RegexOptions.Compiled)

        let decimalMetricPattern = Regex(@"\b\d+\.\d+\b", RegexOptions.Compiled)

        let knownStandaloneHeadingNames =
            [ "abstract"
              "acknowledgements"
              "acknowledgments"
              "ccs concepts"
              "conclusion"
              "conclusion and implications"
              "conclusions"
              "discussion"
              "experimental setup"
              "genai usage disclosure"
              "introduction"
              "methodology"
              "neurips paper checklist"
              "references"
              "related work"
              "results" ]
            |> Set.ofList

        let nativeHeadingTerms =
            [ "ablation"
              "abstract"
              "acknowledgements"
              "acknowledgments"
              "analysis"
              "appendix"
              "background"
              "baseline"
              "baselines"
              "conclusion"
              "conclusions"
              "construction"
              "dataset"
              "discussion"
              "evaluation"
              "evolution"
              "experiment"
              "experimental"
              "experiments"
              "generation"
              "implementation"
              "introduction"
              "limitation"
              "limitations"
              "method"
              "methodology"
              "metric"
              "overview"
              "references"
              "related"
              "results"
              "scaling"
              "setup"
              "template"
              "work" ]
            |> Set.ofList

        let normalizeLineText (cells: DoclingOcrCell list) =
            cells
            |> List.sortBy _.bbox.l
            |> List.map _.text
            |> String.concat " "
            |> Text.normalizeWhitespace

        let toTopLeftCell (pageHeight: float) (cell: DoclingOcrCell) : DoclingOcrCell =
            { cell with
                text = Text.normalizeWhitespace cell.text
                bbox = DoclingGeometry.toTopLeft pageHeight cell.bbox }

        let cellHeight (cell: DoclingOcrCell) =
            max 1.0 (DoclingGeometry.height cell.bbox)

        let sameLine (line: DoclingOcrCell list) (cell: DoclingOcrCell) =
            let lineTop = line |> List.averageBy (fun item -> item.bbox.t)
            let lineHeight = line |> List.averageBy cellHeight
            abs (cell.bbox.t - lineTop) <= max 3.0 (lineHeight * 0.6)

        let splitWideLineGaps (line: DoclingOcrCell list) : DoclingOcrCell list list =
            let sorted = line |> List.sortBy _.bbox.l
            let lineHeight = sorted |> List.averageBy cellHeight
            let gapThreshold = max 36.0 (lineHeight * 4.0)

            let rec loop
                (current: DoclingOcrCell list)
                (groups: DoclingOcrCell list list)
                (remaining: DoclingOcrCell list)
                =
                match current, remaining with
                | [], [] -> groups
                | _ :: _, [] -> (List.rev current) :: groups
                | [], cell :: rest -> loop [ cell ] groups rest
                | previous :: _, cell :: rest when cell.bbox.l - previous.bbox.r > gapThreshold ->
                    loop [ cell ] ((List.rev current) :: groups) rest
                | _, cell :: rest -> loop (cell :: current) groups rest

            loop [] [] sorted |> List.rev

        let groupCellsIntoLines (pageHeight: float) (cells: DoclingOcrCell list) : DoclingOcrCell list list =
            let rec addCell (cell: DoclingOcrCell) (lines: DoclingOcrCell list list) =
                match lines with
                | [] -> [ [ cell ] ]
                | line :: rest when sameLine line cell -> (cell :: line) :: rest
                | line :: rest -> line :: addCell cell rest

            cells
            |> List.choose (fun cell ->
                let cell = toTopLeftCell pageHeight cell

                if String.IsNullOrWhiteSpace cell.text then
                    None
                else
                    Some cell)
            |> List.sortBy (fun cell -> cell.bbox.t, cell.bbox.l)
            |> List.fold (fun lines cell -> addCell cell lines) []
            |> List.map (List.sortBy _.bbox.l)
            |> List.collect splitWideLineGaps
            |> List.filter (fun line -> not (String.IsNullOrWhiteSpace(normalizeLineText line)))

        let lineBox (cells: DoclingOcrCell list) : DoclingBoundingBox =
            let first = cells |> List.head

            cells
            |> List.tail
            |> List.fold
                (fun bbox (cell: DoclingOcrCell) ->
                    { bbox with
                        l = min bbox.l cell.bbox.l
                        t = min bbox.t cell.bbox.t
                        r = max bbox.r cell.bbox.r
                        b = max bbox.b cell.bbox.b })
                first.bbox

        let normalizedHeadingName (text: string) =
            FsColbert.DocumentSections.normalizedName text

        let hasNativeHeadingTerm (terms: string list) =
            terms |> List.exists nativeHeadingTerms.Contains

        let acronymLikeHeading (heading: string) =
            let letters = heading |> Seq.filter Char.IsLetter |> Seq.toArray

            if letters.Length = 0 then
                false
            else
                let upper = letters |> Array.filter Char.IsUpper |> Array.length
                float upper / float letters.Length >= 0.8

        let firstNumberWithinReasonableRange (numbering: string) =
            let firstPart =
                numbering.Split('.', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead

            match firstPart |> Option.map Int32.TryParse with
            | Some(true, value) -> value >= 1 && value <= 20
            | _ -> false

        let validNumberedHeading (numbering: string) (heading: string) =
            let heading = Text.normalizeWhitespace heading
            let terms = Text.terms heading
            let digitCount = heading |> Seq.filter Char.IsDigit |> Seq.length
            let digitRatio = float digitCount / float (max 1 heading.Length)
            let hasHeadingTerm = hasNativeHeadingTerm terms

            not (String.IsNullOrWhiteSpace heading)
            && firstNumberWithinReasonableRange numbering
            && heading.Length >= 3
            && heading.Length <= 80
            && terms.Length >= 1
            && terms.Length <= 8
            && digitRatio <= 0.1
            && not (dotLeaderPattern.IsMatch heading)
            && not (decimalMetricPattern.IsMatch heading)
            && (not (acronymLikeHeading heading) || hasHeadingTerm)

        let validAppendixHeading (heading: string) =
            let heading = Text.normalizeWhitespace heading
            let terms = Text.terms heading
            let digitCount = heading |> Seq.filter Char.IsDigit |> Seq.length
            let digitRatio = float digitCount / float (max 1 heading.Length)
            let hasHeadingTerm = hasNativeHeadingTerm terms

            not (String.IsNullOrWhiteSpace heading)
            && heading.Length >= 3
            && heading.Length <= 80
            && terms.Length >= 1
            && terms.Length <= 8
            && digitRatio <= 0.1
            && not (dotLeaderPattern.IsMatch heading)
            && not (decimalMetricPattern.IsMatch heading)
            && (not (acronymLikeHeading heading) || hasHeadingTerm)

        let privateLineHeading (text: string) =
            let trimmed = Text.normalizeWhitespace text
            let normalized = normalizedHeadingName trimmed
            let terms = Text.terms trimmed
            let digitCount = trimmed |> Seq.filter Char.IsDigit |> Seq.length
            let digitRatio = float digitCount / float (max 1 trimmed.Length)

            let numberedMatch = numberedHeadingPattern.Match trimmed
            let appendixMatch = appendixHeadingPattern.Match trimmed

            trimmed.Length >= 4
            && trimmed.Length <= 120
            && terms.Length <= 10
            && digitRatio <= 0.35
            && not (dotLeaderPattern.IsMatch trimmed)
            && (if numberedMatch.Success then
                    validNumberedHeading numberedMatch.Groups[1].Value numberedMatch.Groups[2].Value
                elif appendixMatch.Success then
                    validAppendixHeading appendixMatch.Groups[2].Value
                else
                    knownStandaloneHeadingNames.Contains normalized)

        let nativeTitleCandidate (text: string) =
            let trimmed = Text.normalizeWhitespace text

            let terms = Text.terms trimmed

            trimmed.Length >= 24
            && trimmed.Length <= 180
            && terms.Length >= 4
            && terms.Length <= 24
            && not (trimmed.StartsWith("arXiv:", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.Contains("@", StringComparison.Ordinal))
            && not (trimmed.EndsWith(".", StringComparison.Ordinal))
            && (trimmed.Contains(":", StringComparison.Ordinal)
                || trimmed.Contains(" - ", StringComparison.Ordinal))

        let lineLabel (titleAssigned: bool) (pageNo: int) (firstPage: int) (text: string) =
            if not titleAssigned && pageNo = firstPage && nativeTitleCandidate text then
                DoclingLabel.Title, true
            elif privateLineHeading text then
                DoclingLabel.SectionHeader, titleAssigned
            else
                DoclingLabel.Text, titleAssigned

        let pages =
            pageInputs
            |> List.map (fun page ->
                page.pageNo,
                { pageNo = page.pageNo
                  size =
                    { width = float page.image.width
                      height = float page.image.height } })
            |> Map.ofList

        let firstPage =
            pageInputs
            |> List.map _.pageNo
            |> List.sort
            |> List.tryHead
            |> Option.defaultValue 1

        let textItems =
            let mutable titleAssigned = false
            let mutable textIndex = 0

            [ for page in pageInputs |> List.sortBy _.pageNo do
                  let pageHeight = float page.image.height

                  for line in groupCellsIntoLines pageHeight page.ocrCells do
                      let text = normalizeLineText line

                      match Text.notEmpty text with
                      | None -> ()
                      | Some text ->
                          let label, assigned = lineLabel titleAssigned page.pageNo firstPage text
                          titleAssigned <- assigned

                          let selfRef = $"#/texts/{textIndex}"
                          textIndex <- textIndex + 1

                          { selfRef = selfRef
                            parent = "#/body"
                            label = label
                            text = text
                            orig = text
                            contentLayer = DoclingContentLayer.Body
                            prov =
                              [ { pageNo = page.pageNo
                                  bbox = lineBox line
                                  charSpan = None } ]
                            keywords = []
                            sourceId = None
                            sourceDisplayName = None } ]

        if List.isEmpty textItems then
            Error $"Document structure native-text conversion found no readable text in {Path.GetFileName path}."
        else
            if announce then
                report
                    $"Document structure parser using fast native-text conversion for {Path.GetFileName path}; skipping layout ONNX."

            { name = documentName
              originFileName = Some(Path.GetFileName path)
              originMimeType = Some "application/pdf"
              pages = pages
              texts = textItems
              tables = []
              pictures = []
              bodyChildren = textItems |> List.map _.selfRef
              furnitureChildren = [] }
            |> DoclingPassages.toPassages chunkOptions passageSource
            |> Ok

    let private passageTextLength (passages: PassageRef list) =
        passages
        |> List.sumBy (fun passage ->
            if isNull passage.text then
                0L
            else
                int64 passage.text.Length)

    let private containsVisualDescription (passage: PassageRef) =
        not (isNull passage.text)
        && passage.text.Contains("[Visual description:", StringComparison.Ordinal)

    let private appendVisualDescriptionPassages (layoutPassages: PassageRef list) (nativePassages: PassageRef list) =
        let visualPassages =
            layoutPassages
            |> List.filter containsVisualDescription
            |> List.distinctBy _.text
            |> List.mapi (fun offset passage ->
                { passage with
                    index = nativePassages.Length + offset })

        nativePassages @ visualPassages

    let private distinctNonEmpty values =
        values
        |> List.choose (fun value ->
            value
            |> Text.normalizeWhitespace
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not))
        |> List.distinctBy _.ToLowerInvariant()

    let private isIgnoredLayoutLabel (label: string) =
        match (defaultArg (Option.ofObj label) "").Trim().ToLowerInvariant() with
        | ""
        | "text"
        | "paragraph"
        | "page_header"
        | "page_footer" -> true
        | _ -> false

    let private layoutKeywordTerms labels captions =
        distinctNonEmpty
            [ yield! labels
              yield! (labels |> List.map (fun label -> label.Replace("_", " ")))
              yield! captions ]

    let private pagesOverlap left right =
        match left, right with
        | [], _
        | _, [] -> false
        | _ ->
            let right = Set.ofList right
            left |> List.exists right.Contains

    let private normalizedSectionName value =
        FsColbert.DocumentSections.normalizedName value

    let private isNeuripsChecklistName value =
        String.Equals(normalizedSectionName value, "neurips paper checklist", StringComparison.Ordinal)

    let private sectionPathContainsChecklist sectionPath =
        sectionPath |> List.exists isNeuripsChecklistName

    let private firstNonEmptyLine text =
        (defaultArg (Option.ofObj text) "")
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryHead

    let private textStartsChecklistHeader text =
        firstNonEmptyLine text
        |> Option.exists (fun line ->
            isNeuripsChecklistName line
            || line.Contains("NeurIPS Paper Checklist", StringComparison.OrdinalIgnoreCase))

    let private passageHasChecklistHeader (passage: PassageRef) =
        sectionPathContainsChecklist passage.sectionPath
        || textStartsChecklistHeader passage.text

    let private normalizedLastSection sectionPath =
        sectionPath
        |> List.tryLast
        |> Option.map normalizedSectionName
        |> Option.defaultValue ""

    let private checklistInternalSectionNames =
        [ "answer: [yes]"
          "answer: [no]"
          "answer: [na]"
          "broader impacts"
          "claims"
          "code of ethics"
          "crowdsourcing and research with human subjects"
          "experimental result reproducibility"
          "experimental setting/details"
          "experiments compute resources"
          "experiments statistical significance"
          "guidelines"
          "justification"
          "limitations"
          "licenses for existing assets"
          "new assets"
          "open access to data and code"
          "safeguards"
          "theory assumptions and proofs" ]
        |> Set.ofList

    let private clearResumeSectionNames =
        [ "acknowledgements"
          "acknowledgments"
          "appendix"
          "bibliography"
          "genai usage disclosure"
          "references"
          "supplementary material" ]
        |> Set.ofList

    let private looksLikeChecklistInternalSection sectionPath =
        let name = normalizedLastSection sectionPath

        checklistInternalSectionNames.Contains name
        || name.StartsWith("answer", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("question", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("guidelines", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("justification", StringComparison.OrdinalIgnoreCase)

    let private isClearResumeSection sectionPath =
        let name = normalizedLastSection sectionPath

        clearResumeSectionNames.Contains name
        || name.StartsWith("appendix", StringComparison.OrdinalIgnoreCase)

    let private pickLayoutSectionPath (nativePassage: PassageRef) (layoutCandidates: PassageRef list) =
        let allowChecklistPath =
            passageHasChecklistHeader nativePassage
            || sectionPathContainsChecklist nativePassage.sectionPath

        layoutCandidates
        |> List.choose (fun passage ->
            match passage.sectionPath with
            | [] -> None
            | path when allowChecklistPath || not (sectionPathContainsChecklist path) -> Some path
            | _ -> None)
        |> List.sortByDescending List.length
        |> List.tryHead

    let private enrichNativePassagesWithLayout (layoutPassages: PassageRef list) (nativePassages: PassageRef list) =
        nativePassages
        |> List.map (fun nativePassage ->
            let layoutCandidates =
                layoutPassages
                |> List.filter (fun layoutPassage -> pagesOverlap nativePassage.pageNumbers layoutPassage.pageNumbers)

            if List.isEmpty layoutCandidates then
                nativePassage
            else
                let layoutLabels =
                    layoutCandidates
                    |> List.collect _.layoutLabels
                    |> List.filter (isIgnoredLayoutLabel >> not)
                    |> distinctNonEmpty

                let captions =
                    layoutCandidates
                    |> List.collect _.captions
                    |> distinctNonEmpty
                    |> List.truncate 8

                let sectionPath =
                    match pickLayoutSectionPath nativePassage layoutCandidates with
                    | Some layoutPath when
                        List.isEmpty nativePassage.sectionPath
                        || List.length layoutPath > List.length nativePassage.sectionPath
                        ->
                        layoutPath
                    | Some layoutPath when passageHasChecklistHeader nativePassage -> layoutPath
                    | _ -> nativePassage.sectionPath

                let keywords =
                    nativePassage.keywords @ layoutKeywordTerms layoutLabels captions
                    |> distinctNonEmpty

                { nativePassage with
                    sectionPath = sectionPath
                    contentRole = FsColbert.DocumentContentRoles.infer sectionPath nativePassage.text
                    layoutLabels = nativePassage.layoutLabels @ layoutLabels |> distinctNonEmpty
                    captions = nativePassage.captions @ captions |> distinctNonEmpty |> List.truncate 8
                    keywords = keywords })

    let private shouldResumeAfterChecklist markerSectionPath (passage: PassageRef) =
        not (passageHasChecklistHeader passage)
        && not (sectionPathContainsChecklist passage.sectionPath)
        && not (looksLikeChecklistInternalSection passage.sectionPath)
        && isClearResumeSection passage.sectionPath
        && match markerSectionPath, passage.sectionPath with
           | Some markerPath, path when not (List.isEmpty path) -> path <> markerPath
           | None, path -> not (List.isEmpty path)
           | _ -> false

    let private dropNeuripsChecklistSection (passages: PassageRef list) =
        let rec loop droppingMarkerPath kept remaining =
            match remaining with
            | [] -> List.rev kept
            | passage :: rest ->
                match droppingMarkerPath with
                | None ->
                    if passageHasChecklistHeader passage then
                        loop (Some passage.sectionPath) kept rest
                    else
                        loop None (passage :: kept) rest
                | Some markerPath ->
                    if shouldResumeAfterChecklist (Some markerPath) passage then
                        loop None (passage :: kept) rest
                    else
                        loop (Some markerPath) kept rest

        loop None [] passages
        |> List.mapi (fun index passage -> { passage with index = index })

    let private dropChecklistAndReport report fileName label passages =
        let kept = dropNeuripsChecklistSection passages
        let dropped = passages.Length - kept.Length

        if dropped > 0 then
            report $"Dropped {dropped} {label} passage(s) under NeurIPS Paper Checklist for {fileName}."

        kept

    let private layoutFallbackReason (layoutPassages: PassageRef list) (nativePassages: PassageRef list) =
        let layoutChars = passageTextLength layoutPassages
        let nativeChars = passageTextLength nativePassages

        if layoutPassages.Length <= 1 then
            Some $"Document structure layout conversion produced only {layoutPassages.Length} passage(s)"
        elif nativePassages.Length > layoutPassages.Length && nativeChars >= 2000L then
            let materiallyLessText = layoutChars * 10L < nativeChars * 9L

            let droppedTextWithFewerPassages =
                layoutChars < nativeChars && nativePassages.Length - layoutPassages.Length >= 5

            if materiallyLessText || droppedTextWithFewerPassages then
                Some
                    $"Document structure layout conversion looked incomplete ({layoutPassages.Length} passage(s), {layoutChars} chars vs {nativeChars} native chars)"
            else
                None
        else
            None

    let private keepLayoutUnlessCollapsed
        report
        chunkOptions
        passageSource
        (path: string)
        pageInputs
        (layoutResult: Result<PassageRef list, string>)
        =
        match layoutResult with
        | Error err -> Error err
        | Ok layoutPassages ->
            let fileName = Path.GetFileName path

            match convertNativeTextOnly false report chunkOptions passageSource path pageInputs with
            | Ok nativePassages ->
                let nativePassages = enrichNativePassagesWithLayout layoutPassages nativePassages

                match layoutFallbackReason layoutPassages nativePassages with
                | None -> Ok(dropChecklistAndReport report fileName "layout" layoutPassages)
                | Some reason ->
                    report $"{reason} for {fileName}; checking native-text conversion."

                    if
                        nativePassages.Length > layoutPassages.Length
                        || passageTextLength nativePassages > passageTextLength layoutPassages
                    then
                        report
                            $"Using document structure native-text conversion for {fileName} because it produced {nativePassages.Length} passage(s) and {passageTextLength nativePassages} chars."

                        appendVisualDescriptionPassages layoutPassages nativePassages
                        |> dropChecklistAndReport report fileName "native-text"
                        |> Ok
                    else
                        report
                            $"Keeping document structure layout conversion for {fileName}; native-text conversion produced {nativePassages.Length} passage(s) and {passageTextLength nativePassages} chars."

                        Ok(dropChecklistAndReport report fileName "layout" layoutPassages)
            | Error nativeError ->
                if layoutPassages.Length <= 1 then
                    report
                        $"Document structure native-text fallback failed for {fileName}; keeping layout result: {nativeError}"

                Ok(dropChecklistAndReport report fileName "layout" layoutPassages)

    let readPdfPassagesWithProvidersAndCancellation
        options
        report
        chunkOptions
        passageSource
        (path: string)
        rasterizer
        nativeTextProvider
        ocrProvider
        layoutPredictor
        figureClassifier
        cancellationToken
        =
        async {
            throwIfCanceled cancellationToken

            let! pageInputs =
                buildPageInputsWithCancellation
                    options
                    report
                    path
                    rasterizer
                    nativeTextProvider
                    ocrProvider
                    cancellationToken

            match pageInputs with
            | Error err -> return Error err
            | Ok pageInputs ->
                throwIfCanceled cancellationToken

                if options.enableLayoutAnalysis then
                    let! layoutResult =
                        convertPageInputsWithCancellation
                            options
                            report
                            chunkOptions
                            passageSource
                            path
                            layoutPredictor
                            figureClassifier
                            pageInputs
                            cancellationToken

                    return keepLayoutUnlessCollapsed report chunkOptions passageSource path pageInputs layoutResult
                else
                    throwIfCanceled cancellationToken
                    return convertNativeTextOnly true report chunkOptions passageSource path pageInputs
        }

    let readPdfPassagesWithProviders
        options
        report
        chunkOptions
        passageSource
        path
        rasterizer
        nativeTextProvider
        ocrProvider
        layoutPredictor
        figureClassifier
        =
        readPdfPassagesWithProvidersAndCancellation
            options
            report
            chunkOptions
            passageSource
            path
            rasterizer
            nativeTextProvider
            ocrProvider
            layoutPredictor
            figureClassifier
            CancellationToken.None

    let private readPdfPassagesWithCancellation
        options
        report
        storageRoot
        chunkOptions
        passageSource
        (path: string)
        (cancellationToken: CancellationToken)
        =
        async {
            throwIfCanceled cancellationToken

            let options =
                { options with
                    visualDescriptions =
                        { options.visualDescriptions with
                            cacheStorageRoot =
                                options.visualDescriptions.cacheStorageRoot |> Option.orElse (Some storageRoot) } }

            let rasterizer = createRasterizer options
            report $"Document structure PDF parser starting for {Path.GetFileName path}."
            let rasterizerInfo = currentRasterizerInfo ()
            let layoutFingerprint = layoutModelFingerprint options

            report
                $"Document structure PDF parser rasterizer: {rasterizerInfo.displayName} ({rasterizerInfo.id}); dpi={max 36 options.rasterDpi}; {layoutFingerprint}."

            let ocrProviderResult = tryCreateRapidOcrProvider options storageRoot

            report (
                match ocrProviderResult with
                | Ok(Some _) -> "Document structure OCR provider is enabled."
                | Ok None -> "Document structure OCR provider is not configured; native PDF text will be required."
                | Error err -> $"Document structure OCR provider setup failed: {err}"
            )

            match ocrProviderResult with
            | Error err -> return Error err
            | Ok ocrProvider ->
                throwIfCanceled cancellationToken

                let disposableOcr =
                    ocrProvider |> Option.map (fun provider -> provider :?> IDisposable)

                try
                    let! pageInputs =
                        buildPageInputsWithCancellation
                            options
                            report
                            path
                            rasterizer
                            DoclingPdfNative.readPageCells
                            ocrProvider
                            cancellationToken

                    match pageInputs with
                    | Error err -> return Error err
                    | Ok pageInputs ->
                        throwIfCanceled cancellationToken

                        if options.enableLayoutAnalysis then
                            let! layout =
                                createLayoutPredictor options report storageRoot cancellationToken
                                |> timed report "Document structure preparing layout ONNX model"
                                |> withTimeout
                                    60000
                                    "Document structure layout model preparation timed out; falling back to legacy PDF parsing."

                            match layout with
                            | Error err -> return Error err
                            | Ok layout ->
                                throwIfCanceled cancellationToken

                                try
                                    let! figure =
                                        loadFigureClassifier options storageRoot
                                        |> timed report "Document structure preparing figure classifier"
                                        |> withTimeout
                                            30000
                                            "Document structure figure classifier preparation timed out; falling back to legacy PDF parsing."

                                    match figure with
                                    | Error err -> return Error err
                                    | Ok(figureClassifier, figureDisposable) ->
                                        throwIfCanceled cancellationToken

                                        try
                                            let! layoutResult =
                                                convertPageInputsWithCancellation
                                                    options
                                                    report
                                                    chunkOptions
                                                    passageSource
                                                    path
                                                    layout.predictor
                                                    figureClassifier
                                                    pageInputs
                                                    cancellationToken

                                            return
                                                keepLayoutUnlessCollapsed
                                                    report
                                                    chunkOptions
                                                    passageSource
                                                    path
                                                    pageInputs
                                                    layoutResult
                                        finally
                                            figureDisposable |> Option.iter (fun disposable -> disposable.Dispose())
                                finally
                                    layout.disposable |> Option.iter (fun disposable -> disposable.Dispose())
                        else
                            throwIfCanceled cancellationToken
                            return convertNativeTextOnly true report chunkOptions passageSource path pageInputs
                finally
                    disposableOcr |> Option.iter (fun disposable -> disposable.Dispose())
        }

    let private readPdfPassages options report storageRoot chunkOptions passageSource path =
        readPdfPassagesWithCancellation
            options
            report
            storageRoot
            chunkOptions
            passageSource
            path
            CancellationToken.None

    let fallbackToLegacy
        (report: string -> unit)
        (path: string)
        (hybrid: Async<Result<PassageRef list, string>>)
        (legacyReader: unit -> Async<Result<PassageRef list, string>>)
        =
        async {
            match! hybrid with
            | Ok passages ->
                let fileName = Path.GetFileName path
                report $"Document structure PDF parser produced {passages.Length} passage(s) for {fileName}."

                if passages.Length > 1 then
                    return Ok passages
                else
                    report
                        $"Document structure parser produced only {passages.Length} passage(s) for {fileName}; checking PdfPig fallback chunking."

                    let! legacy = legacyReader ()

                    return
                        match legacy with
                        | Ok legacyPassages when legacyPassages.Length > passages.Length ->
                            report
                                $"Using PdfPig fallback for {fileName} because it produced {legacyPassages.Length} passage(s)."

                            Ok legacyPassages
                        | Ok _ -> Ok passages
                        | Error legacyError ->
                            report
                                $"PdfPig fallback check failed for {fileName}; keeping document structure result: {legacyError}"

                            Ok passages
            | Error hybridError ->
                report $"Document structure PDF parser failed for {Path.GetFileName path}: {hybridError}"
                report $"Falling back to PdfPig text extraction for {Path.GetFileName path}."
                let! legacy = legacyReader ()

                return
                    match legacy with
                    | Ok passages -> Ok passages
                    | Error legacyError ->
                        Error
                            $"Document structure PDF parser failed: {hybridError}{Environment.NewLine}Legacy PdfPig parser also failed: {legacyError}"
        }

    let readPdfPassagesWithOptionsWithFallback
        options
        storageRoot
        report
        chunkOptions
        passageSource
        path
        (legacyReader: unit -> Async<Result<PassageRef list, string>>)
        =
        async {
            let hybrid =
                async {
                    try
                        return! readPdfPassages options report storageRoot chunkOptions passageSource path
                    with ex ->
                        return Error $"Document structure PDF parser could not start: {ex.Message}"
                }

            return! fallbackToLegacy report path hybrid legacyReader
        }

    let readPdfPassagesWithFallback
        storageRoot
        report
        chunkOptions
        passageSource
        path
        (legacyReader: unit -> Async<Result<PassageRef list, string>>)
        =
        readPdfPassagesWithOptionsWithFallback
            activeDefaults
            storageRoot
            report
            chunkOptions
            passageSource
            path
            legacyReader

    let readPdfPassagesWithOptionsWithFallbackAndCancellation
        options
        storageRoot
        report
        chunkOptions
        passageSource
        path
        (legacyReader: unit -> Async<Result<PassageRef list, string>>)
        (cancellationToken: CancellationToken)
        =
        async {
            throwIfCanceled cancellationToken

            let hybrid =
                async {
                    try
                        return!
                            readPdfPassagesWithCancellation
                                options
                                report
                                storageRoot
                                chunkOptions
                                passageSource
                                path
                                cancellationToken
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException cancellationToken)
                    | ex -> return Error $"Document structure PDF parser could not start: {ex.Message}"
                }

            throwIfCanceled cancellationToken
            return! fallbackToLegacy report path hybrid legacyReader
        }

    let readPdfPassagesWithFallbackAndCancellation
        storageRoot
        report
        chunkOptions
        passageSource
        path
        (legacyReader: unit -> Async<Result<PassageRef list, string>>)
        (cancellationToken: CancellationToken)
        =
        readPdfPassagesWithOptionsWithFallbackAndCancellation
            activeDefaults
            storageRoot
            report
            chunkOptions
            passageSource
            path
            legacyReader
            cancellationToken
