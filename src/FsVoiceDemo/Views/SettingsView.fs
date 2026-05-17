namespace FsVoiceDemo.Views

open System
open Fabulous.Maui
open FsVoiceDemo
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module SettingsView =
    let private isRealtimeActive model =
        model.bundle.IsSome || model.sessionState <> RTOpenAI.WebRTC.State.Disconnected

    let private canEditSettings model =
        not model.isBusy && not (isRealtimeActive model)

    let private retrievalModeToggled enabled =
        if enabled then
            RetrievalModeChanged FsColbertWithFallback
        else
            RetrievalModeChanged InternalDocumentIndex

    let private roleLabel role =
        $"{FsVoice.QA.ModelRole.storageName role} model"

    let private roleValue model role =
        model.modelRoleOverrides
        |> Map.tryFind role
        |> Option.defaultValue (FsVoice.QA.UseCaseDefinition.model role model.activeUseCase).modelId

    let private facetValue model (field: FsVoice.QA.UseCaseSettingsField) =
        model.useCaseSettings
        |> Map.tryFind field.key
        |> Option.orElse field.defaultValue
        |> Option.defaultValue ""

    let private parseBool (value: string) =
        match Boolean.TryParse(value) with
        | true, parsed -> parsed
        | false, _ -> false

    let private isBoolFacet (field: FsVoice.QA.UseCaseSettingsField) =
        match (defaultArg (Option.ofObj field.kind) "").Trim().ToLowerInvariant() with
        | "bool"
        | "boolean"
        | "toggle"
        | "switch" -> true
        | _ -> false

    let private sectionTitle text =
        Label(text)
            .font(size = 15., attributes = FontAttributes.Bold)
            .centerVertical()
            .gridRow(0)
            .gridColumnSpan(3)
            .margin (2.)

    let private sectionBorder content =
        Border(content)
            .stroke(SolidColorBrush(Colors.LightGray))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private settingsForm model =
        let canEditSettings = canEditSettings model
        let roles = FsVoice.QA.ModelRole.all
        let columns = [ Dimension.Absolute 112.; Dimension.Star; Dimension.Absolute 48. ]

        VStack(spacing = 12.) {
            sectionBorder (
                (Grid(columns, [ Dimension.Absolute 34.; Dimension.Absolute 48.; Dimension.Absolute 48. ]) {
                    sectionTitle "Account"

                    ViewControls.formLabel "OpenAI key" 1

                    Entry(model.openAiKey, OpenAiKeyChanged)
                        .isPassword(model.hideSecrets)
                        .placeholder("OpenAI API key")
                        .isEnabled(canEditSettings)
                        .gridRow(1)
                        .gridColumn(1)
                        .margin (2.)

                    (ViewControls.compactIconButton
                        (if model.hideSecrets then
                             Icons.visible
                         else
                             Icons.visibilityOff)
                        ToggleSecretVisibility)
                        .isEnabled(canEditSettings)
                        .gridRow(1)
                        .gridColumn (2)

                    ViewControls.formLabel "Use Case" 2

                    Label($"{model.activeUseCase.displayName} ({model.activeUseCase.id})")
                        .font(size = 13.)
                        .centerVertical()
                        .gridRow(2)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)
                })
                    .padding (10.)
            )

            sectionBorder (
                (Grid(
                    columns,
                    Dimension.Absolute 34.
                    :: List.init roles.Length (fun _ -> Dimension.Absolute 48.)
                ) {
                    sectionTitle "Models"

                    for row, role in roles |> List.indexed do
                        let row = row + 1

                        ViewControls.formLabel (roleLabel role) row

                        Entry(roleValue model role, fun value -> ModelRoleModelChanged(role, value))
                            .placeholder(roleLabel role)
                            .isEnabled(canEditSettings)
                            .gridRow(row)
                            .gridColumn(1)
                            .gridColumnSpan(2)
                            .margin (2.)
                })
                    .padding (10.)
            )

            sectionBorder (
                (Grid(
                    columns,
                    List.init 6 (fun row ->
                        if row = 0 then
                            Dimension.Absolute 34.
                        else
                            Dimension.Absolute 48.)
                ) {
                    sectionTitle "Retrieval"

                    ViewControls.formLabel "Mode" 1

                    (HStack(spacing = 8.) {
                        Switch(model.retrievalMode = FsColbertWithFallback, retrievalModeToggled)
                            .isEnabled(canEditSettings)
                            .centerVertical ()

                        Label(RetrievalModes.displayName model.retrievalMode).font(size = 13.).centerVertical ()
                    })
                        .gridRow(1)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                    ViewControls.formLabel "Lexical Filter" 2

                    Switch(model.useLexicalFilter, UseLexicalFilterToggled)
                        .isEnabled(canEditSettings)
                        .gridRow(2)
                        .gridColumn(1)
                        .centerVertical ()

                    ViewControls.formLabel "Log Expansions" 3

                    Switch(model.logExpansions, LogExpansionsToggled)
                        .isEnabled(canEditSettings)
                        .gridRow(3)
                        .gridColumn(1)
                        .centerVertical ()

                    ViewControls.formLabel "Log Chunks" 4

                    Switch(model.logChunks, LogChunksToggled)
                        .isEnabled(canEditSettings)
                        .gridRow(4)
                        .gridColumn(1)
                        .centerVertical ()
                })
                    .padding (10.)
            )

            sectionBorder (
                (Grid(
                    columns,
                    List.init 4 (fun row ->
                        if row = 0 then
                            Dimension.Absolute 34.
                        else
                            Dimension.Absolute 48.)
                ) {
                    sectionTitle "PDF Parsing"

                    ViewControls.formLabel "PDF Parser" 1

                    (HStack(spacing = 8.) {
                        Switch(model.useHybridPdfParsing, UseHybridPdfParsingToggled)
                            .isEnabled(canEditSettings)
                            .centerVertical ()

                        Label(if model.useHybridPdfParsing then "Hybrid" else "Legacy")
                            .font(size = 13.)
                            .centerVertical ()
                    })
                        .gridRow(1)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                    ViewControls.formLabel "Layout Analysis" 2

                    (HStack(spacing = 8.) {
                        Switch(model.useLayoutAnalysis, UseLayoutAnalysisToggled)
                            .isEnabled(canEditSettings && model.useHybridPdfParsing)
                            .centerVertical ()

                        Label(if model.useLayoutAnalysis then "On - slower" else "Off")
                            .font(size = 13.)
                            .centerVertical ()
                    })
                        .gridRow(2)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                    ViewControls.formLabel "Index Keywords" 3

                    Switch(model.elaborateIndexKeywords, ElaborateIndexKeywordsToggled)
                        .isEnabled(canEditSettings)
                        .gridRow(3)
                        .gridColumn(1)
                        .centerVertical ()
                })
                    .padding (10.)
            )

            sectionBorder (
                (Grid(
                    columns,
                    [ Dimension.Absolute 34.
                      Dimension.Absolute 48.
                      Dimension.Absolute 48.
                      Dimension.Absolute 48. ]
                ) {
                    sectionTitle "Links"

                    ViewControls.formLabel "Privacy" 1

                    Button("Privacy Policy", OpenAppLink PrivacyPolicy)
                        .font(size = 13.)
                        .gridRow(1)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                    ViewControls.formLabel "Licenses" 2

                    Button("Third-Party Notices", OpenAppLink ThirdPartyNotices)
                        .font(size = 13.)
                        .gridRow(2)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                    ViewControls.formLabel "Settings" 3

                    Button("Help", OpenAppLink SettingsHelp)
                        .font(size = 13.)
                        .gridRow(3)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)
                })
                    .padding (10.)
            )

            if not (List.isEmpty model.activeUseCase.settingsFacets) then
                sectionBorder (
                    (Grid(
                        columns,
                        Dimension.Absolute 34.
                        :: List.init model.activeUseCase.settingsFacets.Length (fun _ -> Dimension.Absolute 48.)
                    ) {
                        sectionTitle "Use Case Settings"

                        for row, field in model.activeUseCase.settingsFacets |> List.indexed do
                            let row = row + 1

                            ViewControls.formLabel field.label row

                            if isBoolFacet field then
                                Switch(
                                    parseBool (facetValue model field),
                                    fun value -> UseCaseSettingChanged(field.key, string value)
                                )
                                    .isEnabled(canEditSettings)
                                    .gridRow(row)
                                    .gridColumn(1)
                                    .centerVertical ()
                            else
                                Entry(facetValue model field, fun value -> UseCaseSettingChanged(field.key, value))
                                    .placeholder(field.label)
                                    .isEnabled(canEditSettings)
                                    .gridRow(row)
                                    .gridColumn(1)
                                    .gridColumnSpan(2)
                                    .margin (2.)
                    })
                        .padding (10.)
                )
        }

    let contentPage (model: Model) =
        ContentPage(
            ScrollView(
                (VStack(spacing = 12.) {
                    ToolbarView.settings

                    settingsForm model

                })
                    .padding (18.)
            )
        )
            .title ("Settings")
