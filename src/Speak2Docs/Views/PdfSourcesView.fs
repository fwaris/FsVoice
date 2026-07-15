namespace Speak2Docs.Views

open System
open Fabulous.Maui
open Speak2Docs
open Speak2Docs.WorkFlow
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open Microsoft.Maui.Layouts
open type Fabulous.Maui.View

module PdfSourcesView =
    type private DocumentCounts =
        { total: int
          selected: int
          ready: int
          processing: int
          queued: int
          failed: int }

    type private PdfSourceRow =
        { document: PdfDocumentSource
          appTheme: Microsoft.Maui.ApplicationModel.AppTheme
          processingActive: bool
          canMutateDocuments: bool
          canChangeSourceSelection: bool
          showSelection: bool
          showActions: bool }

    let private isRealtimeActive model =
        model.bundle.IsSome
        || model.pendingConnectionId.IsSome
        || model.sessionState <> RealtimeDisconnected

    let private canMutateDocuments model =
        not model.isBusy && not (isRealtimeActive model)

    let private canChangeSourceSelection model =
        not model.isBusy && not (isRealtimeActive model)

    let private isInProgress (doc: PdfDocumentSource) =
        match doc.status with
        | Queued
        | Processing -> true
        | Ready
        | Failed -> false

    let private statusColor appTheme (doc: PdfDocumentSource) =
        match doc.status with
        | Ready -> Theme.readyColor appTheme
        | Processing -> Theme.processingColor appTheme
        | Queued -> Theme.mutedTextColor appTheme
        | Failed -> Theme.failedColor appTheme

    let private countWhere (predicate: PdfDocumentSource -> bool) (docs: PdfDocumentSource list) =
        docs |> List.filter predicate |> List.length

    let private counts (docs: PdfDocumentSource list) : DocumentCounts =
        { total = List.length docs
          selected = docs |> countWhere (fun doc -> doc.selected && PdfDocuments.canSelect doc)
          ready = docs |> countWhere (fun doc -> doc.status = Ready)
          processing = docs |> countWhere (fun doc -> doc.status = Processing)
          queued = docs |> countWhere (fun doc -> doc.status = Queued)
          failed = docs |> countWhere (fun doc -> doc.status = Failed) }

    let private matchesSearch search (doc: PdfDocumentSource) =
        let query = (defaultArg (Option.ofObj search) "").Trim()

        if String.IsNullOrWhiteSpace query then
            true
        else
            doc.displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (PdfDocuments.kindLabel doc).Contains(query, StringComparison.OrdinalIgnoreCase)
            || (PdfDocuments.statusText doc).Contains(query, StringComparison.OrdinalIgnoreCase)

    let private searchedDocuments search docs =
        docs |> List.filter (matchesSearch search)

    let private panel appTheme content =
        Border(content)
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private addPdfButton model =
        Button(Icons.add, PickSources)
            .font(size = 24., fontFamily = C.FONT_SYMBOLS)
            .background(Colors.Magenta)
            .textColor(Colors.White)
            .cornerRadius(20)
            .width(40.)
            .height(40.)
            .padding(0.)
            .isEnabled(canMutateDocuments model)
            .centerVertical ()

    let private textActionButton (text: string) (msg: Msg) enabled appTheme =
        let color =
            if enabled then
                Colors.Magenta
            else
                Theme.mutedTextColor appTheme

        Button(text, msg)
            .font(size = 12., attributes = FontAttributes.Bold)
            .background(Colors.Transparent)
            .textColor(color)
            .cornerRadius(8)
            .height(36.)
            .padding(4.)
            .isEnabled (enabled)

    let private emptyView appTheme message =
        Border(
            Label(message)
                .font(size = 13.)
                .textColor(Theme.secondaryTextColor appTheme)
                .centerHorizontal()
                .centerVertical()
                .padding (12.)
        )
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private statusText (doc: PdfDocumentSource) =
        match doc.status with
        | Ready -> $"{doc.chunkCount} chunk(s) - {PdfDocuments.kindLabel doc}"
        | Queued -> "Queued"
        | Processing -> "Processing"
        | Failed -> defaultArg doc.error "Failed"

    let private row (item: PdfSourceRow) =
        let doc = item.document

        let deleteIcon =
            if item.processingActive then
                Icons.deleteForever
            else
                Icons.delete

        let deleteMessage =
            if item.processingActive then
                CancelPdfProcessing
            else
                DeletePdf doc.id

        let columns =
            if item.showSelection && item.showActions then
                [ Dimension.Absolute 36.
                  Dimension.Star
                  Dimension.Absolute 38.
                  Dimension.Absolute 38. ]
            elif item.showSelection then
                [ Dimension.Absolute 36.; Dimension.Star ]
            elif item.showActions then
                [ Dimension.Star; Dimension.Absolute 38.; Dimension.Absolute 38. ]
            else
                [ Dimension.Star ]

        Border(
            (Grid(columns, [ Dimension.Absolute 24.; Dimension.Absolute 22. ]) {
                let titleColumn = if item.showSelection then 1 else 0

                if item.showSelection then
                    CheckBox(doc.selected, fun selected -> PdfSelectionChanged(doc.id, selected))
                        .isEnabled(item.canChangeSourceSelection && PdfDocuments.canSelect doc)
                        .centerVertical()
                        .gridRowSpan(2)
                        .gridColumn (0)

                Label(doc.displayName)
                    .font(size = 13., attributes = FontAttributes.Bold)
                    .textColor(Theme.textColor item.appTheme)
                    .lineBreakMode(LineBreakMode.MiddleTruncation)
                    .centerVertical()
                    .gridColumn(titleColumn)
                    .gridRow (0)

                Label(statusText doc)
                    .font(size = 11.)
                    .textColor(statusColor item.appTheme doc)
                    .lineBreakMode(LineBreakMode.TailTruncation)
                    .centerVertical()
                    .gridColumn(titleColumn)
                    .gridRow (1)

                if item.showActions then
                    let previewColumn = if item.showSelection then 2 else 1
                    let deleteColumn = if item.showSelection then 3 else 2

                    if doc.status = Failed then
                        (ViewControls.compactIconButton Icons.play (RetryPdfProcessing doc.id))
                            .isEnabled(item.canMutateDocuments)
                            .gridColumn(previewColumn)
                            .gridRowSpan (2)

                    if doc.status = Ready then
                        (ViewControls.compactIconButton Icons.preview (PreviewIndex doc.id))
                            .isEnabled(true)
                            .gridColumn(previewColumn)
                            .gridRowSpan (2)

                    (ViewControls.compactDangerIconButton deleteIcon deleteMessage)
                        .isEnabled(item.processingActive || item.canMutateDocuments)
                        .gridColumn(deleteColumn)
                        .gridRowSpan (2)
            })
                .padding (7.)
        )
            .stroke(SolidColorBrush(Theme.borderColor item.appTheme))
            .strokeThickness(1.)
            .strokeShape(RoundRectangle(CornerRadius(8.)))
            .margin (0., 0., 0., 5.)

    let private rowItems (model: Model) (docs: PdfDocumentSource list) showSelection showActions =
        let processingActive = model.documentProcessingCancellation.IsSome

        docs
        |> List.map (fun doc ->
            { document = doc
              appTheme = model.appTheme
              processingActive = processingActive
              canMutateDocuments = canMutateDocuments model
              canChangeSourceSelection = canChangeSourceSelection model
              showSelection = showSelection
              showActions = showActions })

    let private searchBox (model: Model) =
        SearchBar(model.documentSearch, DocumentSearchChanged, DocumentSearchChanged model.documentSearch)
            .placeholder("Search documents")
            .font(size = 12.)
            .height(38.)
            .cancelButtonColor (Colors.Magenta)

    let private contains (needle: string) (value: string) =
        value.Contains(needle, StringComparison.OrdinalIgnoreCase)

    let private processingPhase (model: Model) (currentDoc: PdfDocumentSource option) =
        match currentDoc with
        | None -> "Preparing"
        | Some doc ->
            let recent =
                model.log
                |> List.tryFind (fun text -> text.Contains(doc.displayName, StringComparison.OrdinalIgnoreCase))

            match recent with
            | Some text when contains "indexed" text || contains "indexing" text -> "Indexing"
            | Some text when contains "ocr" text || contains "layout" text || contains "parser" text -> "Reading / OCR"
            | Some text when contains "read" text || contains "reading" text -> "Reading"
            | Some text when contains "finished" text -> "Finishing"
            | Some _
            | None -> "Processing"

    let private selectedDocumentChip appTheme (doc: PdfDocumentSource) =
        (Grid([ Dimension.Star ], [ Dimension.Star ]) {
            Border(
                (Grid([ Dimension.Star; Dimension.Absolute 52. ], [ Dimension.Absolute 20.; Dimension.Absolute 18. ]) {
                    Label(doc.displayName)
                        .font(size = 11., attributes = FontAttributes.Bold)
                        .textColor(Theme.textColor appTheme)
                        .lineBreakMode(LineBreakMode.MiddleTruncation)
                        .gridColumn(0)
                        .gridRow (0)

                    Label($"{doc.chunkCount}")
                        .font(size = 10., attributes = FontAttributes.Bold)
                        .textColor(Theme.readyColor appTheme)
                        .horizontalTextAlignment(TextAlignment.End)
                        .gridColumn(1)
                        .gridRow (0)

                    Label(PdfDocuments.kindLabel doc)
                        .font(size = 10.)
                        .textColor(Theme.mutedTextColor appTheme)
                        .lineBreakMode(LineBreakMode.TailTruncation)
                        .gridColumnSpan(2)
                        .gridRow (1)
                })
                    .padding (7., 5.)
            )
                .stroke(SolidColorBrush(Theme.borderColor appTheme))
                .strokeThickness(1.)
                .strokeShape(RoundRectangle(CornerRadius(8.)))
                .gridRow (0)

            Button("", PreviewIndex doc.id)
                .background(Colors.Transparent)
                .textColor(Colors.Transparent)
                .padding(0.)
                .gridRow (0)
        })
            .width(158.)
            .height(46.)
            .margin (4., 2., 6., 6.)

    let private selectedFlowView (model: Model) =
        let selected = PdfDocuments.selectedReady model.pdfDocuments

        panel
            model.appTheme
            ((Grid([ Dimension.Star ], [ Dimension.Absolute 44.; Dimension.Star ]) {
                (Grid([ Dimension.Star; Dimension.Absolute 46. ], [ Dimension.Absolute 42. ]) {
                    Button($"Sources {selected.Length}", Library_Show)
                        .font(size = 15., attributes = FontAttributes.Bold)
                        .background(Colors.Magenta)
                        .textColor(Colors.White)
                        .cornerRadius(20)
                        .padding(14., 0.)
                        .height(40.)
                        .alignStartHorizontal()
                        .centerVertical()
                        .gridColumn(0)
                        .gridRow (0)

                    (addPdfButton model).gridColumn(1).gridRow (0)
                })
                    .gridRow (0)

                if List.isEmpty selected then
                    (emptyView model.appTheme "No sources selected").gridRow (1)
                else
                    (ScrollView(
                        (FlexLayout(FlexWrap.Wrap) {
                            for doc in selected do
                                selectedDocumentChip model.appTheme doc
                        })
                            .direction(FlexDirection.Row)
                            .alignItems(FlexAlignItems.Start)
                            .alignContent (FlexAlignContent.Start)
                    ))
                        .verticalScrollBarVisibility(ScrollBarVisibility.Never)
                        .gridRow (1)
            })
                .padding (10.))

    let private processingPopupView (model: Model) =
        let c = counts model.pdfDocuments
        let inProgress = model.pdfDocuments |> List.filter isInProgress
        let currentDoc = inProgress |> List.tryHead
        let activeCount = c.processing + c.queued

        let progress =
            if c.total <= 0 then
                0.
            else
                (float (c.ready + c.failed) / float c.total) |> max 0.08 |> min 1.

        let phase = processingPhase model currentDoc

        let currentText =
            currentDoc
            |> Option.map (fun doc -> $"{phase}: {doc.displayName}")
            |> Option.defaultValue phase

        panel
            model.appTheme
            ((Grid(
                [ Dimension.Star ],
                [ Dimension.Absolute 36.
                  Dimension.Absolute 22.
                  Dimension.Absolute 24.
                  Dimension.Star ]
            ) {
                (Grid([ Dimension.Star; Dimension.Absolute 44. ], [ Dimension.Absolute 34. ]) {
                    Label($"Adding {activeCount} document(s)")
                        .font(size = 15., attributes = FontAttributes.Bold)
                        .textColor(Theme.textColor model.appTheme)
                        .centerVertical()
                        .gridColumn (0)

                    (ViewControls.compactDangerIconButton Icons.stop CancelPdfProcessing)
                        .isEnabled(model.documentProcessingCancellation.IsSome)
                        .gridColumn (1)
                })
                    .gridRow (0)

                ProgressBar(progress).progressColor(Colors.Magenta).height(8.).gridRow (1)

                Label(currentText)
                    .font(size = 12., attributes = FontAttributes.Bold)
                    .textColor(Theme.secondaryTextColor model.appTheme)
                    .lineBreakMode(LineBreakMode.MiddleTruncation)
                    .gridRow (2)

                Label($"{c.ready} ready - {c.queued} queued - {c.processing} active - {c.failed} failed")
                    .font(size = 11.)
                    .textColor(Theme.mutedTextColor model.appTheme)
                    .lineBreakMode(LineBreakMode.TailTruncation)
                    .gridRow (3)
            })
                .padding (12.))

    let private clearSelectionsButton model c =
        textActionButton
            "Clear all selections"
            ClearDocumentSelection
            (canChangeSourceSelection model && c.selected > 0)
            model.appTheme

    let private libraryHeader (model: Model) (c: DocumentCounts) =
        Grid(
            [ Dimension.Absolute 42.; Dimension.Star; Dimension.Absolute 46. ],
            [ Dimension.Absolute 26.; Dimension.Absolute 18. ]
        ) {
            (ViewControls.compactIconButton Icons.back Library_Close).gridColumn(0).gridRowSpan (2)

            Label("Library")
                .font(size = 17., attributes = FontAttributes.Bold)
                .textColor(Theme.textColor model.appTheme)
                .centerVertical()
                .gridColumn(1)
                .gridRow (0)

            Label($"{c.total} docs - {c.selected} selected - {c.ready} ready")
                .font(size = 11.)
                .textColor(Theme.mutedTextColor model.appTheme)
                .lineBreakMode(LineBreakMode.TailTruncation)
                .gridColumn(1)
                .gridRow (1)

            (addPdfButton model).gridColumn(2).gridRowSpan (2)
        }

    let private libraryView (model: Model) =
        let c = counts model.pdfDocuments
        let docs = searchedDocuments model.documentSearch model.pdfDocuments
        let rows = rowItems model docs true true

        panel
            model.appTheme
            ((Grid(
                [ Dimension.Star ],
                [ Dimension.Absolute 50.
                  Dimension.Absolute 42.
                  Dimension.Absolute 38.
                  Dimension.Star ]
            ) {
                (libraryHeader model c).gridRow (0)
                (searchBox model).gridRow (1)
                (clearSelectionsButton model c).gridRow (2)

                if List.isEmpty model.pdfDocuments then
                    (emptyView model.appTheme "No documents added").gridRow (3)
                elif List.isEmpty docs then
                    (emptyView model.appTheme "No matching documents").gridRow (3)
                else
                    (CollectionView (rows) (row)).gridRow (3)
            })
                .padding (10.))

    let view (model: Model) =
        if model.documentProcessingCancellation.IsSome then
            processingPopupView model
        else
            selectedFlowView model

    let libraryPage (model: Model) =
        ContentPage((Grid([ Dimension.Star ], [ Dimension.Star ]) { (libraryView model).gridRow (0) }).padding (18.))
            .background(Theme.pageBackgroundColor model.appTheme)
            .title ("Library")
