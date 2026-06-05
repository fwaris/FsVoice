namespace FsVoice.Ctx

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice.Core

type internal ResponseToolCatalog =
    { tools: FsResponses.Tool list
      byName: Map<string, IQaTool> }

module internal QaResponseTools =
    let isBuiltInContextTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && ([ "selected_source_search"
              "source_inventory"
              "durable_memory_search"
              "blackboard_search" ]
            |> List.exists (fun name -> String.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase)))

    let isBlackboardSearchTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && String.Equals(tool.Name, "blackboard_search", StringComparison.OrdinalIgnoreCase)

    let isDurableMemorySearchTool (tool: IQaTool) =
        String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase)
        && String.Equals(tool.Name, "durable_memory_search", StringComparison.OrdinalIgnoreCase)

    let private sanitizeResponseToolName (value: string) =
        let chars =
            value.Trim()
            |> Seq.map (fun ch ->
                if Char.IsLetterOrDigit ch || ch = '_' || ch = '-' then
                    ch
                else
                    '_')
            |> Seq.toArray

        let name = String(chars).Trim('_')

        if String.IsNullOrWhiteSpace name then "tool"
        elif name.Length <= 64 then name
        else name.Substring(0, 64).TrimEnd('_', '-')

    let private responseToolBaseName (tool: IQaTool) =
        if String.Equals(tool.PluginName, "FsVoiceTools", StringComparison.OrdinalIgnoreCase) then
            sanitizeResponseToolName tool.Name
        else
            sanitizeResponseToolName $"{tool.PluginName}_{tool.Name}"

    let private responseToolName usedNames (tool: IQaTool) =
        let baseName = responseToolBaseName tool

        let rec choose index =
            let suffix = if index = 0 then "" else $"_{index}"

            let prefix =
                if baseName.Length + suffix.Length <= 64 then
                    baseName
                else
                    baseName.Substring(0, 64 - suffix.Length).TrimEnd('_', '-')

            let candidate = prefix + suffix

            if Set.contains candidate usedNames then
                choose (index + 1)
            else
                candidate

        choose 0

    let private responseToolParameterSchema (parameter: QaToolParameter) =
        let description = Some parameter.description

        if
            String.Equals(parameter.name, "max_results", StringComparison.OrdinalIgnoreCase)
            || parameter.name.EndsWith("_count", StringComparison.OrdinalIgnoreCase)
        then
            FsResponses.JsProperty.Integer { description = description }
        else
            FsResponses.JsProperty.String
                { description = description
                  enum = None }

    let createCatalog includeBlackboard (catalog: QaToolCatalog) =
        let folder (usedNames, byName, tools) (tool: IQaTool) =
            let name = responseToolName usedNames tool

            let parameters =
                { FsResponses.Parameters.Default with
                    properties =
                        tool.Parameters
                        |> List.map (fun parameter -> parameter.name, responseToolParameterSchema parameter)
                        |> Map.ofList
                    required = tool.Parameters |> List.map _.name
                    additionalProperties = false }

            let responseTool =
                FsResponses.Tool.Function
                    { FsResponses.Function.Default with
                        name = name
                        description = tool.Description
                        parameters = parameters
                        strict = true }

            Set.add name usedNames, Map.add name tool byName, responseTool :: tools

        let _, byName, tools =
            catalog.tools
            |> List.filter (fun tool -> includeBlackboard || not (isBlackboardSearchTool tool))
            |> List.sortBy (fun tool -> tool.PluginName, tool.Name)
            |> List.fold folder (Set.empty, Map.empty, [])

        { tools = List.rev tools
          byName = byName }

    let private jsonElementArgument (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> element.GetString() |> Option.ofObj |> Option.defaultValue ""
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> ""
        | _ -> element.GetRawText()

    let private functionArgumentsToDictionary (arguments: string) =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        if String.IsNullOrWhiteSpace arguments then
            Ok(dict :> IReadOnlyDictionary<string, string>)
        else
            try
                use document = JsonDocument.Parse(arguments)

                if document.RootElement.ValueKind <> JsonValueKind.Object then
                    Error "Function call arguments must be a JSON object."
                else
                    for property in document.RootElement.EnumerateObject() do
                        dict[property.Name] <- jsonElementArgument property.Value

                    Ok(dict :> IReadOnlyDictionary<string, string>)
            with ex ->
                Error $"Function call arguments were not valid JSON: {ex.Message}"

    let functionCalls events =
        let fromItem =
            function
            | FsResponses.IOitem.Function_call call -> Some call
            | _ -> None

        [ for event in events do
              match event with
              | FsResponses.ResponseStreamEvent.OutputItemDone itemEvent ->
                  match fromItem itemEvent.item with
                  | Some call -> yield call
                  | None -> ()
              | FsResponses.ResponseStreamEvent.ResponseCompleted lifecycle ->
                  for item in lifecycle.response.output do
                      match fromItem item with
                      | Some call -> yield call
                      | None -> ()
              | _ -> () ]
        |> List.distinctBy _.call_id

    let private functionOutput callId output =
        FsResponses.IOitem.Function_call_output { call_id = callId; output = output }

    let private functionQuery (call: FsResponses.FunctionCall) (args: IReadOnlyDictionary<string, string>) =
        ToolArguments.tryString "query" args
        |> Option.orElse (ToolArguments.tryString "question" args)
        |> Option.defaultValue call.arguments
        |> Text.truncate 240

    let private invokeFunctionCall
        report
        recordObservation
        turnId
        (responseTools: ResponseToolCatalog)
        (call: FsResponses.FunctionCall)
        cancellationToken
        =
        task {
            match responseTools.byName |> Map.tryFind call.name with
            | None ->
                let output = $"Tool '{call.name}' is not available in this QA session."
                report output
                return functionOutput call.call_id output, None
            | Some tool ->
                match functionArgumentsToDictionary call.arguments with
                | Error error ->
                    let content = $"Tool {tool.PluginName}.{tool.Name} could not run: {error}"
                    let observation = recordObservation turnId tool call.arguments content
                    report content
                    return functionOutput call.call_id content, Some observation
                | Ok args ->
                    let query = functionQuery call args

                    try
                        let! result = tool.InvokeAsync(args, cancellationToken)
                        let observation = recordObservation turnId tool query result.content
                        return functionOutput call.call_id result.content, Some observation
                    with ex ->
                        let content = $"Tool {tool.PluginName}.{tool.Name} failed: {ex.Message}"
                        let observation = recordObservation turnId tool query content
                        report content
                        return functionOutput call.call_id content, Some observation
        }

    let invokeFunctionCalls report recordObservation turnId responseTools calls cancellationToken =
        task {
            let boundedCalls = calls |> List.truncate 8

            let! results =
                boundedCalls
                |> List.map (fun call ->
                    invokeFunctionCall report recordObservation turnId responseTools call cancellationToken)
                |> Task.WhenAll

            let outputs, observations = results |> Array.toList |> List.unzip
            return outputs, observations |> List.choose id
        }
