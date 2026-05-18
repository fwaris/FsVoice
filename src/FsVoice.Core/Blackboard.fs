namespace FsVoice.Core

open System.Text.Json
open FsVoice

type Blackboard =
    { turns: VoiceTurn list
      toolObservations: VoiceToolObservation list
      activeSuggestion: VoiceSuggestion option
      scratch: Map<string, JsonElement> }

module Blackboard =
    let empty =
        { turns = []
          toolObservations = []
          activeSuggestion = None
          scratch = Map.empty }

    let private scratchKey pluginId key = $"{VoicePluginId.value pluginId}.{key}"

    let appendTurn turn blackboard =
        { blackboard with
            turns = blackboard.turns @ [ turn ] }

    let appendToolObservation observation blackboard =
        { blackboard with
            toolObservations = blackboard.toolObservations @ [ observation ] }

    let setSuggestion suggestion blackboard =
        { blackboard with
            activeSuggestion = suggestion }

    let putScratch pluginId key value blackboard =
        { blackboard with
            scratch = blackboard.scratch |> Map.add (scratchKey pluginId key) value }

    let tryGetScratch pluginId key blackboard =
        blackboard.scratch |> Map.tryFind (scratchKey pluginId key)
