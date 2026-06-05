namespace Speak2Docs

open System
open System.Diagnostics
open System.Threading
open System.Threading.Channels
open FSharp.Control
open Fabulous
open FsVoice.Platform
open Speak2Docs.WorkFlow
open Microsoft.Extensions.AI
open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Controls
open Microsoft.Maui.Devices
open Microsoft.Maui.Graphics
open Microsoft.Maui.Storage
open OpenAI.Chat
#if ANDROID
open Android.Util
#endif

module Update =
    let private minLogFontSize = 10.
    let private maxLogFontSize = 22.
    let private logFontStep = 1.
    let private notificationDurationMs = 3500

    let private clampLogFontSize value =
        min maxLogFontSize (max minLogFontSize value)

    let private expireNotification id =
        async {
            do! Async.Sleep notificationDurationMs
            return id
        }

    let private showNotification text model =
        let id = model.nextNotificationId + 1

        { model with
            notification = Some { id = id; message = text }
            nextNotificationId = id
            log = text :: model.log |> List.truncate C.MAX_LOG },
        Cmd.OfAsync.either expireNotification id NotificationExpired EventError

    let private exitApplication () =
        let quit () =
            let app = Application.Current

            if not (isNull app) then
                app.Quit()

            Environment.Exit(0)

        if MainThread.IsMainThread then
            quit ()
        else
            MainThread.BeginInvokeOnMainThread(Action(quit))

    let private currentAppTheme () =
        let app = Application.Current

        let requestedTheme =
            if isNull app then
                AppInfo.Current.RequestedTheme
            else
                app.RequestedTheme

        match requestedTheme with
        | AppTheme.Unspecified -> AppInfo.Current.RequestedTheme
        | appTheme -> appTheme

    let private sources model =
        KnowledgeSources.selectedSources model.pdfDocuments

    let private sourceKindFromDocument kind =
        match kind with
        | PdfFile -> FsVoice.Ctx.KnowledgeSourceKind.Pdf
        | MarkdownFile -> FsVoice.Ctx.KnowledgeSourceKind.Markdown
        | JsonFile -> FsVoice.Ctx.KnowledgeSourceKind.Json

    let private sourceFromDocument (doc: PdfDocumentSource) : KnowledgeSource =
        { kind = sourceKindFromDocument doc.kind
          location = doc.storedPath
          enabled = true }

    let private isRealtimeActive model =
        model.bundle.IsSome
        || model.pendingConnectionId.IsSome
        || model.sessionState <> RTOpenAI.WebRTC.State.Disconnected

    let private canMutateDocuments model =
        not model.isBusy && not (isRealtimeActive model)

    let private canChangeSourceSelection model =
        not model.isBusy && not (isRealtimeActive model)

    let private documentMutationBlocked model action =
        if model.isBusy then
            Some $"{action} is unavailable while another operation is running."
        elif isRealtimeActive model then
            Some $"{action} is unavailable while realtime is connected."
        else
            None

    let private sourceConfigBlocked model action =
        if model.isBusy then
            Some $"{action} is unavailable while another operation is running."
        elif isRealtimeActive model then
            Some $"{action} is unavailable while realtime is connected."
        else
            None

    let private runtimeSettingsMap (model: Model) =
        seq {
            yield RuntimeSettings.OpenAiKey, model.openAiKey
            yield RuntimeSettings.LogExpansions, string model.logExpansions
            yield RuntimeSettings.LogChunks, string model.logChunks
            yield RuntimeSettings.AnswerMaxOutputTokens, model.answerMaxOutputTokens
            yield RuntimeSettings.AnswerReasoningEffort, model.answerReasoningEffort
            yield RuntimeSettings.AnswerToolCallLoopLimit, model.answerToolCallLoopLimit
            yield RuntimeSettings.UseLexicalFilter, string model.useLexicalFilter
            yield RuntimeSettings.ElaborateIndexKeywords, string model.elaborateIndexKeywords
            yield RuntimeSettings.UseHybridPdfParsing, string model.useHybridPdfParsing
            yield RuntimeSettings.UseLayoutAnalysis, string model.useLayoutAnalysis

            for KeyValue(role, modelId) in model.modelRoleOverrides do
                yield RuntimeSettings.modelRoleKey role, modelId

            for KeyValue(key, value) in model.plugInSettings do
                yield RuntimeSettings.plugInSettingKey key, value
        }
        |> Seq.filter (fun (_, value) -> not (isNull value))
        |> Map.ofSeq

    let private refreshRuntimeSettings model =
        RuntimeSettings.replace model.runtimeSettings (runtimeSettingsMap model)
        model

    let private sourceFileTypes =
        FilePickerFileType(
            dict
                [ DevicePlatform.iOS,
                  [ "com.adobe.pdf"
                    "net.daringfireball.markdown"
                    "public.zip-archive"
                    "com.pkware.zip-archive" ]
                  :> seq<string>
                  DevicePlatform.MacCatalyst,
                  [ "com.adobe.pdf"
                    "net.daringfireball.markdown"
                    "public.zip-archive"
                    "com.pkware.zip-archive" ]
                  :> seq<string>
                  DevicePlatform.Android,
                  [ "application/pdf"
                    "text/markdown"
                    "application/zip"
                    "application/x-zip-compressed" ]
                  :> seq<string>
                  DevicePlatform.WinUI, [ ".pdf"; ".md"; ".zip" ] :> seq<string> ]
        )

    let private saveSettings model =
        refreshRuntimeSettings model |> ignore
        Settings.setOpenAiKey model.openAiKey |> ignore
        Settings.setActivePlugInId model.activePlugIn.id

        model.modelRoleOverrides
        |> Map.iter (fun role modelId -> Settings.setModelRoleModelId model.activePlugIn.id role modelId)

        Settings.setPlugInRetrievalMode model.activePlugIn.id model.retrievalMode
        Settings.setLogExpansions model.logExpansions
        Settings.setLogChunks model.logChunks
        Settings.setActivityLogVerbosity model.activityLogVerbosity
        Settings.setAnswerMaxOutputTokens model.answerMaxOutputTokens
        Settings.setAnswerReasoningEffort model.answerReasoningEffort
        Settings.setAnswerToolCallLoopLimit model.answerToolCallLoopLimit
        Settings.setPlugInUseLexicalFilter model.activePlugIn.id model.useLexicalFilter
        Settings.setPlugInElaborateIndexKeywords model.activePlugIn.id model.elaborateIndexKeywords
        Settings.setUseHybridPdfParsing model.useHybridPdfParsing
        Settings.setUseLayoutAnalysis model.useLayoutAnalysis

        model.plugInSettings
        |> Map.iter (fun key value -> Settings.setPlugInSetting model.activePlugIn.id key value)

    let private savePdfLibraryWithLog docs log =
        let saveLog =
            match Settings.setPdfLibrary docs with
            | Ok _ -> $"Saved document library manifest: {docs.Length} document(s)."
            | Error error -> $"Document library manifest was not saved: {error}"

        saveLog :: log |> List.truncate C.MAX_LOG

    let private mergePrebuiltInstallResult (current: PdfDocumentSource list) (installed: PdfDocumentSource list) =
        let currentById = current |> List.map (fun doc -> doc.id, doc) |> Map.ofList

        let currentUserDocs = current |> List.filter (PdfDocuments.isBuiltIn >> not)

        let installedBuiltInDocs =
            installed
            |> List.filter PdfDocuments.isBuiltIn
            |> List.map (fun doc ->
                match currentById |> Map.tryFind doc.id with
                | Some current -> { doc with selected = current.selected }
                | None -> doc)

        currentUserDocs @ installedBuiltInDocs

    let private processingReport (model: Model) msg =
        let text = $"PDF processing: {msg}"
        Debug.WriteLine text
        Console.WriteLine text
#if ANDROID
        Log.Info("FsVoice", text) |> ignore
#endif
        model.mailbox.Writer.TryWrite(Log_Append msg) |> ignore

    let private createChatClient (key: string) (modelId: string) : IChatClient =
        let client = OpenAI.OpenAIClient(key)
        client.GetResponsesClient().AsIChatClient(modelId)

    let private plugInModelRoleOverrides (definition: FsVoice.Ctx.PlugInDefinition) =
        FsVoice.Ctx.ModelRole.all
        |> List.map (fun role ->
            let fallback = (FsVoice.Ctx.PlugInDefinition.model role definition).modelId
            role, Settings.modelRoleModelId definition.id role fallback)
        |> Map.ofList

    let private composePlugIn (model: Model) =
        PlugInComposer.withHostOverrides
            model.modelRoleOverrides
            model.retrievalMode
            model.useLexicalFilter
            model.elaborateIndexKeywords
            model.activePlugIn

    let private keywordOptions (model: Model) =
        if not model.elaborateIndexKeywords then
            FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions.disabled
        else
            let plugIn = composePlugIn model

            let keywordModel = FsVoice.Ctx.PlugInDefinition.model FsVoice.Ctx.Keyword plugIn

            model.openAiKey
            |> Text.notEmpty
            |> Option.map (fun key ->
                { FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions.defaults with
                    client = Some(createChatClient key keywordModel.modelId)
                    modelId = keywordModel.modelId
                    plugInProfile = plugIn.profile
                    plugInFingerprint = FsVoice.Ctx.PlugInDefinition.fingerprint plugIn })
            |> Option.defaultValue
                { FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions.defaults with
                    client = None
                    modelId = keywordModel.modelId
                    plugInProfile = plugIn.profile
                    plugInFingerprint = FsVoice.Ctx.PlugInDefinition.fingerprint plugIn }

    let private withKeywordCancellation token (options: FsVoice.Retrieval.KnowledgeSources.KeywordGenerationOptions) =
        { options with
            cancellationToken = Some token }

    let private postSources model =
        refreshRuntimeSettings model |> ignore

        match model.bundle with
        | Some bundle ->
            bundle.session.SendFromHostAsync(SourcesChanged(model.retrievalMode, sources model), CancellationToken.None)
            |> ignore
        | None -> ()

    let private pickedResultsToList (results: FileResult seq) =
        if isNull (box results) then [] else results |> Seq.toList

    let private pickAndImportSources existing =
        async {
            try
                let opts =
                    PickOptions(
                        PickerTitle = "Select PDF, Markdown, or Speak2Docs index bundle",
                        FileTypes = sourceFileTypes
                    )

                let tsk () =
                    FilePicker.Default.PickMultipleAsync(opts)

                let! results = MainThread.InvokeOnMainThreadAsync<FileResult seq>(tsk) |> Async.AwaitTask
                let picked = pickedResultsToList results
                let logs = ResizeArray<string>()

                let documentResults =
                    picked
                    |> List.filter (fun result -> PickedSourceFiles.isDocument result.FileName)

                let bundleResults =
                    picked
                    |> List.filter (fun result -> PickedSourceFiles.isIndexBundle result.FileName)

                let unsupported =
                    picked
                    |> List.filter (fun result ->
                        match PickedSourceFiles.kind result.FileName with
                        | UnsupportedPickedSourceFile -> true
                        | PickedDocument
                        | PickedIndexBundle -> false)

                for result in unsupported do
                    logs.Add $"Unsupported source file ignored: {result.FileName}."

                let mutable docs = existing

                for result in bundleResults do
                    use! stream = result.OpenReadAsync() |> Async.AwaitTask
                    let! importedDocs, importLogs = PdfLibrary.importPrebuiltBundle docs result.FileName stream
                    docs <- importedDocs

                    for log in importLogs do
                        logs.Add log

                let! newDocuments = PdfLibrary.copyNewDocuments docs documentResults

                return
                    Ok
                        { documents = docs @ newDocuments
                          newDocuments = newDocuments
                          logs = List.ofSeq logs }
            with ex ->
                return Error ex
        }

    let private processDocuments
        report
        (cancellationToken: CancellationToken)
        keywordOptions
        useHybridPdfParsing
        useLayoutAnalysis
        (docs: PdfDocumentSource list)
        =
        async {
            try
                let parserName = if useHybridPdfParsing then "Hybrid" else "Legacy"

                report $"Starting document processing command for {docs.Length} document(s); parser={parserName}."
                cancellationToken.ThrowIfCancellationRequested()

                let! outcome =
                    PdfLibrary.processDocuments
                        report
                        keywordOptions
                        useHybridPdfParsing
                        useLayoutAnalysis
                        cancellationToken
                        docs

                report $"Document processing command completed for {docs.Length} document(s)."
                return Ok outcome
            with
            | :? OperationCanceledException ->
                report "Document processing was canceled."
                return Ok(Canceled [])
            | ex -> return Error ex
        }

    let private installPrebuiltDocuments docs =
        async {
            try
                let! installed, logs = PdfLibrary.installPrebuiltDocuments docs
                return Ok(installed, logs)
            with ex ->
                return Error ex
        }

    let private restoreBuiltInIndexes docs =
        async {
            try
                let hiddenCount = Settings.hiddenBuiltInSources () |> Set.count
                Settings.clearHiddenBuiltInSources ()
                let! installed, logs = PdfLibrary.installPrebuiltDocuments docs
                return Ok(installed, logs, hiddenCount)
            with ex ->
                return Error ex
        }

    let private deleteDocumentAndIndexes (doc: PdfDocumentSource) =
        async {
            try
                let! removedFile = PdfLibrary.deleteStoredDocument doc
                let! removedIndexCount, indexErrors = KnowledgeSources.clearPersistedIndexes FileSystem.AppDataDirectory

                return
                    Ok
                        { id = doc.id
                          displayName = doc.displayName
                          removedFile = removedFile
                          removedIndexCount = removedIndexCount
                          indexErrors = indexErrors }
            with ex ->
                return Error ex
        }

    let private loadIndexPreviewForDocument (model: Model) documentId =
        async {
            try
                match model.pdfDocuments |> List.tryFind (fun doc -> doc.id = documentId) with
                | None ->
                    let error: exn =
                        InvalidOperationException("Document source was not found for index preview.")

                    return Error error
                | Some doc when not (PdfDocuments.isReady doc) ->
                    let error: exn =
                        InvalidOperationException(
                            $"Index preview is only available for ready sources: {doc.displayName}."
                        )

                    return Error error
                | Some doc ->
                    let source = sourceFromDocument doc

                    let report msg =
                        Debug.WriteLine msg
                        Console.WriteLine msg

                    return
                        KnowledgeSources.loadIndexPreview
                            FileSystem.AppDataDirectory
                            report
                            model.useHybridPdfParsing
                            model.useLayoutAnalysis
                            20
                            source
                        |> Result.mapError (fun err -> InvalidOperationException(err) :> exn)
            with ex ->
                return Error ex
        }

    let private appLinkUrl link =
        match link with
        | TermsOfUse -> C.TERMS_URL
        | PrivacyPolicy -> C.PRIVACY_POLICY_URL
        | ThirdPartyNotices -> C.THIRD_PARTY_NOTICES_URL
        | SettingsHelp -> C.SETTINGS_HELP_URL

    let private openAppLink link : Async<Result<unit, exn>> =
        async {
            try
                let uri = Uri(appLinkUrl link)

                let tsk () = Launcher.Default.OpenAsync(uri)

                let! opened = MainThread.InvokeOnMainThreadAsync<bool>(tsk) |> Async.AwaitTask

                if opened then
                    return Ok()
                else
                    return Error(upcast InvalidOperationException($"Unable to open {uri}."))
            with ex ->
                return Error ex
        }

    let private applyProcessingResult (result: PdfProcessResult) (doc: PdfDocumentSource) : PdfDocumentSource =
        if doc.id <> result.id then
            doc
        else
            match result.error with
            | None ->
                { doc with
                    selected = true
                    status = Ready
                    chunkCount = result.chunkCount
                    error = None }
            | Some err ->
                { doc with
                    selected = false
                    status = Failed
                    chunkCount = 0
                    error = Some err }

    let private sameDocument (left: PdfDocumentSource) (right: PdfDocumentSource) =
        String.Equals(left.id, right.id, StringComparison.OrdinalIgnoreCase)
        || String.Equals(left.storedPath, right.storedPath, StringComparison.OrdinalIgnoreCase)
        || String.Equals(left.displayName, right.displayName, StringComparison.OrdinalIgnoreCase)

    let private pairAvailable processedDocs results =
        let rec pair acc processed results =
            match processed, results with
            | processedDoc :: remainingDocs, result :: remainingResults ->
                pair ((processedDoc, result) :: acc) remainingDocs remainingResults
            | _ -> List.rev acc

        pair [] processedDocs results

    let private processingResultForDocument
        (processedDocs: PdfDocumentSource list)
        (results: PdfProcessResult list)
        (doc: PdfDocumentSource)
        =
        let pairedResults = pairAvailable processedDocs results

        pairedResults
        |> List.tryFind (fun (processedDoc, result) ->
            sameDocument doc processedDoc
            || String.Equals(doc.id, result.id, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun (_, result) -> { result with id = doc.id })

    let private wasSubmittedForProcessing processedDocs doc =
        processedDocs |> List.exists (sameDocument doc)

    let private applyProcessingResults
        (processedDocs: PdfDocumentSource list)
        (results: PdfProcessResult list)
        (docs: PdfDocumentSource list)
        : PdfDocumentSource list =
        docs
        |> List.map (fun doc ->
            match processingResultForDocument processedDocs results doc with
            | Some result -> applyProcessingResult result doc
            | None -> doc)

    let private failProcessingDocuments processedDocs error (docs: PdfDocumentSource list) : PdfDocumentSource list =
        docs
        |> List.map (fun doc ->
            match doc.status with
            | Processing
            | Queued when wasSubmittedForProcessing processedDocs doc ->
                { doc with
                    selected = false
                    status = Failed
                    chunkCount = 0
                    error = Some error }
            | Processing
            | Queued
            | Ready
            | Failed -> doc)

    let private retryDocs ids (docs: PdfDocumentSource list) : PdfDocumentSource list =
        docs
        |> List.map (fun doc ->
            if Set.contains doc.id ids then
                { doc with
                    selected = false
                    status = Processing
                    chunkCount = 0
                    error = None }
            else
                doc)

    let private disposeDocumentProcessingCancellation (model: Model) =
        model.documentProcessingCancellation |> Option.iter _.Dispose()

    let private documentProcessingCommand report (cts: CancellationTokenSource) (model: Model) docs =
        let keywordOptions = keywordOptions model |> withKeywordCancellation cts.Token

        Cmd.OfAsync.either
            (processDocuments report cts.Token keywordOptions model.useHybridPdfParsing model.useLayoutAnalysis)
            docs
            (fun result -> PdfProcessingCompleted(docs, result))
            EventError

    let private stopBundleCommand (bundle: ConnectionBundle) =
        Cmd.OfAsync.either Connect.stop bundle (fun result -> StopCompleted(bundle.id, result)) EventError

    let private currentRealtimeConnectionId model =
        model.bundle |> Option.map _.id |> Option.orElse model.pendingConnectionId

    let private isCurrentRealtimeConnection connectionId model =
        currentRealtimeConnectionId model
        |> Option.exists (fun current -> String.Equals(current, connectionId, StringComparison.OrdinalIgnoreCase))

    let private markRealtimeDisconnected connectionId model =
        { model with
            disconnectedConnectionIds = model.disconnectedConnectionIds |> Set.add connectionId }

    let private forgetRealtimeDisconnected connectionId model =
        { model with
            disconnectedConnectionIds = model.disconnectedConnectionIds |> Set.remove connectionId }

    let private cleanupFailedRealtimeConnection connectionId error model =
        let log =
            $"Realtime connection failed: {error}" :: model.log |> List.truncate C.MAX_LOG

        if not (isCurrentRealtimeConnection connectionId model) then
            forgetRealtimeDisconnected connectionId model, Cmd.none
        else
            match model.bundle with
            | Some bundle when
                String.Equals(bundle.id, connectionId, StringComparison.OrdinalIgnoreCase)
                && not model.isBusy
                ->
                { model with
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = true
                    log = log }
                |> forgetRealtimeDisconnected connectionId,
                stopBundleCommand bundle
            | _ ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none

    let private handleRealtimeStateChanged connectionId state model =
        if not (isCurrentRealtimeConnection connectionId model) then
            model, Cmd.none
        elif state = RTOpenAI.WebRTC.State.Disconnected then
            let model = markRealtimeDisconnected connectionId model

            match model.bundle with
            | Some bundle when
                String.Equals(bundle.id, connectionId, StringComparison.OrdinalIgnoreCase)
                && not model.isBusy
                ->
                { model with
                    sessionState = state
                    isBusy = true
                    log =
                        "Realtime connection disconnected; cleaning up session." :: model.log
                        |> List.truncate C.MAX_LOG },
                stopBundleCommand bundle
            | _ -> { model with sessionState = state }, Cmd.none
        else
            { model with sessionState = state }, Cmd.none

    let private startParams connectionId openAiDataSharingAcknowledged model =
        refreshRuntimeSettings model |> ignore

        let orchestrationOptions =
            { settings = model.runtimeSettings
              plugIn = model.activePlugIn
              qaPlugIn = model.qaPlugIn
              retrievalMode = model.retrievalMode
              sources = sources model }

        let orchestration =
            DemoVoiceOrchestration(orchestrationOptions) :> IVoiceOrchestration<ToHost, FromHost>

        { connectionId = connectionId
          apiKey = model.openAiKey
          openAiDataSharingAcknowledged = openAiDataSharingAcknowledged
          orchestration = orchestration
          context =
            { storageRoot = FileSystem.AppDataDirectory
              settings = RuntimeSettings.snapshot model.runtimeSettings
              report = fun msg -> model.mailbox.Writer.TryWrite(Log_Append msg) |> ignore }
          mailbox = model.mailbox
          runtimeSettings = model.runtimeSettings }

    let private startRealtimeFlow openAiDataSharingAcknowledged model =
        saveSettings model
        let connectionId = Guid.NewGuid().ToString("N")

        { model with
            isBusy = true
            pendingConnectionId = Some connectionId
            disconnectedConnectionIds = model.disconnectedConnectionIds |> Set.remove connectionId
            sessionState = RTOpenAI.WebRTC.State.Connecting
            openAiDisclosure = None
            log = "Starting realtime Speak2Docs flow..." :: model.log },
        Cmd.OfAsync.either
            Connect.start
            (startParams connectionId openAiDataSharingAcknowledged model)
            (fun result -> StartCompleted(connectionId, result))
            EventError

    let init () =
        let docs = Settings.pdfLibrary ()

        let loadedPlugIn, plugInLogs =
            PlugInHost.loadActive
                FileSystem.AppDataDirectory
                [ typeof<Model>.Assembly ]
                (Some(Settings.activePlugInId ()))

        Settings.setActivePlugInId loadedPlugIn.definition.id

        let modelRoleOverrides = plugInModelRoleOverrides loadedPlugIn.definition

        let retrievalMode =
            loadedPlugIn.definition.runtime.retrievalMode
            |> PlugInComposer.fromQaRetrievalMode
            |> Settings.plugInRetrievalMode loadedPlugIn.definition.id

        let initialLog =
            ("Speak2Docs is ready. Select a source or add a document, then connect."
             :: plugInLogs)
            |> List.truncate C.MAX_LOG

        let runtimeSettings = RuntimeSettings.empty ()
        let openAiDisclosureSuppressed = Settings.shouldSuppressOpenAiDataDisclosure ()

        let model =
            { currentPage = if Settings.hasAcceptedCurrentTerms () then Main else Terms
              mailbox = Channel.CreateBounded<Msg>(100)
              bundle = None
              pendingConnectionId = None
              disconnectedConnectionIds = Set.empty
              sessionState = RTOpenAI.WebRTC.State.Disconnected
              openAiKey = Settings.openAiKey ()
              activePlugIn = loadedPlugIn.definition
              qaPlugIn = loadedPlugIn.plugIn
              runtimeSettings = runtimeSettings
              plugInSettings = Settings.plugInSettings loadedPlugIn.definition.id loadedPlugIn.definition.settingsFacets
              modelRoleOverrides = modelRoleOverrides
              retrievalMode = retrievalMode
              pdfDocuments = docs
              log = initialLog
              logFontSize = 12.
              activityLogVerbosity = Settings.activityLogVerbosity ()
              hideSecrets = true
              openAiDisclosureSuppressed = openAiDisclosureSuppressed
              openAiDisclosure = None
              openAiDisclosureDoNotShowAgain = openAiDisclosureSuppressed
              isBusy = false
              documentProcessingCancellation = None
              logExpansions = Settings.logExpansions ()
              logChunks = Settings.logChunks ()
              answerMaxOutputTokens = string (Settings.answerMaxOutputTokens ())
              answerReasoningEffort = Settings.answerReasoningEffort ()
              answerToolCallLoopLimit = string (Settings.answerToolCallLoopLimit ())
              useLexicalFilter =
                Settings.plugInUseLexicalFilter
                    loadedPlugIn.definition.id
                    loadedPlugIn.definition.runtime.useLexicalFilter
              elaborateIndexKeywords = Settings.plugInElaborateIndexKeywords loadedPlugIn.definition.id false
              useHybridPdfParsing = Settings.useHybridPdfParsing ()
              useLayoutAnalysis = Settings.useLayoutAnalysis ()
              notification = None
              nextNotificationId = 0
              appTheme = currentAppTheme ()
              indexPreview = None }
            |> refreshRuntimeSettings

        model, Cmd.OfAsync.either installPrebuiltDocuments docs PrebuiltDocumentsInstalled EventError

    let update msg model =
        match msg with
        | TermsAccepted ->
            Settings.setAcceptedTermsVersion C.TERMS_VERSION

            { model with
                currentPage = Main
                appTheme = currentAppTheme ()
                log =
                    $"Accepted Speak2Docs Terms of Use and Privacy Policy version {C.TERMS_VERSION}."
                    :: model.log
                    |> List.truncate C.MAX_LOG },
            Cmd.none
        | TermsDeclined ->
            exitApplication ()
            model, Cmd.none
        | OpenAiDisclosure_Show mode ->
            match sourceConfigBlocked model "Showing OpenAI data notice" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    currentPage = Main
                    openAiDisclosure = Some mode
                    openAiDisclosureDoNotShowAgain = model.openAiDisclosureSuppressed },
                Cmd.none
        | OpenAiDisclosureDoNotShowAgainChanged value ->
            { model with
                openAiDisclosureDoNotShowAgain = value },
            Cmd.none
        | OpenAiDisclosureAcknowledged ->
            let disclosureMode = model.openAiDisclosure
            let suppressDisclosure = model.openAiDisclosureDoNotShowAgain

            if suppressDisclosure then
                Settings.setSuppressOpenAiDataDisclosureVersion C.OPENAI_DATA_DISCLOSURE_VERSION
            else
                Settings.clearSuppressOpenAiDataDisclosureVersion ()

            let model =
                { model with
                    currentPage = Main
                    openAiDisclosure = None
                    openAiDisclosureSuppressed = suppressDisclosure
                    log =
                        (if suppressDisclosure then
                             "Acknowledged OpenAI data notice and enabled Do not show again."
                         else
                             "Acknowledged OpenAI data notice for this action.")
                        :: model.log
                        |> List.truncate C.MAX_LOG }

            match disclosureMode with
            | Some ConnectAfterAcknowledgement -> startRealtimeFlow true model
            | Some ReviewOnly
            | None -> model, Cmd.none
        | OpenAiDisclosureDismissed ->
            let message =
                match model.openAiDisclosure with
                | Some ConnectAfterAcknowledgement -> "Connection canceled. No data was sent to OpenAI."
                | Some ReviewOnly
                | None -> "OpenAI data notice dismissed."

            { model with
                currentPage = Main
                openAiDisclosure = None
                openAiDisclosureDoNotShowAgain = model.openAiDisclosureSuppressed }
            |> showNotification message
        | OpenAiKeyChanged value ->
            match sourceConfigBlocked model "Changing OpenAI key" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None -> { model with openAiKey = value } |> refreshRuntimeSettings, Cmd.none
        | AnswerMaxOutputTokensChanged value ->
            match sourceConfigBlocked model "Changing max answer tokens" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    answerMaxOutputTokens = value }
                |> refreshRuntimeSettings,
                Cmd.none
        | AnswerReasoningEffortChanged value ->
            match sourceConfigBlocked model "Changing answer reasoning level" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    answerReasoningEffort = Settings.normalizeAnswerReasoningEffort value }
                |> refreshRuntimeSettings,
                Cmd.none
        | AnswerToolCallLoopLimitChanged value ->
            match sourceConfigBlocked model "Changing tool call loop limit" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    answerToolCallLoopLimit = value }
                |> refreshRuntimeSettings,
                Cmd.none
        | ModelRoleModelChanged(role, value) ->
            match sourceConfigBlocked model $"Changing {FsVoice.Ctx.ModelRole.storageName role} model" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let modelRoleOverrides = model.modelRoleOverrides |> Map.add role value

                { model with
                    modelRoleOverrides = modelRoleOverrides }
                |> refreshRuntimeSettings,
                Cmd.none
        | PlugInSettingChanged(key, value) ->
            match sourceConfigBlocked model $"Changing {key}" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    plugInSettings = model.plugInSettings |> Map.add key value }
                |> refreshRuntimeSettings,
                Cmd.none
        | RetrievalModeChanged mode ->
            match sourceConfigBlocked model "Changing retrieval mode" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None -> { model with retrievalMode = mode } |> refreshRuntimeSettings, Cmd.none
        | LogExpansionsToggled value ->
            match sourceConfigBlocked model "Changing retrieval logging" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let model = { model with logExpansions = value }
                saveSettings model
                postSources model
                model, Cmd.none
        | LogChunksToggled value ->
            match sourceConfigBlocked model "Changing chunk logging" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let model = { model with logChunks = value }
                saveSettings model
                postSources model
                model, Cmd.none
        | ActivityLogVerbosityChanged value ->
            let model =
                { model with
                    activityLogVerbosity = value
                    log =
                        $"Activity log set to {ActivityLog.displayName value}." :: model.log
                        |> List.truncate C.MAX_LOG }

            saveSettings model
            model, Cmd.none
        | UseLexicalFilterToggled value ->
            match sourceConfigBlocked model "Changing lexical filter" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let model = { model with useLexicalFilter = value }
                saveSettings model
                postSources model
                model, Cmd.none
        | ElaborateIndexKeywordsToggled value ->
            match sourceConfigBlocked model "Changing index elaboration" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let model =
                    { model with
                        elaborateIndexKeywords = value }

                saveSettings model
                postSources model
                model, Cmd.none
        | UseHybridPdfParsingToggled value ->
            match sourceConfigBlocked model "Changing PDF parser" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let parserName = if value then "Hybrid" else "Legacy"

                let model =
                    { model with
                        useHybridPdfParsing = value
                        log =
                            $"PDF parser set to {parserName}. Reprocess documents to rebuild indexes with this parser."
                            :: model.log
                            |> List.truncate C.MAX_LOG }

                saveSettings model
                postSources model
                model, Cmd.none
        | UseLayoutAnalysisToggled value ->
            match sourceConfigBlocked model "Changing layout analysis" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let state = if value then "enabled" else "disabled"

                let model =
                    { model with
                        useLayoutAnalysis = value
                        log =
                            $"Layout analysis {state}. Reprocess documents to rebuild Hybrid parser indexes with this setting."
                            :: model.log
                            |> List.truncate C.MAX_LOG }

                saveSettings model
                postSources model
                model, Cmd.none
        | PrebuiltDocumentsInstalled(Ok(docs, logs)) ->
            let docs = mergePrebuiltInstallResult model.pdfDocuments docs

            let model =
                { model with
                    pdfDocuments = docs
                    log = (logs @ model.log) |> List.truncate C.MAX_LOG }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            postSources model
            model, Cmd.none
        | PrebuiltDocumentsInstalled(Error ex) ->
            { model with
                log =
                    $"Prebuilt document installation failed: {ex.Message}" :: model.log
                    |> List.truncate C.MAX_LOG },
            Cmd.none
        | Settings_Show ->
            if model.isBusy then
                { model with
                    log =
                        "Opening settings is unavailable while another operation is running."
                        :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            else
                { model with currentPage = Settings }, Cmd.none
        | Settings_Close ->
            saveSettings model
            { model with currentPage = Main }, Cmd.none
        | Info_Show -> { model with currentPage = Info }, Cmd.none
        | Info_Close -> { model with currentPage = Main }, Cmd.none
        | OpenAppLink link -> model, Cmd.OfAsync.either openAppLink link AppLinkOpened EventError
        | AppLinkOpened(Ok()) -> model, Cmd.none
        | AppLinkOpened(Error ex) ->
            { model with
                log = $"Unable to open app link: {ex.Message}" :: model.log |> List.truncate C.MAX_LOG },
            Cmd.none
        | ToggleSecretVisibility ->
            match sourceConfigBlocked model "Changing secret visibility" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with
                    hideSecrets = not model.hideSecrets },
                Cmd.none
        | PickSources ->
            match documentMutationBlocked model "Adding sources" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with isBusy = true },
                Cmd.OfAsync.either pickAndImportSources model.pdfDocuments PickSourcesCompleted EventError
        | PickSourcesCompleted(Ok result) ->
            let msg =
                if List.isEmpty result.newDocuments && List.isEmpty result.logs then
                    "No new sources selected."
                elif List.isEmpty result.newDocuments then
                    "Source import complete."
                else
                    $"Processing {result.newDocuments.Length} new document(s)."

            let log = ([ msg ] @ result.logs @ model.log) |> List.truncate C.MAX_LOG

            let model =
                { model with
                    pdfDocuments = result.documents
                    log = log }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            if List.isEmpty result.newDocuments then
                let model = { model with isBusy = false }
                postSources model
                model, Cmd.none
            else
                let report msg = processingReport model msg
                let cts = new CancellationTokenSource()

                { model with
                    documentProcessingCancellation = Some cts },
                documentProcessingCommand report cts model result.newDocuments
        | PickSourcesCompleted(Error ex) ->
            { model with
                isBusy = false
                log = $"Source picker failed: {ex.Message}" :: model.log |> List.truncate C.MAX_LOG },
            Cmd.none
        | PdfProcessingCompleted(processedDocs, Ok(Completed results)) ->
            disposeDocumentProcessingCancellation model

            let pdfDocuments =
                model.pdfDocuments
                |> applyProcessingResults processedDocs results
                |> failProcessingDocuments processedDocs "Document processing completed without a result. Tap retry."

            let readyCount = results |> List.filter (fun r -> r.error.IsNone) |> List.length
            let failedCount = results.Length - readyCount

            let log =
                $"Document processing complete: {readyCount} ready, {failedCount} failed."
                :: model.log
                |> List.truncate C.MAX_LOG

            let model =
                { model with
                    pdfDocuments = pdfDocuments
                    isBusy = false
                    documentProcessingCancellation = None
                    log = log }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            postSources model
            model, Cmd.none
        | PdfProcessingCompleted(processedDocs, Ok(Canceled results)) ->
            disposeDocumentProcessingCancellation model

            let pdfDocuments =
                model.pdfDocuments
                |> applyProcessingResults processedDocs results
                |> failProcessingDocuments processedDocs "Document processing canceled. Tap retry."

            let readyCount = results |> List.filter (fun r -> r.error.IsNone) |> List.length

            let log =
                $"Document processing canceled; preserved {readyCount} completed document(s)."
                :: model.log
                |> List.truncate C.MAX_LOG

            let model =
                { model with
                    pdfDocuments = pdfDocuments
                    isBusy = false
                    documentProcessingCancellation = None
                    log = log }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            if readyCount > 0 then
                postSources model

            model, Cmd.none
        | PdfProcessingCompleted(processedDocs, Error ex) ->
            disposeDocumentProcessingCancellation model

            let canceled =
                match ex with
                | :? OperationCanceledException -> true
                | _ -> false

            let error =
                if canceled then
                    "Document processing canceled."
                else
                    $"Document processing failed: {ex.Message}"

            let pdfDocuments = failProcessingDocuments processedDocs error model.pdfDocuments

            let log = error :: model.log |> List.truncate C.MAX_LOG

            let model =
                { model with
                    pdfDocuments = pdfDocuments
                    isBusy = false
                    documentProcessingCancellation = None
                    log = log }

            { model with
                log = savePdfLibraryWithLog model.pdfDocuments model.log },
            Cmd.none
        | CancelPdfProcessing ->
            match model.documentProcessingCancellation with
            | None ->
                { model with
                    log =
                        "No document processing operation is running." :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            | Some cts ->
                cts.Cancel()

                { model with
                    log = "Canceling document processing..." :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
        | PdfSelectionChanged(id, selected) ->
            if not (canChangeSourceSelection model) then
                { model with
                    log =
                        "Changing selected sources is unavailable while realtime is connected or another operation is running."
                        :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            else
                let pdfDocuments =
                    model.pdfDocuments
                    |> List.map (fun doc ->
                        if doc.id = id && PdfDocuments.canSelect doc then
                            { doc with selected = selected }
                        else
                            doc)

                let model =
                    { model with
                        pdfDocuments = pdfDocuments }

                let model =
                    { model with
                        log = savePdfLibraryWithLog model.pdfDocuments model.log }

                postSources model
                model, Cmd.none
        | RetryPdfProcessing id ->
            match documentMutationBlocked model "Retrying document processing" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                let retry =
                    model.pdfDocuments
                    |> List.filter (fun doc -> doc.id = id && doc.status = Failed)

                if List.isEmpty retry then
                    model, Cmd.none
                else
                    let ids = retry |> List.map _.id |> Set.ofList

                    let model =
                        { model with
                            pdfDocuments = retryDocs ids model.pdfDocuments
                            isBusy = true }

                    let report msg = processingReport model msg
                    let cts = new CancellationTokenSource()

                    let model =
                        { model with
                            log = savePdfLibraryWithLog model.pdfDocuments model.log }

                    { model with
                        documentProcessingCancellation = Some cts },
                    documentProcessingCommand report cts model retry
        | DeletePdf id ->
            match documentMutationBlocked model "Deleting documents" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                match model.pdfDocuments |> List.tryFind (fun doc -> doc.id = id) with
                | None -> model, Cmd.none
                | Some doc when PdfDocuments.isBuiltIn doc ->
                    match Settings.addHiddenBuiltInSource doc.originalPath with
                    | Error error ->
                        { model with
                            log =
                                $"Built-in source was not hidden: {error}" :: model.log
                                |> List.truncate C.MAX_LOG },
                        Cmd.none
                    | Ok _ ->
                        let pdfDocuments = model.pdfDocuments |> List.filter (fun item -> item.id <> doc.id)

                        let log =
                            $"Hid built-in source until restore: {doc.displayName}." :: model.log
                            |> List.truncate C.MAX_LOG

                        let model =
                            { model with
                                pdfDocuments = pdfDocuments
                                log = log }

                        let model =
                            { model with
                                log = savePdfLibraryWithLog model.pdfDocuments model.log }

                        postSources model
                        model, Cmd.none
                | Some doc ->
                    { model with isBusy = true },
                    Cmd.OfAsync.either deleteDocumentAndIndexes doc DeletePdfCompleted EventError
        | DeletePdfCompleted(Ok result) ->
            let pdfDocuments =
                model.pdfDocuments |> List.filter (fun doc -> doc.id <> result.id)

            let fileMsg =
                if result.removedFile then
                    $"Deleted document: {result.displayName}."
                else
                    $"Removed document library entry: {result.displayName}."

            let indexMsg =
                if result.removedIndexCount = 0 then
                    "No persisted FsColbert indexes needed removal."
                else
                    $"Removed {result.removedIndexCount} persisted FsColbert index file(s)."

            let log =
                [ yield fileMsg; yield indexMsg; yield! result.indexErrors ] @ model.log
                |> List.truncate C.MAX_LOG

            let model =
                { model with
                    pdfDocuments = pdfDocuments
                    isBusy = false
                    log = log }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            postSources model
            model, Cmd.none
        | DeletePdfCompleted(Error ex) ->
            { model with
                isBusy = false
                log = $"Document delete failed: {ex.Message}" :: model.log |> List.truncate C.MAX_LOG },
            Cmd.none
        | RestoreBuiltInIndexes ->
            match documentMutationBlocked model "Restoring built-in indexes" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                { model with isBusy = true },
                Cmd.OfAsync.either restoreBuiltInIndexes model.pdfDocuments RestoreBuiltInIndexesCompleted EventError
        | RestoreBuiltInIndexesCompleted(Ok(docs, logs, hiddenCount)) ->
            let restoreMsg =
                if hiddenCount = 0 then
                    "No hidden built-in indexes needed restore."
                else
                    $"Restored {hiddenCount} hidden built-in index(es)."

            let model =
                { model with
                    pdfDocuments = docs
                    isBusy = false
                    log = ([ restoreMsg ] @ logs @ model.log) |> List.truncate C.MAX_LOG }

            let model =
                { model with
                    log = savePdfLibraryWithLog model.pdfDocuments model.log }

            postSources model
            model, Cmd.none
        | RestoreBuiltInIndexesCompleted(Error ex) ->
            { model with
                isBusy = false
                log =
                    $"Built-in index restore failed: {ex.Message}" :: model.log
                    |> List.truncate C.MAX_LOG },
            Cmd.none
        | PreviewIndex id ->
            match model.pdfDocuments |> List.tryFind (fun doc -> doc.id = id) with
            | None ->
                { model with
                    log =
                        "Document source was not found for index preview." :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            | Some doc when not (PdfDocuments.isReady doc) ->
                { model with
                    log =
                        $"Index preview is only available for ready sources: {doc.displayName}."
                        :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            | Some _ ->
                { model with
                    currentPage = IndexPreview id
                    indexPreview = Some(PreviewLoading id) },
                Cmd.OfAsync.either
                    (loadIndexPreviewForDocument model)
                    id
                    (fun result -> IndexPreviewLoaded(id, result))
                    EventError
        | RefreshIndexPreview ->
            match model.currentPage with
            | IndexPreview id ->
                { model with
                    indexPreview = Some(PreviewLoading id) },
                Cmd.OfAsync.either
                    (loadIndexPreviewForDocument model)
                    id
                    (fun result -> IndexPreviewLoaded(id, result))
                    EventError
            | Main
            | Terms
            | Info
            | Settings -> model, Cmd.none
        | IndexPreviewBack ->
            { model with
                currentPage = Main
                indexPreview = None },
            Cmd.none
        | IndexPreviewLoaded(id, Ok preview) ->
            match model.currentPage with
            | IndexPreview currentId when currentId = id ->
                { model with
                    indexPreview = Some(PreviewReady preview)
                    log =
                        $"Loaded index preview for {preview.source.DisplayName}: {preview.sampledCount}/{preview.totalChunks} chunk(s)."
                        :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
            | Main
            | Terms
            | Info
            | Settings
            | IndexPreview _ -> model, Cmd.none
        | IndexPreviewLoaded(id, Error ex) ->
            match model.currentPage with
            | IndexPreview currentId when currentId = id ->
                { model with
                    indexPreview = Some(PreviewFailed(id, ex.Message))
                    log = $"Index preview failed: {ex.Message}" :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | Main
            | Terms
            | Info
            | Settings
            | IndexPreview _ -> model, Cmd.none
        | ApplySources ->
            match sourceConfigBlocked model "Applying sources" with
            | Some msg ->
                { model with
                    log = msg :: model.log |> List.truncate C.MAX_LOG },
                Cmd.none
            | None ->
                saveSettings model
                postSources model

                let count = sources model |> List.length

                { model with
                    log =
                        $"Configured {count} document source(s)." :: model.log
                        |> List.truncate C.MAX_LOG },
                Cmd.none
        | StartStop ->
            if model.isBusy then
                model, Cmd.none
            else
                match model.bundle, model.pendingConnectionId with
                | None, Some _ -> model, Cmd.none
                | None, None ->
                    match Text.notEmpty model.openAiKey with
                    | None -> showNotification "Set your OpenAI API key in Settings before connecting." model
                    | Some _ when not model.openAiDisclosureSuppressed ->
                        { model with
                            currentPage = Main
                            openAiDisclosure = Some ConnectAfterAcknowledgement
                            openAiDisclosureDoNotShowAgain = false
                            log =
                                "Review the OpenAI data notice before starting realtime voice QA." :: model.log
                                |> List.truncate C.MAX_LOG },
                        Cmd.none
                    | Some _ -> startRealtimeFlow true model
                | Some bundle, _ -> { model with isBusy = true }, stopBundleCommand bundle
        | StartCompleted(connectionId, Ok bundle) ->
            match model.pendingConnectionId with
            | Some pendingId when String.Equals(pendingId, connectionId, StringComparison.OrdinalIgnoreCase) ->
                let wasDisconnected = model.disconnectedConnectionIds |> Set.contains connectionId

                let actualState = bundle.connection.WebRtcClient.State

                if wasDisconnected then
                    { model with
                        bundle = None
                        pendingConnectionId = Some connectionId
                        sessionState = RTOpenAI.WebRTC.State.Disconnected
                        isBusy = true
                        log =
                            "Realtime connection closed before session could start." :: model.log
                            |> List.truncate C.MAX_LOG }
                    |> forgetRealtimeDisconnected connectionId,
                    stopBundleCommand bundle
                else
                    let activeState =
                        if actualState = RTOpenAI.WebRTC.State.Disconnected then
                            RTOpenAI.WebRTC.State.Connecting
                        else
                            actualState

                    { model with
                        bundle = Some bundle
                        pendingConnectionId = None
                        sessionState = activeState
                        isBusy = false
                        log = "Realtime flow started." :: model.log }
                    |> forgetRealtimeDisconnected connectionId,
                    Cmd.none
            | _ -> forgetRealtimeDisconnected connectionId model, stopBundleCommand bundle
        | StartCompleted(connectionId, Error ex) ->
            match model.pendingConnectionId with
            | Some pendingId when String.Equals(pendingId, connectionId, StringComparison.OrdinalIgnoreCase) ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = $"Start failed: {ex.Message}" :: model.log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none
            | _ -> forgetRealtimeDisconnected connectionId model, Cmd.none
        | StopCompleted(connectionId, Ok()) ->
            match model.bundle, model.pendingConnectionId with
            | Some bundle, _ when String.Equals(bundle.id, connectionId, StringComparison.OrdinalIgnoreCase) ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = "Realtime flow stopped." :: model.log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none
            | _, Some pendingId when String.Equals(pendingId, connectionId, StringComparison.OrdinalIgnoreCase) ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = "Realtime cleanup completed." :: model.log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none
            | _ -> forgetRealtimeDisconnected connectionId model, Cmd.none
        | StopCompleted(connectionId, Error ex) ->
            match model.bundle, model.pendingConnectionId with
            | Some bundle, _ when String.Equals(bundle.id, connectionId, StringComparison.OrdinalIgnoreCase) ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = $"Stop cleanup reported an issue: {ex.Message}" :: model.log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none
            | _, Some pendingId when String.Equals(pendingId, connectionId, StringComparison.OrdinalIgnoreCase) ->
                { model with
                    bundle = None
                    pendingConnectionId = None
                    sessionState = RTOpenAI.WebRTC.State.Disconnected
                    isBusy = false
                    log = $"Stop cleanup reported an issue: {ex.Message}" :: model.log }
                |> forgetRealtimeDisconnected connectionId,
                Cmd.none
            | _ -> forgetRealtimeDisconnected connectionId model, Cmd.none
        | WebRTC_StateChanged(connectionId, state) -> handleRealtimeStateChanged connectionId state model
        | RealtimeConnectFailed(connectionId, error) -> cleanupFailedRealtimeConnection connectionId error model
        | Log_Append text ->
            { model with
                log = text :: model.log |> List.truncate C.MAX_LOG },
            Cmd.none
        | Log_Clear -> { model with log = [] }, Cmd.none
        | LogFont_Increase ->
            { model with
                logFontSize = clampLogFontSize (model.logFontSize + logFontStep) },
            Cmd.none
        | LogFont_Decrease ->
            { model with
                logFontSize = clampLogFontSize (model.logFontSize - logFontStep) },
            Cmd.none
        | NotificationExpired id ->
            match model.notification with
            | Some notification when notification.id = id -> { model with notification = None }, Cmd.none
            | Some _
            | None -> model, Cmd.none
        | ThemeChanged appTheme -> { model with appTheme = appTheme }, Cmd.none
        | EventError ex ->
            disposeDocumentProcessingCancellation model

            { model with
                isBusy = false
                documentProcessingCancellation = None
                log = ex.Message :: model.log |> List.truncate C.MAX_LOG },
            Cmd.none

    let subscribeMailbox model =
        let background dispatch =
            let cts = new CancellationTokenSource()

            let comp =
                async {
                    let reader = model.mailbox.Reader.ReadAllAsync(cts.Token) |> AsyncSeq.iter dispatch

                    match! Async.Catch reader with
                    | Choice1Of2 _ -> ()
                    | Choice2Of2(:? OperationCanceledException) -> ()
                    | Choice2Of2 ex -> dispatch (EventError ex)
                }

            Async.Start(comp, cts.Token)

            { new IDisposable with
                member _.Dispose() =
                    cts.Cancel()
                    cts.Dispose() }

        [ [ "mailbox" ], background ]

    let internal statusText model =
        match model.sessionState with
        | RTOpenAI.WebRTC.State.Connected -> "Connected"
        | RTOpenAI.WebRTC.State.Connecting -> "Connecting"
        | _ -> "Disconnected"

    let internal statusColor model =
        match model.sessionState with
        | RTOpenAI.WebRTC.State.Connected -> Colors.SeaGreen
        | RTOpenAI.WebRTC.State.Connecting -> Colors.DarkOrange
        | _ -> Colors.DimGray
