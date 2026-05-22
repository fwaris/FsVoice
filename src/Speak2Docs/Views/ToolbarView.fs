namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module ToolbarView =
    let private isRealtimeActive model =
        model.bundle.IsSome
        || model.pendingConnectionId.IsSome
        || model.sessionState <> RTOpenAI.WebRTC.State.Disconnected

    let private connectionColor model =
        match model.sessionState with
        | RTOpenAI.WebRTC.State.Connecting -> Colors.Orange
        | RTOpenAI.WebRTC.State.Connected -> Colors.Magenta
        | _ -> Theme.secondaryTextColor model.appTheme

    let private connectionIcon model =
        match model.sessionState with
        | RTOpenAI.WebRTC.State.Connected -> Icons.mic
        | _ -> Icons.micOff

    let main model =
        (Grid(
            [ Dimension.Absolute 52.
              Dimension.Absolute 52.
              Dimension.Absolute 52.
              Dimension.Star
              Dimension.Absolute 52. ],
            [ Dimension.Absolute 50. ]
        ) {
            (ViewControls.iconButton Icons.settings Settings_Show).alignStartHorizontal().gridColumn (0)

            (ViewControls.iconButton Icons.restore RestoreBuiltInIndexes)
                .isEnabled(not model.isBusy && not (isRealtimeActive model))
                .alignStartHorizontal()
                .gridColumn (1)

            (ViewControls.iconButton Icons.info Info_Show).alignStartHorizontal().gridColumn (2)

            Label(C.APP_NAME)
                .font(size = 20., attributes = FontAttributes.Bold)
                .centerHorizontal()
                .centerVertical()
                .gridColumn (3)

            (ViewControls.iconButton (connectionIcon model) StartStop)
                .isEnabled(not model.isBusy)
                .textColor(connectionColor model)
                .alignEndHorizontal()
                .gridColumn (4)
        })

    let settings =
        (Grid([ Dimension.Absolute 52.; Dimension.Star; Dimension.Absolute 52. ], [ Dimension.Absolute 50. ]) {
            (ViewControls.iconButton Icons.back Settings_Close).alignStartHorizontal().gridColumn (0)

            Label("Settings")
                .font(size = 20., attributes = FontAttributes.Bold)
                .centerHorizontal()
                .centerVertical()
                .gridColumn (1)
        })
