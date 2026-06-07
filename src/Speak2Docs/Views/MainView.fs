namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module MainView =
    let private notificationView (notification: TransientNotification) =
        Border(
            Label(notification.message)
                .font(size = 13., attributes = FontAttributes.Bold)
                .textColor(Colors.White)
                .lineBreakMode(LineBreakMode.WordWrap)
                .centerVertical()
                .padding (12., 8.)
        )
            .background(Colors.DarkMagenta)
            .stroke(SolidColorBrush(Colors.DarkMagenta))
            .strokeThickness(1.)
            .strokeShape(RoundRectangle(CornerRadius(8.)))
            .margin (0., 2., 0., 6.)

    let private aiDisclaimerView model =
        Label("Uses AI which can make mistakes")
            .font(size = 12.)
            .textColor(Theme.secondaryTextColor model.appTheme)
            .horizontalTextAlignment(TextAlignment.Center)
            .centerHorizontal()
            .margin (0., 8., 0., 0.)

    let private disclosureLink text link =
        Button(text, OpenAppLink link)
            .font(size = 14., attributes = FontAttributes.Bold)
            .background(Colors.Transparent)
            .textColor(Colors.DarkMagenta)
            .height (42.)

    let private disclosureText appTheme text =
        Label(text).font(size = 13.).textColor(Theme.textColor appTheme).lineBreakMode (LineBreakMode.WordWrap)

    let private disclosureMutedText appTheme text =
        Label(text).font(size = 12.).textColor(Theme.secondaryTextColor appTheme).lineBreakMode (LineBreakMode.WordWrap)

    let private disclosurePopupTitle mode =
        match mode with
        | ConnectAfterAcknowledgement -> "Before Connecting"
        | ReviewOnly -> "OpenAI Data Notice"

    let private clamp upper lower value = min upper (max lower value)

    let private disclosurePopupSize model =
        match model.mainPageSize with
        | Some size when size.width > 0. && size.height > 0. ->
            let availableWidth = max 0. (size.width - 48.)
            let availableHeight = max 0. (size.height - 72.)

            clamp availableWidth 280. 360., clamp availableHeight 260. 520.
        | _ -> 330., 420.

    let private openAiDisclosureOverlay model mode =
        let popupWidth, popupHeight = disclosurePopupSize model

        (Grid([ Dimension.Star ], [ Dimension.Star ]) {
            Border(
                (Grid([ Dimension.Star ], [ Dimension.Auto; Dimension.Star; Dimension.Absolute 46. ]) {
                    Label(disclosurePopupTitle mode)
                        .font(size = 17., attributes = FontAttributes.Bold)
                        .textColor(Theme.textColor model.appTheme)
                        .lineBreakMode(LineBreakMode.WordWrap)
                        .horizontalTextAlignment(TextAlignment.Center)
                        .gridRow (0)

                    (ScrollView(
                        (VStack(spacing = 8.) {
                            disclosureText
                                model.appTheme
                                "Speak2Docs uses OpenAI to answer questions about selected documents."

                            disclosureMutedText
                                model.appTheme
                                "Sent to OpenAI: microphone audio, transcripts, prompts, selected passages, and optional PDF visual crops."

                            disclosureMutedText
                                model.appTheme
                                "OpenAI is the third-party AI service used for processing."

                            disclosureMutedText
                                model.appTheme
                                "Documents and indexes stay on this device except selected context needed for answers."

                            Grid([ Dimension.Star; Dimension.Star ], [ Dimension.Absolute 42. ]) {
                                (disclosureLink "Privacy Policy" PrivacyPolicy).gridColumn (0)
                                (disclosureLink "Terms of Use" TermsOfUse).gridColumn (1)
                            }

                            Grid([ Dimension.Absolute 42.; Dimension.Star ], [ Dimension.Auto ]) {
                                CheckBox(model.openAiDisclosureDoNotShowAgain, OpenAiDisclosureDoNotShowAgainChanged)
                                    .gridColumn(0)
                                    .centerVertical ()

                                Label("Do not show again before connecting")
                                    .font(size = 12.)
                                    .textColor(Theme.textColor model.appTheme)
                                    .lineBreakMode(LineBreakMode.WordWrap)
                                    .gridColumn(1)
                                    .centerVertical ()
                            }
                        })
                    ))
                        .verticalScrollBarVisibility(ScrollBarVisibility.Always)
                        .gridRow (1)

                    (Grid([ Dimension.Star; Dimension.Star ], [ Dimension.Absolute 46. ]) {
                        Button("Dismiss", OpenAiDisclosureDismissed)
                            .font(size = 14., attributes = FontAttributes.Bold)
                            .background(Colors.Transparent)
                            .textColor(Theme.textColor model.appTheme)
                            .gridColumn (0)

                        Button("Acknowledge", OpenAiDisclosureAcknowledged)
                            .font(size = 14., attributes = FontAttributes.Bold)
                            .background(Colors.DarkMagenta)
                            .textColor(Colors.White)
                            .cornerRadius(8)
                            .gridColumn (1)
                    })
                        .gridRow (2)
                })
                    .padding (16.)
            )
                .background(Theme.pageBackgroundColor model.appTheme)
                .stroke(SolidColorBrush(Theme.borderColor model.appTheme))
                .strokeThickness(1.)
                .strokeShape(RoundRectangle(CornerRadius(8.)))
                .width(popupWidth)
                .height(popupHeight)
                .centerHorizontal()
                .centerVertical ()
        })
            .background (Color.FromArgb("#99000000"))

    let private logView model =
        let logItems =
            model.log
            |> ActivityLog.visible model.activityLogVerbosity
            |> List.map (fun text ->
                {| text = text
                   fontSize = model.logFontSize |})

        Border(
            (Grid(
                [ Dimension.Star
                  Dimension.Absolute 42.
                  Dimension.Absolute 42.
                  Dimension.Absolute 46. ],
                [ Dimension.Absolute 44.; Dimension.Star ]
            ) {
                Label($"Activity ({ActivityLog.displayName model.activityLogVerbosity})")
                    .font(size = 15., attributes = FontAttributes.Bold)
                    .centerVertical()
                    .gridColumn(0)
                    .gridRow (0)

                (ViewControls.compactIconButton Icons.remove LogFont_Decrease)
                    .isEnabled(model.logFontSize > 10.)
                    .gridColumn(1)
                    .gridRow (0)

                (ViewControls.compactIconButton Icons.add LogFont_Increase)
                    .isEnabled(model.logFontSize < 22.)
                    .gridColumn(2)
                    .gridRow (0)

                (ViewControls.compactIconButton Icons.clear Log_Clear).gridColumn(3).gridRow (0)

                (CollectionView (logItems) (fun item ->
                    Label(item.text).font(size = item.fontSize).lineBreakMode(LineBreakMode.WordWrap).padding (0., 4.)))
                    .gridColumnSpan(4)
                    .gridRow (1)
            })
                .padding (10.)
        )
            .stroke(SolidColorBrush(Theme.borderColor model.appTheme))
            .strokeThickness(1.)
            .strokeShape (RoundRectangle(CornerRadius(8.)))

    let contentPage (model: Model) =
        ContentPage(
            (Grid(
                [ Dimension.Star ],
                [ Dimension.Absolute 54.
                  Dimension.Auto
                  Dimension.Absolute 230.
                  Dimension.Star
                  Dimension.Auto ]
            ) {
                (ToolbarView.main model).gridRow (0)

                match model.notification with
                | Some notification -> (notificationView notification).gridRow (1)
                | None -> ()

                (PdfSourcesView.view model).gridRow (2)

                (logView model).gridRow (3)

                (aiDisclaimerView model).gridRow (4)

                match model.openAiDisclosure with
                | Some mode -> (openAiDisclosureOverlay model mode).gridRowSpan (5)
                | None -> ()
            })
                .padding (18.)
        )
            .background(Theme.pageBackgroundColor model.appTheme)
            .title("Realtime QA over PDFs")
            .onSizeAllocated (fun args -> MainPageSizeAllocated(args.Width, args.Height))
