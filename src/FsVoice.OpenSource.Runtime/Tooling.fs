namespace FsVoice.OpenSource

open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsVoice.Ctx

type private OpenSourceToolHost(contextProviders: IQaContextProvider list, report: string -> unit) =
    let reportSearch stage question maxResults resultCount sources =
        JsonSerializer.Serialize(
            {| event = $"search.{stage}"
               query = question
               maxResults = maxResults
               providerCount = contextProviders.Length
               resultCount = resultCount
               sources = sources |},
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        )
        |> report

    interface IQaToolHost with
        member _.Report message = report message

        member _.SearchKnowledgeAsync(question, maxResults, cancellationToken) =
            task {
                reportSearch "started" question maxResults 0 Array.empty

                if List.isEmpty contextProviders then
                    reportSearch "completed" question maxResults 0 Array.empty
                    return "No document sources are configured for this open-source voice session."
                else
                    let! chunks =
                        contextProviders
                        |> List.map (fun provider ->
                            provider.RetrieveAsync(
                                { query = question
                                  maxResults = maxResults },
                                cancellationToken
                            ))
                        |> Task.WhenAll

                    let ranked =
                        chunks
                        |> Array.collect List.toArray
                        |> Array.sortByDescending _.score
                        |> Array.truncate maxResults

                    let sources = ranked |> Array.map _.source.DisplayName |> Array.distinct

                    reportSearch "completed" question maxResults ranked.Length sources

                    if ranked.Length = 0 then
                        return "No relevant source passages were found."
                    else
                        let lines =
                            ranked
                            |> Array.mapi (fun index chunk ->
                                let source = chunk.source.DisplayName
                                let score = chunk.score.ToString("0.000", CultureInfo.InvariantCulture)
                                $"{index + 1}. {source} chunk {chunk.index} score={score}\n{chunk.text}")

                        return String.Join("\n\n", lines)
            }

        member _.SourceInventoryAsync(cancellationToken) =
            task {
                if List.isEmpty contextProviders then
                    return "No document sources are configured for this open-source voice session."
                else
                    let! inventories =
                        contextProviders
                        |> List.map (fun provider -> provider.InventoryAsync cancellationToken)
                        |> Task.WhenAll

                    return String.Join("\n\n", inventories)
            }

        member _.SearchMemoryAsync(_query, _maxResults, _cancellationToken) =
            Task.FromResult "Durable memory search is not enabled for this open-source voice backend."

        member _.SearchBlackboardAsync(_query, _cancellationToken) =
            Task.FromResult "Blackboard search is not enabled for this open-source voice backend."

type private GetCurrentTimeTool() =
    interface IQaTool with
        member _.PluginName = "FsVoiceTools"
        member _.Name = "get_current_time"
        member _.Description = "Return the current local and UTC time for the running host."
        member _.Parameters = []

        member _.InvokeAsync(_: IReadOnlyDictionary<string, string>, _: CancellationToken) =
            let now = DateTimeOffset.Now

            let payload =
                {| local = now.ToString("O", CultureInfo.InvariantCulture)
                   utc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                   timeZone = TimeZoneInfo.Local.Id |}

            JsonSerializer.Serialize(payload, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
            |> QaToolResult.text
            |> Task.FromResult

type private GetAgentStatusTool(status: unit -> string) =
    interface IQaTool with
        member _.PluginName = "FsVoiceTools"
        member _.Name = "get_agent_status"

        member _.Description =
            "Return a compact status snapshot for the local Gemma and Pocket TTS voice agent runtime."

        member _.Parameters = []

        member _.InvokeAsync(_: IReadOnlyDictionary<string, string>, _: CancellationToken) =
            status () |> QaToolResult.text |> Task.FromResult

type OpenSourceToolCatalog =
    { Declarations: GemmaToolDeclaration array
      Invoke: string -> Map<string, string> -> CancellationToken -> Task<bool * string * string option>
      ToolNames: string array }

module OpenSourceTooling =
    let private parameterType (parameter: QaToolParameter) =
        if
            String.Equals(parameter.name, "max_results", StringComparison.OrdinalIgnoreCase)
            || parameter.name.EndsWith("_count", StringComparison.OrdinalIgnoreCase)
        then
            "integer"
        else
            "string"

    let private declarationFromTool (tool: IQaTool) =
        { Name = tool.Name
          Description = tool.Description
          Parameters =
            tool.Parameters
            |> List.map (fun parameter ->
                { Name = parameter.name
                  Description = parameter.description
                  Type = parameterType parameter
                  Required = parameter.required })
            |> List.toArray }

    let private mapToReadOnlyDictionary (arguments: Map<string, string>) =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        for KeyValue(key, value) in arguments do
            dict[key] <- value

        dict :> IReadOnlyDictionary<string, string>

    let create contextProviders status report =
        let host = OpenSourceToolHost(contextProviders, report) :> IQaToolHost

        let sourceTools =
            QaToolLoader.builtInTools host
            |> List.filter (fun tool ->
                String.Equals(tool.Name, "selected_source_search", StringComparison.OrdinalIgnoreCase)
                || String.Equals(tool.Name, "source_inventory", StringComparison.OrdinalIgnoreCase))

        let tools =
            [ yield! sourceTools
              yield GetCurrentTimeTool() :> IQaTool
              yield GetAgentStatusTool(status) :> IQaTool ]

        let byName = tools |> List.map (fun tool -> tool.Name, tool) |> Map.ofList

        let invoke name arguments cancellationToken =
            task {
                match byName |> Map.tryFind name with
                | None -> return false, "", Some $"Tool '{name}' is not whitelisted."
                | Some tool ->
                    try
                        let! result = tool.InvokeAsync(mapToReadOnlyDictionary arguments, cancellationToken)
                        return true, result.content, None
                    with ex ->
                        return false, "", Some $"Tool {tool.PluginName}.{tool.Name} failed: {ex.Message}"
            }

        { Declarations = tools |> List.map declarationFromTool |> List.toArray
          Invoke = invoke
          ToolNames = tools |> List.map _.Name |> List.toArray }
