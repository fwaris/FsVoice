namespace FsVoice.Ctx

open System
open FsVoice.Core

type BlackboardEntryKind =
    | Transcript
    | RealtimeJudgement
    | RecallDecision
    | MemoryEvidence
    | SourceEvidence
    | ToolObservation
    | Conflict
    | AnswerCandidate
    | FinalAnswer
    | CompactedSummary

type BlackboardConflict =
    { description: string
      severity: string
      relatedRecordIds: string list }

type BlackboardCompactedSummary =
    { summary: string
      coveredTurnIds: string list
      coveredRecordIds: string list
      coveredKinds: BlackboardEntryKind list
      fromCreatedAt: DateTimeOffset option
      toCreatedAt: DateTimeOffset option
      originalRecordCount: int }

type BlackboardEntry =
    | TranscriptEntry of TranscriptSnapshot
    | RealtimeJudgementEntry of RealtimeJudgement
    | RecallDecisionEntry of SupervisorDecision
    | MemoryEvidenceEntry of MemoryRecallHit
    | SourceEvidenceEntry of SourceChunk
    | ToolObservationEntry of QaToolObservation
    | ConflictEntry of BlackboardConflict
    | AnswerCandidateEntry of string
    | FinalAnswerEntry of QaAnswer
    | CompactedSummaryEntry of BlackboardCompactedSummary

type BlackboardRecord =
    { id: string
      turnId: string
      kind: BlackboardEntryKind
      entry: BlackboardEntry
      text: string
      score: float32 option
      indexable: bool
      createdAt: DateTimeOffset }

type BlackboardHit =
    { record: BlackboardRecord
      score: float32
      reasons: string list }

type BlackboardSearchOptions =
    { maxResults: int
      minScore: float32 option
      includeKinds: BlackboardEntryKind list
      includeNonIndexable: bool }

module BlackboardSearchOptions =
    let defaults =
        { maxResults = 8
          minScore = None
          includeKinds = []
          includeNonIndexable = false }

type Blackboard =
    { records: BlackboardRecord list
      maxRecords: int }

type BlackboardPrunePolicy =
    { triggerChars: int
      targetChars: int
      preserveRecentTurns: int }

type BlackboardPruneSelection =
    { totalChars: int
      targetChars: int
      preservedTurnIds: string list
      recordsToSummarize: BlackboardRecord list
      recordsToDrop: BlackboardRecord list }

module BlackboardEntryKind =
    let displayName =
        function
        | Transcript -> "transcript"
        | RealtimeJudgement -> "realtime_judgement"
        | RecallDecision -> "recall_decision"
        | MemoryEvidence -> "memory_evidence"
        | SourceEvidence -> "source_evidence"
        | ToolObservation -> "tool_observation"
        | Conflict -> "conflict"
        | AnswerCandidate -> "answer_candidate"
        | FinalAnswer -> "final_answer"
        | CompactedSummary -> "compacted_summary"

module Blackboard =
    let empty maxRecords =
        { records = []
          maxRecords = max 1 maxRecords }

    let private trimToMax board records =
        records |> List.truncate board.maxRecords

    let add record board =
        { board with
            records = record :: board.records |> trimToMax board }

    let addMany records board =
        records |> List.fold (fun current record -> add record current) board

    let recent maxCount board =
        board.records |> List.truncate (max 1 maxCount)

    let totalTextChars board =
        board.records |> List.sumBy (fun record -> record.text.Length)

    let private kindIncluded options record =
        List.isEmpty options.includeKinds
        || List.contains record.kind options.includeKinds

    let searchCandidates options board =
        board.records
        |> List.filter (fun record -> (options.includeNonIndexable || record.indexable) && kindIncluded options record)

    let private termSet value = Text.terms value |> Set.ofList

    let private lexicalScore query value =
        let normalizedQuery = Text.normalizeWhitespace query
        let normalizedValue = Text.normalizeWhitespace value

        if String.IsNullOrWhiteSpace normalizedQuery then
            0.1f
        else
            let queryTerms = termSet normalizedQuery
            let valueTerms = termSet normalizedValue

            let overlap =
                if Set.isEmpty queryTerms then
                    0.0f
                else
                    let matched = Set.intersect queryTerms valueTerms
                    float32 matched.Count / float32 queryTerms.Count

            let exactBoost =
                if
                    normalizedQuery.Length >= 8
                    && normalizedValue.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                then
                    0.6f
                else
                    0.0f

            overlap + exactBoost

    let search options query board =
        let query = Text.normalizeWhitespace query

        searchCandidates options board
        |> List.choose (fun record ->
            let score = lexicalScore query record.text

            if score <= 0.0f then
                None
            else
                let reasons =
                    if String.IsNullOrWhiteSpace query then
                        [ "recent" ]
                    else
                        [ "lexical" ]

                Some
                    { record = record
                      score = score
                      reasons = reasons })
        |> List.filter (fun hit -> options.minScore |> Option.forall (fun minScore -> hit.score >= minScore))
        |> List.sortByDescending (fun hit -> hit.score, hit.record.createdAt)
        |> List.truncate (max 1 options.maxResults)

    let mergeHits maxResults hits =
        hits
        |> List.groupBy _.record.id
        |> List.map (fun (_, group) ->
            let best = group |> List.maxBy _.score

            { best with
                reasons = group |> List.collect _.reasons |> List.distinct })
        |> List.sortByDescending (fun hit -> hit.score, hit.record.createdAt)
        |> List.truncate (max 1 maxResults)

    let renderHits hits =
        if List.isEmpty hits then
            "No matching blackboard observations were found."
        else
            hits
            |> List.mapi (fun index hit ->
                let kind = BlackboardEntryKind.displayName hit.record.kind
                let reasons = hit.reasons |> String.concat ", "
                let text = hit.record.text |> Text.truncate 1200
                $"[{index + 1}] {kind} score={hit.score:F2} ({reasons})\n{text}")
            |> String.concat "\n\n"

    let private isBlackboardSearchObservation record =
        match record.entry with
        | ToolObservationEntry observation ->
            String.Equals(observation.pluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
            && String.Equals(observation.toolName, "blackboard_search", StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let private isSummarizableForPruning record =
        match record.kind with
        | Transcript
        | ToolObservation
        | MemoryEvidence
        | SourceEvidence
        | Conflict
        | FinalAnswer
        | CompactedSummary -> not (isBlackboardSearchObservation record)
        | _ -> false

    let private isDropOnlyForPruning record =
        match record.kind with
        | RealtimeJudgement
        | RecallDecision
        | AnswerCandidate -> true
        | ToolObservation -> isBlackboardSearchObservation record
        | _ -> false

    let recentTurnIds preserveRecentTurns board =
        board.records
        |> List.filter (fun record -> record.kind <> CompactedSummary)
        |> List.map _.turnId
        |> List.distinct
        |> List.truncate (max 0 preserveRecentTurns)

    let tryCreatePruneSelection policy board =
        let totalChars = totalTextChars board

        if totalChars <= policy.triggerChars then
            None
        else
            let preservedTurnIds = recentTurnIds policy.preserveRecentTurns board
            let preservedTurnIdSet = preservedTurnIds |> Set.ofList

            let isProtected record =
                record.kind <> CompactedSummary && preservedTurnIdSet.Contains record.turnId

            let oldRecords = board.records |> List.filter (isProtected >> not)

            let summarizable = oldRecords |> List.filter isSummarizableForPruning |> List.rev

            if List.isEmpty summarizable then
                None
            else
                let targetChars = max 1 policy.targetChars
                let charsToRemove = max 1 (totalChars - targetChars)

                let selected, _ =
                    (([], 0), summarizable)
                    ||> List.fold (fun (selected, removedChars) record ->
                        if removedChars >= charsToRemove then
                            selected, removedChars
                        else
                            record :: selected, removedChars + record.text.Length)

                let selected = selected |> List.rev

                if List.isEmpty selected then
                    None
                else
                    let dropOnly = oldRecords |> List.filter isDropOnlyForPruning

                    Some
                        { totalChars = totalChars
                          targetChars = targetChars
                          preservedTurnIds = preservedTurnIds
                          recordsToSummarize = selected
                          recordsToDrop = dropOnly }

    let summaryFromSelection selection summaryText =
        let summarized = selection.recordsToSummarize
        let createdAt = summarized |> List.map _.createdAt

        { summary = Text.normalizeWhitespace summaryText
          coveredTurnIds = summarized |> List.map _.turnId |> List.distinct
          coveredRecordIds = summarized |> List.map _.id
          coveredKinds = summarized |> List.map _.kind |> List.distinct
          fromCreatedAt =
            if List.isEmpty createdAt then
                None
            else
                Some(List.min createdAt)
          toCreatedAt =
            if List.isEmpty createdAt then
                None
            else
                Some(List.max createdAt)
          originalRecordCount = summarized.Length }

    let applyPruneSelection selection summaryRecord board =
        let removedIds =
            selection.recordsToSummarize @ selection.recordsToDrop
            |> List.map _.id
            |> Set.ofList

        { board with
            records =
                summaryRecord
                :: (board.records |> List.filter (fun record -> not (removedIds.Contains record.id)))
                |> trimToMax board }

module BlackboardSemantic =
    let private passageFor index (record: BlackboardRecord) : FsColbert.PassageRef =
        { sourceId = record.id
          sourceDisplayName = BlackboardEntryKind.displayName record.kind
          sourceLocation = $"blackboard://{record.id}"
          index = index
          sectionPath = []
          contentRole = FsColbert.PassageContentRole.Unknown
          pageNumbers = []
          text = record.text
          keywords = [] }

    let search
        (encoder: FsColbert.OnnxColbertEncoder)
        (options: BlackboardSearchOptions)
        (query: string)
        (board: Blackboard)
        =
        async {
            let query = Text.normalizeWhitespace query
            let records = Blackboard.searchCandidates options board

            if String.IsNullOrWhiteSpace query || List.isEmpty records then
                return []
            else
                try
                    let passages = records |> List.mapi passageFor

                    let! index =
                        FsColbert.IndexBuilder.createFromPassages
                            encoder
                            FsColbert.ChunkOptions.fsKameDefaults
                            passages
                            None

                    let searchOptions =
                        { FsColbert.SearchOptions.defaults with
                            maxResults = max 1 options.maxResults
                            candidateLimit =
                                max FsColbert.SearchOptions.defaults.candidateLimit (max 1 options.maxResults)
                            useLexicalFilter = true
                            useRRF = true
                            denseWeight = 1.0f
                            lexicalWeight = 0.15f }

                    let recordsById =
                        records |> List.map (fun record -> record.id, record) |> Map.ofList

                    let! hits = FsColbert.Search.query encoder searchOptions index query

                    return
                        hits
                        |> List.choose (fun hit ->
                            recordsById
                            |> Map.tryFind hit.reference.sourceId
                            |> Option.map (fun record ->
                                { record = record
                                  score = hit.score
                                  reasons = [ "semantic" ] }))
                        |> List.filter (fun hit ->
                            options.minScore |> Option.forall (fun minScore -> hit.score >= minScore))
                        |> List.truncate (max 1 options.maxResults)
                with _ ->
                    return []
        }

module BlackboardRecords =
    let private newId kind =
        let suffix = Guid.NewGuid().ToString("N")
        $"bb_{BlackboardEntryKind.displayName kind}_{suffix}"

    let private normalize value = Text.normalizeWhitespace value

    let private create turnId kind entry indexable score text =
        { id = newId kind
          turnId = turnId
          kind = kind
          entry = entry
          text = normalize text
          score = score
          indexable = indexable
          createdAt = DateTimeOffset.UtcNow }

    let transcript (snapshot: TranscriptSnapshot) =
        create snapshot.turnId Transcript (TranscriptEntry snapshot) true None $"User transcript: {snapshot.text}"

    let realtimeJudgement turnId (judgement: RealtimeJudgement) =
        let risk = judgement.riskFlags
        let turnKind = Option.defaultValue "unknown" judgement.turnKind
        let topicContinuity = Option.defaultValue "unknown" judgement.topicContinuity
        let memoryAction = Option.defaultValue "unknown" judgement.memoryAction
        let needsExternalContext = Option.defaultValue false judgement.needsExternalContext

        create
            turnId
            RealtimeJudgement
            (RealtimeJudgementEntry judgement)
            false
            (Some(float32 judgement.confidence))
            $"Realtime judgement: turn_kind={turnKind}; topic_continuity={topicContinuity}; memory_action={memoryAction}; needs_external_context={needsExternalContext}; confidence={judgement.confidence:F2}; risks=memory:{risk.memoryMutation} sensitive:{risk.sensitive} conflict:{risk.conflictLikely}."

    let recallDecision turnId decision =
        create
            turnId
            RecallDecision
            (RecallDecisionEntry decision)
            false
            None
            $"Durable memory recall decision:\n{DurableMemory.renderRecallSpec decision}"

    let memoryEvidence turnId (hit: MemoryRecallHit) =
        let record = hit.record
        let reasons = hit.reasons |> String.concat ", "

        create
            turnId
            MemoryEvidence
            (MemoryEvidenceEntry hit)
            true
            (Some hit.score)
            $"Durable memory {record.memoryId} score={hit.score:F2} ({reasons})\n{record.title}\n{record.text}"

    let sourceEvidence turnId (chunk: SourceChunk) =
        create
            turnId
            SourceEvidence
            (SourceEvidenceEntry chunk)
            true
            (Some chunk.score)
            $"Source evidence score={chunk.score:F2} source={chunk.source.DisplayName} chunk={chunk.index}\n{chunk.text}"

    let toolObservation turnId (observation: QaToolObservation) =
        let isBlackboardSearch =
            String.Equals(observation.pluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
            && String.Equals(observation.toolName, "blackboard_search", StringComparison.OrdinalIgnoreCase)

        create
            turnId
            ToolObservation
            (ToolObservationEntry observation)
            (not isBlackboardSearch)
            None
            $"Tool observation {observation.pluginName}.{observation.toolName}\nQuery: {observation.query}\n{observation.content}"

    let conflict turnId conflict =
        create
            turnId
            Conflict
            (ConflictEntry conflict)
            true
            None
            $"Conflict severity={conflict.severity}\n{conflict.description}"

    let answerCandidate turnId answer =
        create turnId AnswerCandidate (AnswerCandidateEntry answer) false None $"Answer candidate:\n{answer}"

    let finalAnswer (answer: QaAnswer) =
        create answer.turnId FinalAnswer (FinalAnswerEntry answer) true None $"Final answer:\n{answer.answer}"

    let compactedSummary (summary: BlackboardCompactedSummary) =
        let kinds =
            summary.coveredKinds
            |> List.map BlackboardEntryKind.displayName
            |> String.concat ", "

        let turns = summary.coveredTurnIds |> String.concat ", "

        create
            "blackboard_summary"
            CompactedSummary
            (CompactedSummaryEntry summary)
            true
            None
            $"Compacted blackboard summary: records={summary.originalRecordCount}; turns={turns}; kinds={kinds}\n{summary.summary}"
