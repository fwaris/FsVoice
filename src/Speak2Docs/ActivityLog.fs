namespace Speak2Docs

open System
open System.Text
open System.Text.RegularExpressions

type ActivityLogVerbosity =
    | Informational
    | Verbose

module ActivityLog =
    let private unquotedLocalPath =
        Regex(@"(?<![:/\w])(?:file://)?(?:/[^\s'""<>]+|[A-Za-z]:[\\/][^\s'""<>]+)", RegexOptions.Compiled)

    let private documentGuidFileNamePrefix =
        Regex(
            @"(?<![\w])(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})[-_](?=[^\s'""<>]*\.[A-Za-z0-9]{1,12})",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let private normalize (text: string) =
        (defaultArg (Option.ofObj text) "").Trim()

    let private trimPathToken (value: string) =
        let trailing =
            value
            |> Seq.rev
            |> Seq.takeWhile (fun c -> c = '.' || c = ',' || c = ';' || c = ':' || c = ')' || c = ']' || c = '}')
            |> Seq.toArray
            |> Array.rev
            |> String

        if trailing.Length = 0 then
            value, ""
        else
            value.Substring(0, value.Length - trailing.Length), trailing

    let private localPath (value: string) =
        let value = normalize value

        if value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) then
            try
                let uri = Uri(value)

                if uri.IsFile then uri.LocalPath else value
            with _ ->
                value
        else
            value

    let private fileName (value: string) =
        let path = localPath value

        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryLast
        |> Option.defaultValue path

    let private removeDocumentGuidFromFileNames text =
        documentGuidFileNamePrefix.Replace(defaultArg (Option.ofObj text) "", "")

    let private shortenLocalPath value =
        let path, trailing = trimPathToken value
        let path = localPath path

        let startsLikePath =
            path.StartsWith("/", StringComparison.Ordinal)
            || Regex.IsMatch(path, "^[A-Za-z]:[\\/]")

        let name = fileName path |> removeDocumentGuidFromFileNames

        if
            startsLikePath
            && name.Contains(".", StringComparison.Ordinal)
            && name.Length < path.Length
        then
            $"{name}{trailing}"
        else
            value

    let private shortenQuotedLocalPaths (quote: char) (text: string) =
        let parts = (defaultArg (Option.ofObj text) "").Split(quote)

        if parts.Length = 1 then
            text
        else
            parts
            |> Array.mapi (fun index part -> if index % 2 = 1 then shortenLocalPath part else part)
            |> String.concat (string quote)

    let private hideLocalPathDetails text =
        text
        |> shortenQuotedLocalPaths '\''
        |> shortenQuotedLocalPaths '"'
        |> fun value -> unquotedLocalPath.Replace(value, fun m -> shortenLocalPath m.Value)
        |> removeDocumentGuidFromFileNames

    let toStorageValue =
        function
        | Informational -> "informational"
        | Verbose -> "verbose"

    let ofStorageValue value =
        match (normalize value).ToLowerInvariant() with
        | "verbose"
        | "debug"
        | "troubleshooting" -> Verbose
        | _ -> Informational

    let displayName =
        function
        | Informational -> "Informational"
        | Verbose -> "Verbose"

    let private containsAny (needles: string list) (value: string) =
        needles
        |> List.exists (fun needle -> value.Contains(needle, System.StringComparison.OrdinalIgnoreCase))

    let private startsWithAny (prefixes: string list) (value: string) =
        prefixes
        |> List.exists (fun prefix -> value.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))

    let private utf8ByteCount (value: string) =
        Encoding.UTF8.GetByteCount(defaultArg (Option.ofObj value) "")

    let private utf8Tail maxBytes (value: string) =
        let value = defaultArg (Option.ofObj value) ""

        if maxBytes <= 0 then
            ""
        else
            let bytes = Encoding.UTF8.GetBytes value

            if bytes.Length <= maxBytes then
                value
            else
                let mutable start = bytes.Length - maxBytes

                while start < bytes.Length && (bytes[start] &&& 0xC0uy) = 0x80uy do
                    start <- start + 1

                if start >= bytes.Length then
                    ""
                else
                    Encoding.UTF8.GetString(bytes, start, bytes.Length - start)

    let private recentWholeLines maxBytes value =
        let value = defaultArg (Option.ofObj value) ""

        if utf8ByteCount value <= maxBytes then
            value
        else
            let tail = utf8Tail maxBytes value
            let newline = tail.IndexOf('\n')

            if newline >= 0 then tail.Substring(newline + 1) else tail

    let boundedFileText maxBytes trimBytes existing appendLine =
        let existing = defaultArg (Option.ofObj existing) ""
        let appendLine = defaultArg (Option.ofObj appendLine) ""
        let maxBytes = max 0 maxBytes
        let trimBytes = min maxBytes (max 0 trimBytes)
        let candidate = existing + appendLine

        if utf8ByteCount candidate <= maxBytes then
            candidate
        else
            let retained = recentWholeLines trimBytes existing
            let trimmedCandidate = retained + appendLine

            if utf8ByteCount trimmedCandidate <= maxBytes then
                trimmedCandidate
            else
                recentWholeLines maxBytes trimmedCandidate

    let private informationalPrefixes =
        [ "Document structure PDF parser starting"
          "Document structure PDF parser produced"
          "Document structure parser rasterizing"
          "Document structure parser rasterized"
          "Document structure parser reading native PDF text"
          "Document structure parser read native text"
          "Document structure OCR page"
          "Document structure parser converting"
          "Document structure layout conversion for"
          "Document structure layout page"
          "Document structure Heron layout page"
          "Document structure preparing layout ONNX model"
          "Document structure preparing figure classifier"
          "Oracle final response:"
          "Oracle response ready:" ]

    let private alwaysVerbosePrefixes =
        [ "Answer Response timing"; "Answer Responses timing:" ]

    let private verbosePrefixes =
        [ "Context ready:"
          "Persisted document library:"
          "Realtime state changed:"
          "Realtime session created"
          "Realtime session updated"
          "Realtime protected startup greeting requested"
          "Realtime greeting requested:"
          "Realtime response created:"
          "Realtime response audio started:"
          "Realtime response audio stopped:"
          "Realtime response done:"
          "Realtime response requested"
          "Realtime acknowledged"
          "Tool provider folder"
          "Oracle tool call started:"
          "Oracle tool call completed"
          "Oracle tool output ready"
          "Oracle tool output was blank"
          "QA request started:"
          "QA request returned:"
          "QA memory started:"
          "QA memory completed:"
          "QA retrieval started:"
          "QA retrieval completed:"
          "QA answer started:"
          "QA answer completed:"
          "Answer Responses request started:"
          "Answer Responses transport:"
          "Answer Response timing"
          "Answer Responses timing:"
          "QA session configured:"
          "QA source update skipped"
          "QA answer trace"
          "QA timing:"
          "Host sources changed:"
          "Ignoring oracle response"
          "Using document structure"
          "Keeping document structure"
          "Starting document processing command"
          "Document processing command completed"
          "Document structure"
          "Falling back to PdfPig"
          "Loaded source chunk(s)."
          "Retrieved chunk:"
          "Loaded FsColbert index"
          "Loaded prebuilt FsColbert index"
          "Loaded persisted FsColbert index preview"
          "FsColbert indexed"
          "Preparing FsColbert model"
          "Building FsColbert index"
          "Prebuilt FsColbert index is available"
          "Building missing FsColbert index"
          "Loaded FsColbert indices" ]

    let private verboseDiagnosticContains =
        [ "response_id=resp_"
          "Server VAD active"
          "Server VAD interruption requested"
          "Server VAD is interruptible"
          "Server VAD is non-interrupting"
          "protected greeting Server VAD active" ]

    let classify text =
        let text = normalize text

        if startsWithAny alwaysVerbosePrefixes text then
            Verbose
        elif containsAny verboseDiagnosticContains text then
            Verbose
        elif
            containsAny
                [ "failed"
                  "error"
                  "unable"
                  "unavailable"
                  "not found"
                  "not granted"
                  "timed out"
                  "canceled"
                  "canceling"
                  "interrupted" ]
                text
        then
            Informational
        elif startsWithAny informationalPrefixes text then
            Informational
        elif startsWithAny verbosePrefixes text then
            Verbose
        else
            Informational

    let isVisible verbosity text =
        match verbosity, classify text with
        | Verbose, _ -> true
        | Informational, Informational -> true
        | Informational, Verbose -> false

    let visible verbosity entries =
        let displayText =
            match verbosity with
            | Informational -> hideLocalPathDetails
            | Verbose -> id

        entries |> List.filter (isVisible verbosity) |> List.map displayText
