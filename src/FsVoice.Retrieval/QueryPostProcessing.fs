namespace FsVoice.Retrieval

open System
open System.Text.RegularExpressions
open FsVoice.Core
open FsVoice.Ctx

type ProcessedQuery =
    { normalizedQuery: string
      searchTerms: string list
      rewrittenQueries: string list }

module QueryPostProcessing =
    let private regexOptions = RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant

    let private replace (pattern: string) (replacement: string) (value: string) =
        Regex.Replace(value, pattern, replacement, regexOptions)

    let private applyReplacements replacements value =
        replacements
        |> List.fold (fun current (pattern, replacement) -> replace pattern replacement current) value

    let private fillerReplacements =
        [ @"\b(?:um+|uh+|er+|ah+|hmm)\b[,\s]*", " "
          @"\b(?:you\s+know|i\s+mean)\b[,\s]*", " "
          @"^\s*(?:okay|ok|hey|so)\b[,\s]*", ""
          @"^\s*(?:can|could|would)\s+you\s+(?:please\s+)?", "" ]

    let private profileReplacements (profile: QaPlugInProfile) =
        let profile = QaPlugInProfile.sanitize profile

        profile.voiceReplacements
        |> List.map (fun replacement -> replacement.pattern, replacement.replacement)

    let private boundary phrase =
        @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])"

    let private containsPhrase (phrase: string) (value: string) =
        Regex.IsMatch(value, boundary phrase, regexOptions)

    let private expansionTerms profile normalized =
        let profile = QaPlugInProfile.sanitize profile

        profile.queryExpansionRules
        |> List.collect (fun rule ->
            if rule.triggers |> List.exists (fun trigger -> containsPhrase trigger normalized) then
                rule.terms
            else
                [])
        |> Seq.distinctBy _.ToLowerInvariant()
        |> Seq.toList

    let forVoiceLikeRetrievalWithProfile profile query =
        let normalized =
            defaultArg (Option.ofObj query) ""
            |> Text.normalizeWhitespace
            |> applyReplacements (profileReplacements profile)
            |> applyReplacements fillerReplacements
            |> Text.normalizeWhitespace

        let fallback =
            if String.IsNullOrWhiteSpace normalized then
                Text.normalizeWhitespace query
            else
                normalized

        let expandedTerms = expansionTerms profile fallback

        let searchTerms =
            seq {
                yield fallback
                yield! expandedTerms
            }
            |> Seq.collect Text.terms
            |> Seq.distinct
            |> Seq.truncate 32
            |> Seq.toList

        let expandedQuery =
            if List.isEmpty expandedTerms then
                fallback
            else
                let expansionText = String.concat " " expandedTerms
                Text.normalizeWhitespace $"{fallback} {expansionText}"

        { normalizedQuery = fallback
          searchTerms = searchTerms
          rewrittenQueries =
            [ fallback; expandedQuery; Text.normalizeWhitespace query ]
            |> Seq.choose Text.notEmpty
            |> Seq.distinctBy _.ToLowerInvariant()
            |> Seq.truncate 3
            |> Seq.toList }

    let forVoiceLikeRetrieval query =
        forVoiceLikeRetrievalWithProfile QaPlugInProfile.generic query
