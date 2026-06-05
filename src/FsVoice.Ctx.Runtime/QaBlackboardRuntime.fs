namespace FsVoice.Ctx

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open FsVoice.Core

type private BlackboardPruningCheckpoint =
    { version: int
      selection: BlackboardPruneSelection }

type internal QaBlackboardRuntime
    (
        options: QaSessionOptions,
        sessionCancellation: CancellationTokenSource,
        report: string -> unit,
        currentMemoryEncoder: unit -> FsColbert.OnnxColbertEncoder option,
        transport: QaResponsesTransport
    ) =
    let blackboardGate = obj ()
    let mutable blackboard = Blackboard.empty 120
    let mutable blackboardVersion = 0
    let mutable blackboardPruningInProgress = false
    let mutable blackboardSummarized = false
    let mutable blackboardPruningUnavailableLogged = false

    let getBlackboard () =
        lock blackboardGate (fun () -> blackboard)

    let updateBlackboard update =
        lock blackboardGate (fun () ->
            blackboard <- update blackboard
            blackboardVersion <- blackboardVersion + 1
            blackboard)

    let blackboardPrunePolicy () =
        { triggerChars = max 1 options.blackboardPruning.triggerChars
          targetChars = max 1 options.blackboardPruning.targetChars
          preserveRecentTurns = max 0 options.blackboardPruning.preserveRecentTurns }

    let blackboardSummarizerAvailable () =
        options.answerTransport.IsSome || options.clients.answerGenerator.IsSome

    let tryReportBlackboardPruningUnavailable () =
        let shouldReport =
            lock blackboardGate (fun () ->
                let totalChars = Blackboard.totalTextChars blackboard

                if
                    options.blackboardPruning.enabled
                    && not blackboardPruningUnavailableLogged
                    && totalChars > options.blackboardPruning.triggerChars
                then
                    blackboardPruningUnavailableLogged <- true
                    Some totalChars
                else
                    None)

        shouldReport
        |> Option.iter (fun totalChars ->
            report
                $"Blackboard pruning skipped because no model summarizer is configured: chars={totalChars}; trigger={options.blackboardPruning.triggerChars}.")

    let tryCreateBlackboardPruningCheckpoint () =
        if not options.blackboardPruning.enabled then
            None
        elif not (blackboardSummarizerAvailable ()) then
            tryReportBlackboardPruningUnavailable ()
            None
        else
            lock blackboardGate (fun () ->
                if blackboardPruningInProgress then
                    None
                else
                    match Blackboard.tryCreatePruneSelection (blackboardPrunePolicy ()) blackboard with
                    | None -> None
                    | Some selection ->
                        blackboardPruningInProgress <- true

                        Some
                            { version = blackboardVersion
                              selection = selection })

    let markBlackboardPruningFinished () =
        lock blackboardGate (fun () -> blackboardPruningInProgress <- false)

    let blackboardSummaryInstructions =
        "Summarize pruned QA blackboard records for future blackboard_search use. Preserve user goals and corrections, prior final answers, source-grounded findings with source names or chunk hints, tool results, durable-memory forget/retract observations, unresolved follow-ups, and conflicts. Do not invent facts. Write compact bullets grouped by topic."

    let renderBlackboardSummaryRecord index (record: BlackboardRecord) =
        let kind = BlackboardEntryKind.displayName record.kind
        let score = record.score |> Option.map (sprintf "%.2f") |> Option.defaultValue "n/a"

        $"[{index + 1}] id={record.id}; turn={record.turnId}; kind={kind}; created={record.createdAt:O}; score={score}\n{Text.truncate 3000 record.text}"

    let blackboardSummaryUserText (selection: BlackboardPruneSelection) =
        let records =
            selection.recordsToSummarize
            |> List.sortBy _.createdAt
            |> List.mapi renderBlackboardSummaryRecord
            |> String.concat "\n\n"

        let dropOnly =
            selection.recordsToDrop
            |> List.map (fun record -> $"{record.id}:{BlackboardEntryKind.displayName record.kind}")
            |> String.concat ", "

        let preservedTurnIds = selection.preservedTurnIds |> String.concat ", "

        $"Create one compact in-session blackboard summary for these records. Covered records will be removed after the summary is accepted; the summary must be useful for later search.\n\nTotal blackboard chars before pruning: {selection.totalChars}\nTarget chars after pruning: {selection.targetChars}\nPreserved recent turn ids: {preservedTurnIds}\nDrop-only operational records not included in the summary: {dropOnly}\n\nRecords to summarize:\n\n{records}"

    let blackboardSummaryRequest answerConfig selection =
        { FsResponses.WebSocketCreateRequest.Default with
            model = answerConfig.modelId
            input = [ FsResponses.IOitem.Message(FsResponses.Message.OfText(blackboardSummaryUserText selection)) ]
            instructions = Some blackboardSummaryInstructions
            max_output_tokens = Some(max 1 options.blackboardPruning.summaryMaxOutputTokens)
            generate = Some true
            reasoning = QaAnswerModel.answerReasoning answerConfig
            store = Some true
            temperature = QaAnswerModel.answerTemperature answerConfig
            tools = Some []
            tool_choice = None }

    let summarizeBlackboardSelection selection cancellationToken =
        async {
            let answerConfig = QaAnswerModel.modelConfig options Answer

            match options.answerTransport with
            | Some _ ->
                let request = blackboardSummaryRequest answerConfig selection
                let! events = transport.RunOfflineRequest request cancellationToken |> Async.AwaitTask

                let summary =
                    FsResponses.ResponseStream.outputText events |> Text.normalizeWhitespace

                if String.IsNullOrWhiteSpace summary then
                    report
                        $"Blackboard pruning summary returned empty text; keeping existing blackboard records. {QaResponses.diagnostics events}."

                    return None
                elif QaResponses.responseError events |> Option.isSome then
                    report
                        $"Blackboard pruning summary returned error; keeping existing blackboard records. {QaResponses.diagnostics events}."

                    return None
                else
                    return Some summary
            | None ->
                match options.clients.answerGenerator with
                | None -> return None
                | Some client ->
                    let opts = ChatOptions()
                    opts.MaxOutputTokens <- Nullable(max 1 options.blackboardPruning.summaryMaxOutputTokens)

                    if ModelCapabilities.supportsTemperature answerConfig.modelId then
                        opts.Temperature <- Nullable(answerConfig.temperature |> Option.defaultValue 0.2f)

                    let messages =
                        [ ChatMessage(ChatRole.System, blackboardSummaryInstructions)
                          ChatMessage(ChatRole.User, blackboardSummaryUserText selection) ]

                    let! response = client.GetResponseAsync(messages, opts, cancellationToken) |> Async.AwaitTask
                    let summary = response.Text |> Text.normalizeWhitespace

                    if String.IsNullOrWhiteSpace summary then
                        report
                            $"Blackboard pruning summary returned empty text; keeping existing blackboard records. {QaAnswerModel.responseDiagnostics response}."

                        return None
                    else
                        return Some summary
        }

    let tryApplyBlackboardPruning checkpoint summaryText =
        let selectedIds =
            checkpoint.selection.recordsToSummarize @ checkpoint.selection.recordsToDrop
            |> List.map _.id
            |> Set.ofList

        let summaryRecord =
            summaryText
            |> Blackboard.summaryFromSelection checkpoint.selection
            |> BlackboardRecords.compactedSummary

        lock blackboardGate (fun () ->
            let currentIds = blackboard.records |> List.map _.id |> Set.ofList

            let selectedStillPresent =
                selectedIds |> Set.forall (fun id -> currentIds.Contains id)

            let protectedTurnIds =
                Blackboard.recentTurnIds options.blackboardPruning.preserveRecentTurns blackboard
                |> Set.ofList

            let selectedNowProtected =
                blackboard.records
                |> List.exists (fun record ->
                    selectedIds.Contains record.id
                    && record.kind <> CompactedSummary
                    && protectedTurnIds.Contains record.turnId)

            if selectedStillPresent && not selectedNowProtected then
                blackboard <- Blackboard.applyPruneSelection checkpoint.selection summaryRecord blackboard
                blackboardVersion <- blackboardVersion + 1
                blackboardPruningInProgress <- false
                blackboardSummarized <- true
                true
            else
                blackboardPruningInProgress <- false
                false)

    let runBlackboardPruning checkpoint =
        async {
            try
                let token = sessionCancellation.Token

                report
                    $"Blackboard pruning started: records={checkpoint.selection.recordsToSummarize.Length}; drop_only={checkpoint.selection.recordsToDrop.Length}; chars={checkpoint.selection.totalChars}; version={checkpoint.version}."

                let! summary = summarizeBlackboardSelection checkpoint.selection token

                match summary with
                | None -> markBlackboardPruningFinished ()
                | Some summaryText ->
                    if tryApplyBlackboardPruning checkpoint summaryText then
                        report
                            $"Blackboard pruning applied: summarized_records={checkpoint.selection.recordsToSummarize.Length}; dropped_records={checkpoint.selection.recordsToDrop.Length}; summary_chars={summaryText.Length}."
                    else
                        report
                            "Blackboard pruning summary was discarded because the selected records are no longer safe to replace."
            with
            | :? OperationCanceledException -> markBlackboardPruningFinished ()
            | ex ->
                report $"Blackboard pruning failed: {ex.Message}"
                markBlackboardPruningFinished ()
        }

    member _.AddRecord record =
        updateBlackboard (Blackboard.add record) |> ignore

    member _.AddRecords records =
        if not (List.isEmpty records) then
            updateBlackboard (Blackboard.addMany records) |> ignore

    member _.HasSummary() =
        lock blackboardGate (fun () -> blackboardSummarized)

    member _.SearchAsync(query, cancellationToken) =
        task {
            let query = Text.normalizeWhitespace query
            let board = getBlackboard ()

            let options =
                { BlackboardSearchOptions.defaults with
                    maxResults = 8
                    includeKinds =
                        [ ToolObservation
                          MemoryEvidence
                          SourceEvidence
                          Conflict
                          FinalAnswer
                          CompactedSummary ] }

            let lexicalHits = Blackboard.search options query board

            let! semanticHits =
                match currentMemoryEncoder () with
                | Some encoder when not (String.IsNullOrWhiteSpace query) ->
                    BlackboardSemantic.search encoder options query board
                    |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
                | _ -> Task.FromResult []

            return
                lexicalHits @ semanticHits
                |> Blackboard.mergeHits options.maxResults
                |> Blackboard.renderHits
        }

    member _.SchedulePruningIfNeeded() =
        match tryCreateBlackboardPruningCheckpoint () with
        | Some checkpoint -> Async.Start(runBlackboardPruning checkpoint, sessionCancellation.Token)
        | None -> ()
