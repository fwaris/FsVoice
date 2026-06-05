namespace FsVoice.Ctx

open System
open System.Text.Json

module internal QaResponses =
    let private cachedTokensText (details: JsonElement option) =
        let tryNumber (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Number ->
                let mutable parsed = 0

                if element.TryGetInt32(&parsed) then Some parsed else None
            | JsonValueKind.String ->
                match Int32.TryParse(element.GetString() |> Option.ofObj |> Option.defaultValue "") with
                | true, parsed -> Some parsed
                | false, _ -> None
            | _ -> None

        details
        |> Option.bind (fun element ->
            if element.ValueKind <> JsonValueKind.Object then
                None
            else
                match element.TryGetProperty "cached_tokens" with
                | true, value -> tryNumber value
                | false, _ -> None)
        |> Option.map string
        |> Option.defaultValue "n/a"

    let responseUsageDiagnostics (usage: FsResponses.Usage option) =
        usage
        |> Option.map (fun usage ->
            $"usage=input:{usage.input_tokens} output:{usage.output_tokens} total:{usage.total_tokens} cached:{cachedTokensText usage.input_tokens_details}")
        |> Option.defaultValue "usage=n/a"

    let eventName event =
        FsResponses.ResponseStreamEvent.typeName event

    let terminalResponse events =
        events
        |> List.tryPick (function
            | FsResponses.ResponseStreamEvent.ResponseCompleted event
            | FsResponses.ResponseStreamEvent.ResponseFailed event
            | FsResponses.ResponseStreamEvent.ResponseIncomplete event -> Some event.response
            | _ -> None)

    let lifecycleResponse event =
        match event with
        | FsResponses.ResponseStreamEvent.ResponseCreated lifecycle
        | FsResponses.ResponseStreamEvent.ResponseQueued lifecycle
        | FsResponses.ResponseStreamEvent.ResponseInProgress lifecycle
        | FsResponses.ResponseStreamEvent.ResponseCompleted lifecycle
        | FsResponses.ResponseStreamEvent.ResponseFailed lifecycle
        | FsResponses.ResponseStreamEvent.ResponseIncomplete lifecycle -> Some lifecycle.response
        | _ -> None

    let anyResponse events =
        events |> List.rev |> List.tryPick lifecycleResponse

    let responseIdFromEvents events = anyResponse events |> Option.map _.id

    let responseError events =
        events
        |> List.tryPick (function
            | FsResponses.ResponseStreamEvent.Error event -> Some event.error
            | _ -> None)

    let diagnostics events =
        let eventNames =
            events
            |> List.countBy eventName
            |> List.map (fun (name, count) -> if count = 1 then name else $"{name}x{count}")
            |> String.concat ","

        let response = terminalResponse events

        let status = response |> Option.map _.status |> Option.defaultValue "n/a"
        let responseId = response |> Option.map _.id |> Option.defaultValue "n/a"

        let previousResponseId =
            response
            |> Option.bind (fun value -> value.previous_response_id)
            |> Option.defaultValue "n/a"

        let usage = response |> Option.bind _.usage |> responseUsageDiagnostics

        let incompleteReason =
            response
            |> Option.bind _.incomplete_details
            |> Option.map _.reason
            |> Option.defaultValue "n/a"

        let errorCode =
            responseError events |> Option.map _.code |> Option.defaultValue "n/a"

        let errorMessage =
            responseError events
            |> Option.map _.message
            |> Option.map (FsVoice.Core.Text.truncate 240)
            |> Option.defaultValue "n/a"

        $"responseId={responseId}; previousResponseId={previousResponseId}; status={status}; incompleteReason={incompleteReason}; {usage}; error={errorCode}; errorMessage={errorMessage}; events={eventNames}"

    let isTokenLimit events =
        let reason =
            terminalResponse events
            |> Option.bind _.incomplete_details
            |> Option.map _.reason
            |> Option.defaultValue ""

        let status =
            terminalResponse events |> Option.map _.status |> Option.defaultValue ""

        status.Contains("incomplete", StringComparison.OrdinalIgnoreCase)
        && (reason.Contains("max_output", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("token", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("length", StringComparison.OrdinalIgnoreCase))

    let isPreviousResponseNotFound events =
        responseError events
        |> Option.exists (fun err ->
            err.code.Contains("previous_response_not_found", StringComparison.OrdinalIgnoreCase)
            || err.message.Contains("previous response", StringComparison.OrdinalIgnoreCase))
