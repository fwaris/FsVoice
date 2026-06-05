namespace FsVoice.Ctx

open System
open Microsoft.Extensions.AI
open FsVoice.Core
open FsVoice.Retrieval

type internal AnswerPrompt =
    { instructions: string
      userPrompt: string }

type internal AnswerModelResult =
    { answer: string
      observations: QaToolObservation list }

module internal QaAnswerModel =
    let modelConfig (options: QaSessionOptions) role =
        options.modelRoles
        |> Option.ofObj
        |> Option.bind (Map.tryFind role)
        |> Option.orElse (PlugInDefinition.defaultModels |> Map.tryFind role)
        |> Option.defaultValue (ModelRoleConfig.create options.answerModelId)

    let roleMaxTokens options role fallback =
        (modelConfig options role).maxOutputTokens |> Option.defaultValue fallback

    let emptyAnswerFallback =
        "I could not produce an answer from the selected context. Please try again."

    let maxAnswerTokensSettingsGuidance =
        "Disconnect, open Settings, increase Max Answer Tokens, then reconnect and try again."

    let tokenLimitFallback maxOutputTokens =
        $"I was unable to obtain a complete answer. The answer model appears to have exceeded the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance}"

    let emptyAnswerFallbackWithLimit maxOutputTokens =
        $"I was unable to obtain an answer from the oracle. The answer model returned empty text with the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."

    let isFallbackAnswer (answer: string) =
        answer = emptyAnswerFallback
        || answer.StartsWith("I was unable to obtain", StringComparison.OrdinalIgnoreCase)

    let renderObservations (observations: QaToolObservation list) =
        if List.isEmpty observations then
            "No tool observations were recorded."
        else
            observations
            |> List.truncate 12
            |> List.mapi (fun index observation ->
                $"[{index + 1}] {observation.pluginName}.{observation.toolName}\n{Text.truncate 900 observation.content}")
            |> String.concat "\n\n"

    let toolBudgetFallbackAnswer (observations: QaToolObservation list) =
        let latest = observations |> List.rev |> List.truncate 3 |> List.rev

        if List.isEmpty latest then
            ""
        else
            $"The latest source evidence I found before stopping extra searches is:\n\n{renderObservations latest}"
            |> Text.truncate 2200

    let nullableValue (value: Nullable<'T>) =
        if value.HasValue then string value.Value else "n/a"

    let responseDiagnostics (response: ChatResponse) =
        let finishReason = response.FinishReason |> nullableValue

        let usage =
            if isNull response.Usage then
                "usage=n/a"
            else
                $"usage=input:{nullableValue response.Usage.InputTokenCount} output:{nullableValue response.Usage.OutputTokenCount} reasoning:{nullableValue response.Usage.ReasoningTokenCount} total:{nullableValue response.Usage.TotalTokenCount}"

        let messageCount =
            if isNull response.Messages then
                0
            else
                response.Messages.Count

        $"finish={finishReason}; {usage}; messages={messageCount}"

    let isTokenLimitFinish (response: ChatResponse) =
        if isNull response then
            false
        else
            let finishReason = response.FinishReason |> nullableValue

            finishReason.Contains("length", StringComparison.OrdinalIgnoreCase)
            || finishReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
            || finishReason.Contains("max output", StringComparison.OrdinalIgnoreCase)

    let answerPromptMessages prompt =
        [ ChatMessage(ChatRole.System, prompt.instructions)
          ChatMessage(ChatRole.User, prompt.userPrompt) ]

    let answerReasoning (answerConfig: ModelRoleConfig) =
        answerConfig.reasoningEffort
        |> Option.map (fun effort ->
            { FsResponses.Reasoning.Default with
                effort = Some effort })

    let answerTemperature (answerConfig: ModelRoleConfig) =
        if ModelCapabilities.supportsTemperature answerConfig.modelId then
            answerConfig.temperature |> Option.defaultValue 0.2f |> Some
        else
            None

    let answerUserItem (prompt: AnswerPrompt) =
        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                role = "user"
                content = [ FsResponses.Content.Input_text {| text = prompt.userPrompt |} ] }

    let answerAssistantItem answer =
        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                status = Some "completed"
                role = "assistant"
                content = [ FsResponses.Content.Output_text { text = answer; annotations = None } ] }

    let renderTemplate replacements (template: string) =
        replacements
        |> List.fold (fun (text: string) (name, value) -> text.Replace("{{" + name + "}}", value)) template

    let renderTypedMemory decision hits =
        let memories = DurableMemory.renderRecall hits
        let policy = DurableMemory.renderRecallSpec decision
        $"Recall policy:\n{policy}\n\nTyped memory evidence:\n{memories}"

    let answerInstructions (options: QaSessionOptions) =
        options.prompts.answerSystem
        |> Option.orElse options.plugInProfile.answerSystemInstruction
        |> Option.defaultValue DefaultPlugInPrompts.answerSystem

    let answerPrompt
        (options: QaSessionOptions)
        (contextSources: unit -> KnowledgeSource list)
        (snapshot: TranscriptSnapshot)
        (decision: SupervisorDecision)
        (memoryHits: MemoryRecallHit list)
        (chunks: SourceChunk list)
        (observations: QaToolObservation list)
        =
        let sourceContext =
            KnowledgeSources.renderContextWithLimit options.maxContextChunks chunks

        let inventory = KnowledgeSources.renderInventory (contextSources ())
        let typedMemory = renderTypedMemory decision memoryHits
        let toolObservations = renderObservations observations

        let userPrompt =
            options.prompts.answerUserTemplate
            |> Option.defaultValue DefaultPlugInPrompts.answerUserTemplate
            |> renderTemplate
                [ "question", snapshot.text
                  "typedMemory", typedMemory
                  "toolObservations", toolObservations
                  "sourceInventory", inventory
                  "sourceContext", sourceContext ]

        { instructions = answerInstructions options
          userPrompt = userPrompt }

    let createSnapshot (request: QaTurnRequest) =
        { turnId = request.turnId
          itemId = request.turnId
          revision = 1
          text = Text.normalizeWhitespace request.question
          isFinal = true
          receivedAt = DateTimeOffset.UtcNow }

    let answerWithChatClient
        (options: QaSessionOptions)
        report
        contextSources
        (snapshot: TranscriptSnapshot)
        (decision: SupervisorDecision)
        (memoryHits: MemoryRecallHit list)
        (chunks: SourceChunk list)
        (observations: QaToolObservation list)
        cancellationToken
        =
        async {
            match options.clients.answerGenerator with
            | None ->
                return
                    { answer = "No answer model is configured for this QA session."
                      observations = [] }
            | Some client ->
                let answerConfig = modelConfig options Answer

                let prompt =
                    answerPrompt options contextSources snapshot decision memoryHits chunks observations

                let messages = answerPromptMessages prompt

                let answerAttempt maxOutputTokens =
                    async {
                        let opts = ChatOptions()

                        if ModelCapabilities.supportsTemperature answerConfig.modelId then
                            opts.Temperature <- Nullable(answerConfig.temperature |> Option.defaultValue 0.2f)

                        opts.MaxOutputTokens <- Nullable(maxOutputTokens)

                        let! response = client.GetResponseAsync(messages, opts, cancellationToken) |> Async.AwaitTask

                        return response, response.Text |> Text.normalizeWhitespace
                    }

                let maxOutputTokens = roleMaxTokens options Answer 2500
                let! response, answer = answerAttempt maxOutputTokens

                if isTokenLimitFinish response then
                    report
                        $"Answer model hit output token limit: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; answer_chars={answer.Length}; contextChunks={chunks.Length}; {responseDiagnostics response}."

                    return
                        { answer = tokenLimitFallback maxOutputTokens
                          observations = [] }
                elif not (String.IsNullOrWhiteSpace answer) then
                    return { answer = answer; observations = [] }
                else
                    report
                        $"Answer model returned empty text: model={answerConfig.modelId}; maxOutputTokens={maxOutputTokens}; contextChunks={chunks.Length}; {responseDiagnostics response}."

                    let retryMaxOutputTokens = max 1200 (maxOutputTokens * 2)
                    let! retryResponse, retryAnswer = answerAttempt retryMaxOutputTokens

                    if isTokenLimitFinish retryResponse then
                        report
                            $"Answer model retry hit output token limit: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; contextChunks={chunks.Length}; {responseDiagnostics retryResponse}."

                        return
                            { answer = tokenLimitFallback retryMaxOutputTokens
                              observations = [] }
                    elif not (String.IsNullOrWhiteSpace retryAnswer) then
                        report
                            $"Answer model retry succeeded after empty response: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; answer_chars={retryAnswer.Length}; {responseDiagnostics retryResponse}."

                        return
                            { answer = retryAnswer
                              observations = [] }
                    else
                        report
                            $"Answer model retry also returned empty text: model={answerConfig.modelId}; maxOutputTokens={retryMaxOutputTokens}; contextChunks={chunks.Length}; {responseDiagnostics retryResponse}."

                        return
                            { answer = emptyAnswerFallbackWithLimit retryMaxOutputTokens
                              observations = [] }
        }
