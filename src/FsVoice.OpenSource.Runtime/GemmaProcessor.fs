namespace FsVoice.OpenSource

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module GemmaResponse =
    [<Literal>]
    let ThoughtStart = "<|channel>thought"

    [<Literal>]
    let ThoughtEnd = "<channel|>"

    let private occurrenceCount (token: string) (text: string) =
        let rec countFrom index count =
            let found = text.IndexOf(token, index, StringComparison.Ordinal)

            if found < 0 then
                count
            else
                countFrom (found + token.Length) (count + 1)

        countFrom 0 0

    let containsProcessLeakage (text: string) =
        let text = if Object.ReferenceEquals(text, null) then "" else text

        let contains (value: string) =
            text.Contains(value, StringComparison.OrdinalIgnoreCase)

        contains "thinking process"
        || contains "analyze the request"
        || contains "analyze the retrieved source"
        || contains "scan the content"
        || contains "synthesize the answer"
        || contains "formulate the response"
        || contains "self-correction/refinement during drafting"
        || contains "final output generation"

    let parse (text: string) : Result<GemmaParsedResponse, GemmaResponseParseError> =
        let source =
            if Object.ReferenceEquals(text, null) then
                ""
            else
                text.Trim()

        if String.IsNullOrWhiteSpace source then
            Error GemmaResponseParseError.EmptyContent
        else
            let startCount = occurrenceCount ThoughtStart source
            let endCount = occurrenceCount ThoughtEnd source

            match startCount, endCount with
            | 0, 0 when containsProcessLeakage source -> Error GemmaResponseParseError.UntaggedProcessText
            | 0, 0 -> Ok { Thought = None; Content = source }
            | 0, _ -> Error GemmaResponseParseError.UnexpectedThoughtChannelDelimiter
            | _, 0 -> Error GemmaResponseParseError.UnclosedThoughtChannel
            | 1, 1 when source.StartsWith(ThoughtStart, StringComparison.Ordinal) ->
                let endIndex =
                    source.IndexOf(ThoughtEnd, ThoughtStart.Length, StringComparison.Ordinal)

                if endIndex < ThoughtStart.Length then
                    Error GemmaResponseParseError.UnexpectedThoughtChannelDelimiter
                else
                    let thought =
                        source.Substring(ThoughtStart.Length, endIndex - ThoughtStart.Length).Trim()

                    let content = source.Substring(endIndex + ThoughtEnd.Length).Trim()

                    if String.IsNullOrWhiteSpace content then
                        Error GemmaResponseParseError.EmptyContent
                    elif containsProcessLeakage content then
                        Error GemmaResponseParseError.UntaggedProcessText
                    else
                        Ok
                            { Thought = thought |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                              Content = content }
            | 1, 1 -> Error GemmaResponseParseError.UnexpectedThoughtChannelDelimiter
            | _ -> Error GemmaResponseParseError.RepeatedThoughtChannel

type GemmaProcessor() =
    let escapeText (text: string) =
        if Object.ReferenceEquals(text, null) then "" else text

    let quoteValue (text: string) =
        let value = if Object.ReferenceEquals(text, null) then "" else text
        "<|\"|>" + value.Replace("<|\"|>", "\"") + "<|\"|>"

    let renderToolDeclaration (tool: GemmaToolDeclaration) =
        let properties =
            tool.Parameters
            |> Array.map (fun parameter ->
                let description =
                    if String.IsNullOrWhiteSpace parameter.Description then
                        ""
                    else
                        ",description:" + quoteValue parameter.Description

                $"{parameter.Name}:{{type:{quoteValue (parameter.Type.ToUpperInvariant())}{description}}}")
            |> String.concat ","

        let required =
            tool.Parameters
            |> Array.filter _.Required
            |> Array.map (fun parameter -> quoteValue parameter.Name)
            |> String.concat ","

        let objectType = quoteValue "OBJECT"
        $"<|tool>declaration:{tool.Name}{{description:{quoteValue tool.Description},parameters:{{properties:{{{properties}}},required:[{required}],type:{objectType}}}}}<tool|>"

    let normalizedRole =
        function
        | GemmaChatRole.System -> "system"
        | GemmaChatRole.User -> "user"
        | GemmaChatRole.Model -> "model"
        | GemmaChatRole.Tool -> "tool"

    let renderMessage (message: GemmaChatMessage) =
        match message.Role with
        | GemmaChatRole.Tool ->
            let name = message.ToolName |> Option.defaultValue "tool"
            $"<|turn>tool\n<|tool_response>response:{name}{{value:{quoteValue message.Content}}}<tool_response|><turn|>\n"
        | _ -> $"<|turn>{normalizedRole message.Role}\n{escapeText message.Content}<turn|>\n"

    let parseToolArguments (body: string) =
        let arguments = Dictionary<string, string>(StringComparer.Ordinal)

        let pattern =
            @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:<\|""\|>(?<quoted>.*?)<\|""\|>|(?<bare>[^,}]+))"

        for item in Regex.Matches(body, pattern, RegexOptions.Singleline) do
            let name = item.Groups["name"].Value

            let value =
                if item.Groups["quoted"].Success then
                    item.Groups["quoted"].Value
                else
                    item.Groups["bare"].Value.Trim()

            arguments[name] <- value

        arguments |> Seq.map (fun item -> item.Key, item.Value) |> Map.ofSeq

    member _.RenderChat
        (messages: GemmaChatMessage array, tools: GemmaToolDeclaration array, addGenerationPrompt: bool)
        =
        let builder = StringBuilder("<bos>")

        let systemMessages =
            messages |> Array.filter (fun message -> message.Role = GemmaChatRole.System)

        let nonSystemMessages =
            messages |> Array.filter (fun message -> message.Role <> GemmaChatRole.System)

        if systemMessages.Length > 0 || tools.Length > 0 then
            builder.Append("<|turn>system\n") |> ignore

            if systemMessages.Length > 0 then
                systemMessages
                |> Array.map _.Content
                |> String.concat "\n\n"
                |> escapeText
                |> builder.Append
                |> ignore

            if tools.Length > 0 then
                if systemMessages.Length > 0 then
                    builder.Append("\n\n") |> ignore

                tools
                |> Array.iter (fun tool -> builder.Append(renderToolDeclaration tool).Append("\n") |> ignore)

            builder.Append("<turn|>\n") |> ignore

        for message in nonSystemMessages do
            builder.Append(renderMessage message) |> ignore

        if addGenerationPrompt then
            builder.Append("<|turn>model\n") |> ignore

        builder.ToString()

    member _.TryParseToolCall(text: string) =
        let source = if Object.ReferenceEquals(text, null) then "" else text

        let patterns =
            [| @"<\|tool_call\>\s*call:(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<args>.*?)\}\s*<tool_call\|>"
               @"(?:^|\s)call:(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<args>.*?)\}(?:\s|$)" |]

        let matchValue =
            patterns
            |> Array.map (fun pattern -> Regex.Match(source, pattern, RegexOptions.Singleline))
            |> Array.tryFind _.Success
            |> Option.defaultValue Match.Empty

        if matchValue.Success then
            Some
                { Name = matchValue.Groups["name"].Value
                  Arguments = parseToolArguments matchValue.Groups["args"].Value
                  RawText = matchValue.Value }
        else
            None
