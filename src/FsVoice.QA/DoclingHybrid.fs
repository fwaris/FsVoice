namespace FsVoice.QA

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Reflection
open System.Security.Cryptography
open System.Threading
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open FsColbert
open RapidOcrNet
open SkiaSharp

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

type DoclingHybridOptions =
    { minNativeCharsPerPage: int
      ocrDedupeOverlapThreshold: float
      rasterDpi: int
      enableOcr: bool
      enableLayoutAnalysis: bool
      enableFigureClassification: bool
      layoutModelProvider: IDoclingLayoutModelProvider option }

module DoclingHybrid =
    let defaults =
        { minNativeCharsPerPage = 24
          ocrDedupeOverlapThreshold = 0.75
          rasterDpi = 96
          enableOcr = true
          enableLayoutAnalysis = true
          enableFigureClassification = false
          layoutModelProvider = None }

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
          Path.Combine(fsColbertRoot storageRoot, "Models", "rapidocr")
          Path.Combine(storageRoot, "models", "rapidocr") ]
        |> List.choose Text.notEmpty
        |> List.distinctBy _.ToLowerInvariant()

    let private toBitmap (image: DoclingRgbImage) =
        DoclingRgbImage.validate image

        let bitmap =
            new SKBitmap(image.width, image.height, SKColorType.Rgba8888, SKAlphaType.Opaque)

        for y = 0 to image.height - 1 do
            for x = 0 to image.width - 1 do
                let offset = (y * image.width + x) * 3

                bitmap.SetPixel(
                    x,
                    y,
                    SKColor(image.pixels[offset], image.pixels[offset + 1], image.pixels[offset + 2], 255uy)
                )

        bitmap

    let mutable private rasterizerFactory: (DoclingHybridOptions -> IDoclingPageRasterizer) option =
        None

    let setRasterizerFactory factory = rasterizerFactory <- Some factory

    let clearRasterizerFactory () = rasterizerFactory <- None

    let private createRasterizer options =
        match rasterizerFactory with
        | Some factory -> factory options
        | None ->
            { new IDoclingPageRasterizer with
                member _.RasterizeAsync path =
                    async {
                        return
                            Error
                                $"No Docling PDF rasterizer is registered for '{path}'. Reference FsVoice.PdfRasterization and call PdfRasterizer.register() before using Hybrid PDF parsing."
                    } }

    type private RapidOcrProvider(ocr: RapidOcr) =
        let gate = obj ()

        let textBlockCell (block: TextBlock) =
            let text = block.GetText() |> Text.normalizeWhitespace

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
        "FsVoice.QA.Resources.Models.pp-doclayout-m.onnx"

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

        let boxArea bbox =
            max 0.0 (bbox.r - bbox.l) * max 0.0 (bbox.b - bbox.t)

        let intersectionOverUnion left right =
            let l = max left.l right.l
            let t = max left.t right.t
            let r = min left.r right.r
            let b = min left.b right.b
            let intersection = max 0.0 (r - l) * max 0.0 (b - t)
            let union = boxArea left + boxArea right - intersection

            if union <= 0.0 then 0.0 else intersection / union

        let applyNms clusters =
            let sorted = clusters |> List.sortByDescending (fun cluster -> cluster.confidence)

            let rec loop kept remaining =
                match remaining with
                | [] -> List.rev kept
                | cluster :: rest ->
                    let shouldKeep =
                        kept
                        |> List.forall (fun keptCluster ->
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
                                        $"Docling hybrid layout page {index + 1}/{pages.Length} ({page.pageNo}) produced {prediction.clusters.Length} cluster(s) in {elapsedSeconds}s."

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
            |> Option.defaultWith (fun () -> failwith $"Docling Heron ONNX input '{name}' was not found.")

        let outputName name =
            session.OutputMetadata.Keys
            |> Seq.tryFind (fun key -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            |> Option.defaultWith (fun () -> failwith $"Docling Heron ONNX output '{name}' was not found.")

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
            |> Option.defaultWith (fun () -> failwith $"Docling Heron ONNX output '{name}' was not returned.")

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
                                        $"Docling Heron layout page {index + 1}/{pages.Length} ({page.pageNo}) produced {prediction.clusters.Length} cluster(s) in {timer.Elapsed.TotalSeconds:F1}s."

                                    prediction))

                        return Ok predictions
                    with ex ->
                        return Error $"Docling Heron layout prediction failed: {ex.Message}"
                }

        interface IDisposable with
            member _.Dispose() = session.Dispose()

    type private HeronLayoutProvider() =
        interface IDoclingLayoutModelProvider with
            member _.Id = "heron"

            member _.DisplayName = "Docling Heron"

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
                    | ex -> return Error $"Unable to initialize Docling Heron layout ONNX model: {ex.Message}"
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

    let private createLayoutPredictor options report storageRoot cancellationToken =
        let provider =
            options.layoutModelProvider |> Option.defaultWith ppDocLayoutMProvider

        let context =
            { storageRoot = storageRoot
              report = report
              cancellationToken = cancellationToken }

        async {
            report $"Docling hybrid using {provider.DisplayName} layout model."
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
                    return Error $"Unable to initialize Docling figure classifier ONNX model: {ex.Message}"
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
                |> timed report $"Docling hybrid rasterizing {Path.GetFileName path} at {max 36 options.rasterDpi} DPI"
                |> withTimeout
                    45000
                    $"Docling hybrid rasterization timed out for {Path.GetFileName path}; falling back to legacy PDF parsing."

            throwIfCanceled cancellationToken

            match rasterized with
            | Error err -> return Error err
            | Ok(rasterized: DoclingRasterPage list) ->
                report $"Docling hybrid rasterized {rasterized.Length} page(s) for {Path.GetFileName path}."

                throwIfCanceled cancellationToken

                let! nativeResult =
                    nativeTextProvider path
                    |> timed report $"Docling hybrid reading native PDF text for {Path.GetFileName path}"
                    |> withTimeout
                        30000
                        $"Docling hybrid native text extraction timed out for {Path.GetFileName path}; falling back to legacy PDF parsing."

                throwIfCanceled cancellationToken

                match nativeResult with
                | Error err -> return Error err
                | Ok nativePages ->
                    report $"Docling hybrid read native text from {nativePages.Length} page(s)."

                    let nativeByPage =
                        nativePages |> List.map (fun page -> page.pageNo, page) |> Map.ofList

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

                                if DoclingCells.hasEnoughText options.minNativeCharsPerPage nativeCells then
                                    let input: DoclingPageInput =
                                        { pageNo = page.pageNo
                                          image = page.image
                                          ocrCells = nativeCells }

                                    return! loop rest (input :: acc)
                                else
                                    match ocrProvider with
                                    | None ->
                                        return
                                            Error
                                                $"Page {page.pageNo} has insufficient native PDF text and no RapidOCR model is configured."
                                    | Some ocrProvider ->
                                        let! ocrResult =
                                            match ocrProvider with
                                            | :? ICancelableDoclingOcrProvider as cancelable ->
                                                cancelable.RecognizeAsync(page, cancellationToken)
                                            | _ -> ocrProvider.RecognizeAsync page
                                            |> timed report $"Docling hybrid OCR page {page.pageNo}"
                                            |> withTimeout
                                                30000
                                                $"Docling hybrid OCR timed out on page {page.pageNo}; falling back to legacy PDF parsing."

                                        throwIfCanceled cancellationToken

                                        match ocrResult with
                                        | Error err -> return Error err
                                        | Ok ocrCells ->
                                            let merged =
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
            report $"Docling hybrid converting {pageInputs.Length} page(s) for {Path.GetFileName path}."

            let! document =
                DoclingStandardHybrid.convertPagesWithOptionsWithCancellation
                    DoclingConversionOptions.defaults
                    documentName
                    (Some(Path.GetFileName path))
                    layoutPredictor
                    figureClassifier
                    pageInputs
                    cancellationToken
                |> timed report $"Docling hybrid layout conversion for {Path.GetFileName path}"
                |> withTimeout
                    (layoutConversionTimeoutMs pageInputs.Length)
                    $"Docling hybrid layout conversion timed out for {Path.GetFileName path} after processing budget for {pageInputs.Length} page(s); falling back to legacy PDF parsing."

            throwIfCanceled cancellationToken
            return document |> Result.map (DoclingPassages.toPassages chunkOptions passageSource)
        }

    let private convertPageInputs report chunkOptions passageSource path layoutPredictor figureClassifier pageInputs =
        convertPageInputsWithCancellation
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

        let pages =
            pageInputs
            |> List.map (fun page ->
                page.pageNo,
                { pageNo = page.pageNo
                  size =
                    { width = float page.image.width
                      height = float page.image.height } })
            |> Map.ofList

        let textItems =
            pageInputs
            |> List.mapi (fun index page ->
                let text =
                    page.ocrCells
                    |> List.map _.text
                    |> String.concat "\n"
                    |> Text.normalizeWhitespace

                match Text.notEmpty text with
                | None -> None
                | Some text ->
                    let selfRef = $"#/texts/{index}"

                    Some
                        { selfRef = selfRef
                          parent = "#/body"
                          label = DoclingLabel.Text
                          text = text
                          orig = text
                          contentLayer = DoclingContentLayer.Body
                          prov =
                            [ { pageNo = page.pageNo
                                bbox =
                                  { l = 0.0
                                    t = 0.0
                                    r = float page.image.width
                                    b = float page.image.height
                                    coordOrigin = DoclingCoordinateOrigin.TopLeft }
                                charSpan = None } ]
                          keywords = []
                          sourceId = None
                          sourceDisplayName = None })
            |> List.choose id

        if List.isEmpty textItems then
            Error $"Docling hybrid native-text conversion found no readable text in {Path.GetFileName path}."
        else
            if announce then
                report
                    $"Docling hybrid using fast native-text conversion for {Path.GetFileName path}; skipping layout ONNX."

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

    let private layoutFallbackReason (layoutPassages: PassageRef list) (nativePassages: PassageRef list) =
        let layoutChars = passageTextLength layoutPassages
        let nativeChars = passageTextLength nativePassages

        if layoutPassages.Length <= 1 then
            Some $"Docling hybrid layout conversion produced only {layoutPassages.Length} passage(s)"
        elif
            nativePassages.Length > layoutPassages.Length
            && nativeChars >= 2000L
            && layoutChars * 4L < nativeChars * 3L
        then
            Some
                $"Docling hybrid layout conversion looked incomplete ({layoutPassages.Length} passage(s), {layoutChars} chars vs {nativeChars} native chars)"
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
                match layoutFallbackReason layoutPassages nativePassages with
                | None -> Ok layoutPassages
                | Some reason ->
                    report $"{reason} for {fileName}; checking native-text conversion."

                    if
                        nativePassages.Length > layoutPassages.Length
                        || passageTextLength nativePassages > passageTextLength layoutPassages
                    then
                        report
                            $"Using Docling native-text conversion for {fileName} because it produced {nativePassages.Length} passage(s) and {passageTextLength nativePassages} chars."

                        Ok nativePassages
                    else
                        report
                            $"Keeping Docling layout conversion for {fileName}; native-text conversion produced {nativePassages.Length} passage(s) and {passageTextLength nativePassages} chars."

                        Ok layoutPassages
            | Error nativeError ->
                if layoutPassages.Length <= 1 then
                    report $"Docling native-text fallback failed for {fileName}; keeping layout result: {nativeError}"

                Ok layoutPassages

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
            let rasterizer = createRasterizer options
            report $"Docling hybrid PDF parser starting for {Path.GetFileName path}."

            let ocrProviderResult = tryCreateRapidOcrProvider options storageRoot

            report (
                match ocrProviderResult with
                | Ok(Some _) -> "Docling hybrid OCR provider is enabled."
                | Ok None -> "Docling hybrid OCR provider is not configured; native PDF text will be required."
                | Error err -> $"Docling hybrid OCR provider setup failed: {err}"
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
                                |> timed report "Docling hybrid preparing layout ONNX model"
                                |> withTimeout
                                    60000
                                    "Docling hybrid layout model preparation timed out; falling back to legacy PDF parsing."

                            match layout with
                            | Error err -> return Error err
                            | Ok layout ->
                                throwIfCanceled cancellationToken

                                try
                                    let! figure =
                                        loadFigureClassifier options storageRoot
                                        |> timed report "Docling hybrid preparing figure classifier"
                                        |> withTimeout
                                            30000
                                            "Docling hybrid figure classifier preparation timed out; falling back to legacy PDF parsing."

                                    match figure with
                                    | Error err -> return Error err
                                    | Ok(figureClassifier, figureDisposable) ->
                                        throwIfCanceled cancellationToken

                                        try
                                            let! layoutResult =
                                                convertPageInputsWithCancellation
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
                report $"Docling hybrid PDF parser produced {passages.Length} passage(s) for {fileName}."

                if passages.Length > 1 then
                    return Ok passages
                else
                    report
                        $"Docling hybrid produced only {passages.Length} passage(s) for {fileName}; checking PdfPig fallback chunking."

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
                                $"PdfPig fallback check failed for {fileName}; keeping Docling hybrid result: {legacyError}"

                            Ok passages
            | Error hybridError ->
                report $"Docling hybrid PDF parser failed for {Path.GetFileName path}: {hybridError}"
                report $"Falling back to PdfPig text extraction for {Path.GetFileName path}."
                let! legacy = legacyReader ()

                return
                    match legacy with
                    | Ok passages -> Ok passages
                    | Error legacyError ->
                        Error
                            $"Docling hybrid PDF parser failed: {hybridError}{Environment.NewLine}Legacy PdfPig parser also failed: {legacyError}"
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
                        return Error $"Docling hybrid PDF parser could not start: {ex.Message}"
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
                    | ex -> return Error $"Docling hybrid PDF parser could not start: {ex.Message}"
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
