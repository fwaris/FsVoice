namespace FsVoice.Retrieval.RapidOcrModels

open System
open System.IO
open System.Security.Cryptography

type RapidOcrModelFile =
    { fileName: string
      sizeBytes: int64
      sha256: string }

[<AbstractClass; Sealed>]
type RapidOcrPpOcrV4Mobile =
    static member ModelId = "rapidocr/pp-ocrv4-mobile/ch"

    static member DisplayName = "RapidOCR PP-OCRv4 mobile Chinese-English"

    static member RelativeFolder =
        Path.Combine("FsVoice", "FsColbert", "Models", "rapidocr", "pp-ocrv4-mobile")

    static member Files =
        [| { fileName = "ch_PP-OCRv4_det_mobile.onnx"
             sizeBytes = 4745517L
             sha256 = "d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9" }
           { fileName = "ch_ppocr_mobile_v2.0_cls_mobile.onnx"
             sizeBytes = 585532L
             sha256 = "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c" }
           { fileName = "ch_PP-OCRv4_rec_mobile.onnx"
             sizeBytes = 10857958L
             sha256 = "48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b" }
           { fileName = "ppocr_keys_v1.txt"
             sizeBytes = 26250L
             sha256 = "a1c84d9bdb9ab29043c58896224d32941783eb821629618416dcb08f12886492" } |]

    static member private ResourceName fileName =
        $"FsVoice.Retrieval.RapidOcrModels.Resources.Models.rapidocr-ppocrv4-mobile.{fileName}"

    static member private Assembly = typeof<RapidOcrPpOcrV4Mobile>.Assembly

    static member private TargetFolder storageRoot =
        if String.IsNullOrWhiteSpace storageRoot then
            invalidArg (nameof storageRoot) "A storage root is required for extracting RapidOCR model assets."

        Path.Combine(storageRoot, RapidOcrPpOcrV4Mobile.RelativeFolder)

    static member private HashStream(stream: Stream) =
        use sha = SHA256.Create()

        sha.ComputeHash(stream)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    static member private HashFile(path: string) =
        if File.Exists path then
            use stream = File.OpenRead path
            Some(RapidOcrPpOcrV4Mobile.HashStream stream)
        else
            None

    static member private CopyResourceIfChanged(folder: string, file: RapidOcrModelFile) =
        let target = Path.Combine(folder, file.fileName)

        match RapidOcrPpOcrV4Mobile.HashFile target with
        | Some hash when String.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase) -> false
        | _ ->
            let resourceName = RapidOcrPpOcrV4Mobile.ResourceName file.fileName

            use source = RapidOcrPpOcrV4Mobile.Assembly.GetManifestResourceStream(resourceName)

            if isNull source then
                invalidOp $"Embedded RapidOCR model resource was not found: {resourceName}"

            let tempPath = $"{target}.{Guid.NewGuid():N}.tmp"

            use targetStream = File.Create tempPath
            source.CopyTo targetStream
            targetStream.Flush()
            targetStream.Dispose()

            match RapidOcrPpOcrV4Mobile.HashFile tempPath with
            | Some hash when String.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase) -> ()
            | Some hash ->
                File.Delete tempPath

                invalidOp
                    $"Embedded RapidOCR model checksum mismatch for {file.fileName}: expected {file.sha256}, got {hash}."
            | None -> invalidOp $"Embedded RapidOCR model was not written: {file.fileName}"

            if File.Exists target then
                File.Delete target

            File.Move(tempPath, target)
            true

    static member IsComplete(folder: string) =
        not (String.IsNullOrWhiteSpace folder)
        && RapidOcrPpOcrV4Mobile.Files
           |> Array.forall (fun file ->
               let path = Path.Combine(folder, file.fileName)

               match RapidOcrPpOcrV4Mobile.HashFile path with
               | Some hash -> String.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase)
               | None -> false)

    static member TryFindExtracted(storageRoot: string) =
        let folder = RapidOcrPpOcrV4Mobile.TargetFolder storageRoot

        if RapidOcrPpOcrV4Mobile.IsComplete folder then
            Some folder
        else
            None

    static member EnsureExtracted(storageRoot: string) =
        let folder = RapidOcrPpOcrV4Mobile.TargetFolder storageRoot
        Directory.CreateDirectory folder |> ignore

        RapidOcrPpOcrV4Mobile.Files
        |> Array.iter (fun file -> RapidOcrPpOcrV4Mobile.CopyResourceIfChanged(folder, file) |> ignore)

        if RapidOcrPpOcrV4Mobile.IsComplete folder then
            folder
        else
            invalidOp $"RapidOCR model assets could not be verified in {folder}."
