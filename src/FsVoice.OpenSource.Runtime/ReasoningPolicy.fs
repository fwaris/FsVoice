namespace FsVoice.OpenSource

open System
open System.Text.RegularExpressions

type ReasoningDepth =
    | Configured
    | Fast
    | Balanced
    | Deep

type ReasoningDecision =
    { Depth: ReasoningDepth
      EnableThinking: bool
      MaxNewTokens: int
      MaxToolRounds: int
      Reason: string }

type PreRoutedToolCall =
    { Name: string
      Arguments: Map<string, string>
      Reason: string }

module ReasoningDepth =
    let name depth =
        match depth with
        | Configured -> "configured"
        | Fast -> "fast"
        | Balanced -> "balanced"
        | Deep -> "deep"

module ReasoningPolicy =
    let balancedGuidance =
        "Think briefly and directly. Use no more than six short reasoning steps before giving the public answer."

    let private isMatch pattern (value: string) =
        Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let private normalized (value: string) =
        if String.IsNullOrWhiteSpace value then
            ""
        else
            Regex.Replace(value.Trim(), @"\s+", " ")

    let private isDirectTimeOrDate value =
        isMatch
            @"^(?:what(?:'s| is)?|tell me|give me|can you tell me) (?:the )?(?:current |local )?(?:time|date)(?: right now| now| today| is it)?[?.!]*$"
            value

    let private isDirectStatus value =
        isMatch
            @"^(?:what(?:'s| is)?|show|tell me|give me|check) (?:the )?(?:agent |system |runtime )?status[?.!]*$"
            value

    let private isSourceInventory value =
        isMatch
            @"^(?:what|which|list|show|tell me)(?: files| documents| sources| indexes).*(?:loaded|available|selected|configured|have)[?.!]*$"
            value
        || isMatch @"^(?:list|show) (?:the )?(?:loaded|available|selected) (?:files|documents|sources)[?.!]*$" value

    let private isSourceQuestion value =
        isMatch
            @"\b(?:in|from|according to) (?:the |this |my )?(?:document|documents|paper|study|pdf|source|sources|report)\b"
            value
        || isMatch @"\b(?:document|paper|study|pdf|source) (?:says|states|mentions|reports|section)\b" value

    let private isGreetingOrControl value =
        isMatch
            @"^(?:hi|hello|hey|good (?:morning|afternoon|evening)|thanks|thank you|stop|cancel|repeat that|say that again)[?.!]*$"
            value

    let private isSimpleArithmetic value =
        isMatch
            @"^(?:what is|what's|calculate)?\s*-?\d+(?:\.\d+)?\s*(?:\+|-|times|multiplied by|divided by)\s*-?\d+(?:\.\d+)?[?.!]*$"
            value

    let private requiresDeepReasoning (value: string) =
        let questionCount = value |> Seq.filter ((=) '?') |> Seq.length

        value.Length >= 180
        || questionCount > 1
        || isMatch
            @"\b(?:analy[sz]e|compare|contrast|evaluate|trade-?offs?|implications?|root cause|step by step|reason through|prove|derive|why)\b"
            value
        || isMatch
            @"\b(?:twice|three times|fewer than|more than|less than|remaining|altogether|in total|and then|after another)\b"
            value
        || isMatch @"\b\d+\s+(?:minutes?|hours?|days?)\s+(?:after|before)\b" value
        || (isMatch @"\bif\b" value && isMatch @"\bthen\b" value)

    let decide (options: GemmaRuntimeOptions) transcript =
        if not options.AdaptiveReasoning then
            { Depth = Configured
              EnableThinking = options.EnableThinking
              MaxNewTokens = max 1 options.ReasoningMaxNewTokens
              MaxToolRounds = max 0 options.ToolMaxRounds
              Reason = "adaptive_reasoning_disabled" }
        else
            let value = normalized transcript

            if
                isGreetingOrControl value
                || isDirectTimeOrDate value
                || isDirectStatus value
                || isSourceInventory value
                || isSimpleArithmetic value
            then
                { Depth = Fast
                  EnableThinking = false
                  MaxNewTokens = max 1 options.FastReasoningMaxNewTokens
                  MaxToolRounds = max 0 options.FastToolMaxRounds
                  Reason = "direct_or_trivial_request" }
            elif requiresDeepReasoning value then
                { Depth = Deep
                  EnableThinking = options.EnableThinking
                  MaxNewTokens = max 1 options.ReasoningMaxNewTokens
                  MaxToolRounds = max 0 options.ToolMaxRounds
                  Reason = "multi_step_or_analytical_request" }
            else
                { Depth = Balanced
                  EnableThinking = options.EnableThinking
                  MaxNewTokens = max 1 options.BalancedReasoningMaxNewTokens
                  MaxToolRounds = max 0 options.BalancedToolMaxRounds
                  Reason = "default_request" }

    let tryPreRoute transcript =
        let value = normalized transcript

        if isDirectTimeOrDate value then
            Some
                { Name = "get_current_time"
                  Arguments = Map.empty
                  Reason = "direct_time_or_date_request" }
        elif isDirectStatus value then
            Some
                { Name = "get_agent_status"
                  Arguments = Map.empty
                  Reason = "direct_runtime_status_request" }
        elif isSourceInventory value then
            Some
                { Name = "source_inventory"
                  Arguments = Map.empty
                  Reason = "direct_source_inventory_request" }
        elif isSourceQuestion value then
            Some
                { Name = "selected_source_search"
                  Arguments = Map [ "question", value ]
                  Reason = "explicit_source_question" }
        else
            None
