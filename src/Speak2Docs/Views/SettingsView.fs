namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module SettingsView =
    let private isRealtimeActive model =
        model.bundle.IsSome
        || model.pendingConnectionId.IsSome
        || model.sessionState <> RTOpenAI.WebRTC.State.Disconnected

    let private canEditSettings model =
        not model.isBusy && not (isRealtimeActive model)

    let private retrievalModeToggled enabled =
        if enabled then
            RetrievalModeChanged FsColbertWithFallback
        else
            RetrievalModeChanged InternalDocumentIndex

    let private activityLogToggled enabled =
        if enabled then
            ActivityLogVerbosityChanged Verbose
        else
            ActivityLogVerbosityChanged Informational

    let private roleLabel role =
        $"{FsVoice.Ctx.ModelRole.storageName role} model"

    let private roleValue model role =
        model.modelRoleOverrides
        |> Map.tryFind role
        |> Option.defaultValue (FsVoice.Ctx.PlugInDefinition.model role model.activePlugIn).modelId

    let private reasoningEffortOptions = [ "Low"; "Medium"; "High" ]

    let private reasoningEffortValues = [ "low"; "medium"; "high" ]

    let private reasoningEffortIndex value =
        let normalized = Settings.normalizeAnswerReasoningEffort value

        reasoningEffortValues
        |> List.tryFindIndex ((=) normalized)
        |> Option.defaultValue 0

    let private reasoningEffortChanged index =
        reasoningEffortValues
        |> List.tryItem index
        |> Option.defaultValue ""
        |> AnswerReasoningEffortChanged

    let private sectionTitle text =
        Label(text)
            .font(size = 15., attributes = FontAttributes.Bold)
            .centerVertical()
            .gridRow(0)
            .gridColumnSpan(3)
            .margin (2.)

    let private sectionBorder appTheme content =
        Border(content)
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private columns =
        [ Dimension.Absolute 112.; Dimension.Star; Dimension.Absolute 48. ]

    let private sectionRows count =
        Dimension.Absolute 34. :: List.init count (fun _ -> Dimension.Absolute 48.)

    let private switchWithText (isOn: bool) (onChanged: bool -> Msg) (canEdit: bool) text =
        Grid([ Dimension.Auto; Dimension.Star ], [ Dimension.Star ]) {
            Switch(isOn, onChanged).isEnabled(canEdit).centerVertical().gridColumn (0)

            Label(text)
                .font(size = 13.)
                .lineBreakMode(LineBreakMode.WordWrap)
                .maxLines(2)
                .centerVertical()
                .gridColumn(1)
                .margin (8., 0., 0., 0.)
        }

    let private accountSection model canEdit =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 1) {
                sectionTitle "Account"

                ViewControls.formLabel "OpenAI key" 1

                Entry(model.openAiKey, OpenAiKeyChanged)
                    .isPassword(model.hideSecrets)
                    .placeholder("OpenAI API key")
                    .isEnabled(canEdit)
                    .gridRow(1)
                    .gridColumn(1)
                    .margin (2.)

                (ViewControls.compactIconButton
                    (if model.hideSecrets then
                         Icons.visible
                     else
                         Icons.visibilityOff)
                    ToggleSecretVisibility)
                    .isEnabled(canEdit)
                    .gridRow(1)
                    .gridColumn (2)
            })
                .padding (10.))

    let private modelsSection model canEdit =
        let roles = FsVoice.Ctx.ModelRole.all
        let reasoningRow = roles.Length + 1
        let tokenRow = roles.Length + 2
        let toolLoopRow = roles.Length + 3

        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows toolLoopRow) {
                sectionTitle "Models"

                for row, role in roles |> List.indexed do
                    let row = row + 1

                    ViewControls.formLabel (roleLabel role) row

                    Entry(roleValue model role, fun value -> ModelRoleModelChanged(role, value))
                        .placeholder(roleLabel role)
                        .isEnabled(canEdit)
                        .gridRow(row)
                        .gridColumn(1)
                        .gridColumnSpan(2)
                        .margin (2.)

                ViewControls.formLabel "Reasoning Level" reasoningRow

                Picker(reasoningEffortOptions, reasoningEffortIndex model.answerReasoningEffort, reasoningEffortChanged)
                    .title("Reasoning level")
                    .isEnabled(canEdit)
                    .gridRow(reasoningRow)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Max Answer Tokens" tokenRow

                Entry(model.answerMaxOutputTokens, AnswerMaxOutputTokensChanged)
                    .placeholder("2500")
                    .isEnabled(canEdit)
                    .gridRow(tokenRow)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Tool Rounds" toolLoopRow

                Entry(model.answerToolCallLoopLimit, AnswerToolCallLoopLimitChanged)
                    .placeholder("3")
                    .isEnabled(canEdit)
                    .gridRow(toolLoopRow)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)
            })
                .padding (10.))

    let private activitySection model canEdit =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 1) {
                sectionTitle "Activity"

                ViewControls.formLabel "Log Level" 1

                (switchWithText
                    (model.activityLogVerbosity = Verbose)
                    activityLogToggled
                    canEdit
                    (ActivityLog.displayName model.activityLogVerbosity))
                    .gridRow(1)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)
            })
                .padding (10.))

    let private audioSection model canEdit =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 1) {
                sectionTitle "Audio"

                ViewControls.formLabel "Default Speaker" 1

                (switchWithText
                    model.audioDefaultToSpeaker
                    AudioDefaultToSpeakerToggled
                    canEdit
                    (if model.audioDefaultToSpeaker then
                         "Speaker"
                     else
                         "Receiver/headset"))
                    .gridRow(1)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)
            })
                .padding (10.))

    let private retrievalSection model canEdit =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 5) {
                sectionTitle "Retrieval"

                ViewControls.formLabel "Mode" 1

                (switchWithText
                    (model.retrievalMode = FsColbertWithFallback)
                    retrievalModeToggled
                    canEdit
                    (RetrievalModes.displayName model.retrievalMode))
                    .gridRow(1)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Lexical Filter" 2

                Switch(model.useLexicalFilter, UseLexicalFilterToggled)
                    .isEnabled(canEdit)
                    .gridRow(2)
                    .gridColumn(1)
                    .centerVertical ()

                ViewControls.formLabel "Log Expansions" 3

                Switch(model.logExpansions, LogExpansionsToggled)
                    .isEnabled(canEdit)
                    .gridRow(3)
                    .gridColumn(1)
                    .centerVertical ()

                ViewControls.formLabel "Log Chunks" 4

                Switch(model.logChunks, LogChunksToggled).isEnabled(canEdit).gridRow(4).gridColumn(1).centerVertical ()
            })
                .padding (10.))

    let private pdfParsingSection model canEdit =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 4) {
                sectionTitle "PDF Parsing"

                ViewControls.formLabel "PDF Parser" 1

                (switchWithText
                    model.useHybridPdfParsing
                    UseHybridPdfParsingToggled
                    canEdit
                    (if model.useHybridPdfParsing then "Hybrid" else "Legacy"))
                    .gridRow(1)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Layout Analysis" 2

                (switchWithText
                    model.useLayoutAnalysis
                    UseLayoutAnalysisToggled
                    (canEdit && model.useHybridPdfParsing)
                    (if model.useLayoutAnalysis then "On - slower" else "Off"))
                    .gridRow(2)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Index Keywords" 3

                Switch(model.elaborateIndexKeywords, ElaborateIndexKeywordsToggled)
                    .isEnabled(canEdit)
                    .gridRow(3)
                    .gridColumn(1)
                    .centerVertical ()

                ViewControls.formLabel "Describe Visuals" 4

                (switchWithText
                    model.describePdfVisuals
                    DescribePdfVisualsToggled
                    (canEdit && model.useHybridPdfParsing && model.useLayoutAnalysis)
                    (if model.describePdfVisuals then "On - slower" else "Off"))
                    .gridRow(4)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)
            })
                .padding (10.))

    let private linksSection model =
        sectionBorder
            model.appTheme
            ((Grid(columns, sectionRows 4) {
                sectionTitle "Links"

                ViewControls.formLabel "Terms" 1

                Button("Terms of Use", OpenAppLink TermsOfUse)
                    .font(size = 13.)
                    .gridRow(1)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Privacy" 2

                Button("Privacy Policy", OpenAppLink PrivacyPolicy)
                    .font(size = 13.)
                    .gridRow(2)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "Licenses" 3

                Button("Third-Party Notices", OpenAppLink ThirdPartyNotices)
                    .font(size = 13.)
                    .gridRow(3)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)

                ViewControls.formLabel "AI Data" 4

                Button(
                    (if model.openAiDisclosureSuppressed then
                         "OpenAI Notice: Hidden"
                     else
                         "OpenAI Data Notice"),
                    OpenAiDisclosure_Show ReviewOnly
                )
                    .font(size = 13.)
                    .gridRow(4)
                    .gridColumn(1)
                    .gridColumnSpan(2)
                    .margin (2.)
            })
                .padding (10.))

    let private settingsForm model =
        let canEdit = canEditSettings model

        VStack(spacing = 12.) {
            accountSection model canEdit
            modelsSection model canEdit
            activitySection model canEdit
            audioSection model canEdit
            retrievalSection model canEdit
            pdfParsingSection model canEdit
            linksSection model
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
            .background(Theme.pageBackgroundColor model.appTheme)
            .title ("Settings")
