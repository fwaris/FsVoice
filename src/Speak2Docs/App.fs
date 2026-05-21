namespace Speak2Docs

open Fabulous
open Fabulous.Maui
open Microsoft.Maui.ApplicationModel
open type Fabulous.Maui.View

module App =
    let private page model =
        match model.currentPage with
        | Terms -> Views.TermsView.contentPage model
        | Main -> Views.MainView.contentPage model
        | Settings -> Views.SettingsView.contentPage model
        | Info -> Views.InfoView.contentPage model
        | IndexPreview _ -> Views.IndexPreviewView.contentPage model

    let program =
        Program.statefulWithCmd Update.init Update.update
        |> Program.withSubscription Update.subscribeMailbox

    let view () =
        Component("Speak2Docs") {
            let! model = Context.Mvu(program)

            (Application() { Window(page model) })
                .userAppTheme(AppTheme.Unspecified)
                .onRequestedThemeChanged (ThemeChanged)
        }
