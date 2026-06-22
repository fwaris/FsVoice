namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module InfoView =
    let private header =
        Grid([ Dimension.Absolute 42.; Dimension.Star ], [ Dimension.Absolute 44. ]) {
            (ViewControls.compactIconButton Icons.back Info_Close).gridColumn(0).gridRow (0)

            Label("Info").font(size = 16., attributes = FontAttributes.Bold).centerVertical().gridColumn(1).gridRow (0)
        }

    let private helpRow appTheme icon title detail =
        Grid([ Dimension.Absolute 42.; Dimension.Star ], [ Dimension.Auto; Dimension.Auto ]) {
            Label(icon)
                .font(size = 24., fontFamily = C.FONT_SYMBOLS)
                .textColor(Colors.Magenta)
                .centerHorizontal()
                .centerVertical()
                .gridRowSpan(2)
                .gridColumn (0)

            Label(title)
                .font(size = 13., attributes = FontAttributes.Bold)
                .lineBreakMode(LineBreakMode.WordWrap)
                .gridColumn(1)
                .gridRow (0)

            Label(detail)
                .font(size = 12.)
                .textColor(Theme.secondaryTextColor appTheme)
                .lineBreakMode(LineBreakMode.WordWrap)
                .gridColumn(1)
                .gridRow (1)
        }

    let private sectionTitle title =
        Label(title).font(size = 15., attributes = FontAttributes.Bold).lineBreakMode (LineBreakMode.WordWrap)

    let private mainControls appTheme =
        Border(
            (VStack(spacing = 12.) {
                sectionTitle "Main Controls"

                helpRow
                    appTheme
                    Icons.settings
                    "Settings"
                    "Edit the API key, model choices, retrieval behavior, audio, and PDF parsing options."

                helpRow
                    appTheme
                    Icons.restore
                    "Restore"
                    "Show bundled indexes again after they were hidden with delete."

                helpRow appTheme Icons.info "Info" "Open this page."
                helpRow appTheme Icons.mic "Connect" "Connect or disconnect the realtime voice QA session."

                helpRow
                    appTheme
                    Icons.libraryBooks
                    "Sources"
                    "Open the full Library page to select, preview, search, or delete sources."

                helpRow appTheme Icons.add "Add" "Import sources and show progress while the new items are indexed."
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private sourceControls appTheme =
        Border(
            (VStack(spacing = 12.) {
                sectionTitle "Source Controls"

                helpRow
                    appTheme
                    Icons.libraryBooks
                    "Library"
                    "Manage the full source list when the compact main-page Sources flow is not enough."

                helpRow
                    appTheme
                    Icons.checkbox
                    "Selection"
                    "Include or exclude ready sources from the next answer session."

                helpRow appTheme Icons.preview "Preview" "Tap a ready source to open a sample of its index and chunks."
                helpRow appTheme Icons.search "Search" "Filter the Library list by source name, type, or status."

                helpRow
                    appTheme
                    Icons.clear
                    "Clear"
                    "Clear all selected sources from the Library without deleting them."

                helpRow
                    appTheme
                    Icons.delete
                    "Delete"
                    "Remove a user source. For bundled indexes, hide it until Restore is used."

                helpRow appTheme Icons.play "Retry" "Process a failed source again."
                helpRow appTheme Icons.deleteForever "Cancel" "Cancel active source processing."
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private activityControls appTheme =
        Border(
            (VStack(spacing = 12.) {
                sectionTitle "Activity Controls"
                helpRow appTheme Icons.remove "Smaller Text" "Decrease activity log text size."
                helpRow appTheme Icons.add "Larger Text" "Increase activity log text size."
                helpRow appTheme Icons.clear "Clear" "Clear the activity log shown on the main page."
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let private legalControls appTheme =
        Border(
            (VStack(spacing = 12.) {
                sectionTitle "Legal"

                helpRow
                    appTheme
                    Icons.info
                    "Terms"
                    "Open the Terms of Use, Privacy Policy, and Third-Party Notices from Settings."

                helpRow appTheme Icons.checkbox "Agreement" "The app asks for Terms acceptance before first use."
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Theme.borderColor appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let contentPage (model: Model) =
        ContentPage(
            ScrollView(
                (VStack(spacing = 12.) {
                    header
                    mainControls model.appTheme
                    sourceControls model.appTheme
                    activityControls model.appTheme
                    legalControls model.appTheme
                })
                    .padding (18.)
            )
        )
            .background(Theme.pageBackgroundColor model.appTheme)
            .title ("Info")
