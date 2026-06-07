namespace FsVoice.Ctx

open System
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

    let reliableAnswerFallback =
        "I could not produce a reliable answer from the available evidence. Please ask a narrower question."

    let maxAnswerTokensSettingsGuidance =
        "Disconnect, open Settings, increase Max Answer Tokens, then reconnect and try again."

    let tokenLimitFallback maxOutputTokens =
        $"I was unable to obtain a complete answer. The answer model appears to have exceeded the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance}"

    let emptyAnswerFallbackWithLimit maxOutputTokens =
        $"I was unable to obtain an answer from the oracle. The answer model returned empty text with the current max answer token limit of {maxOutputTokens}. {maxAnswerTokensSettingsGuidance} You can also ask a narrower question."

    let isFallbackAnswer (answer: string) =
        answer = emptyAnswerFallback
        || answer = reliableAnswerFallback
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

    let finalAnswerSynthesisInstructions =
        """Final answer synthesis mode.
Produce the final answer to speak to the user.
Use only the evidence in the user message: the user question, source context, durable memory, and tool observations.
Do not call tools.
Do not describe your reasoning, tool choices, lookup process, or limitations.
Do not include process phrases such as "I need to", "let me", "I should", or "I can use".
If the evidence is insufficient, say that plainly and concisely.
Return only the answer text."""

    let finalAnswerSynthesisUserItem (prompt: AnswerPrompt) (observations: QaToolObservation list) =
        let toolObservations = renderObservations observations

        let text =
            $"Original answer evidence before this tool loop:\n{prompt.userPrompt}\n\nAuthoritative gathered tool observations from this answer attempt:\n{toolObservations}\n\nReturn only the final answer."

        FsResponses.IOitem.Message
            { FsResponses.Message.Default with
                role = "user"
                content = [ FsResponses.Content.Input_text {| text = text |} ] }

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
